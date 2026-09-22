using System.Net;
using System.Text.Json;
using Grpc.Core;

namespace GrpcEmbed.Client;

public enum GrpcEmbedContractFetch { Never, Startup, FirstCall }
public enum GrpcEmbedContractMismatch { Fail, RefreshAndRetry }

public sealed class GrpcEmbedClientContractOptions
{
    public GrpcEmbedContractFetch Fetch { get; set; } = GrpcEmbedContractFetch.Never;
    public bool SendHash { get; set; }
    public GrpcEmbedContractMismatch OnMismatch { get; set; } = GrpcEmbedContractMismatch.Fail;
    public int MaxRetries { get; set; } = 1;
    public TimeSpan? RefreshInterval { get; set; }
    public Uri? Address { get; set; }
}

internal sealed class ClientContractState
{
    internal sealed record PreparedContract(string Hash, string Service, ClientRoute? Route);
    private sealed record Operation(string Request, string Response, ClientRoute? Route);
    private sealed record Snapshot(string Hash, Dictionary<string, Operation> Operations, DateTime Loaded);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly GrpcEmbedClientOptions _options;
    private volatile Snapshot? _snapshot;

    public ClientContractState(GrpcEmbedClientOptions options)
    {
        _options = options;
        if (!Enum.IsDefined(typeof(GrpcEmbedRoutingMode), options.Routing.Mode))
            throw new InvalidOperationException("Unknown client routing mode.");
        if (options.Routing.Mode != GrpcEmbedRoutingMode.Native && options.Contract.Fetch == GrpcEmbedContractFetch.Never)
            throw new InvalidOperationException("URL routing requires contract fetching.");
        var policy = options.Contract;
        if (!Enum.IsDefined(typeof(GrpcEmbedContractFetch), policy.Fetch) ||
            !Enum.IsDefined(typeof(GrpcEmbedContractMismatch), policy.OnMismatch))
            throw new InvalidOperationException("Unknown contract fetch or mismatch mode.");
        if (policy.MaxRetries is < 0 or > 5)
            throw new InvalidOperationException("Contract MaxRetries must be between 0 and 5.");
        if (policy.RefreshInterval is { } interval && interval <= TimeSpan.Zero)
            throw new InvalidOperationException("Contract RefreshInterval must be positive.");
        if (policy.Fetch == GrpcEmbedContractFetch.Never &&
            (policy.SendHash || policy.RefreshInterval is not null || policy.OnMismatch == GrpcEmbedContractMismatch.RefreshAndRetry))
            throw new InvalidOperationException("Hash validation and refresh require contract fetching.");
        if (policy.Fetch != GrpcEmbedContractFetch.Never && options.HttpClient is null)
            throw new InvalidOperationException("Contract fetching requires a configured HttpClient.");
        if (policy.OnMismatch == GrpcEmbedContractMismatch.RefreshAndRetry && !policy.SendHash)
            throw new InvalidOperationException("RefreshAndRetry requires SendHash.");
    }

    public async Task<PreparedContract?> PrepareAsync(string? service, string method, string requestShape, string responseShape, CancellationToken token)
    {
        if (_options.Contract.Fetch == GrpcEmbedContractFetch.Never) return null;
        var snapshot = await LoadAsync(false, null, token).ConfigureAwait(false);
        if (service is null)
        {
            var named = snapshot.Operations.Where(item => item.Key.EndsWith("/" + method, StringComparison.Ordinal)).ToArray();
            var matching = named.Where(item => item.Value.Request == requestShape && item.Value.Response == responseShape).ToArray();
            var selected = named.Length == 1 ? named : matching;
            if (selected.Length != 1)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Cannot unambiguously resolve {method} in the server contract; configure Grpc.ServiceName."));
            service = selected[0].Key[..selected[0].Key.LastIndexOf('/')];
        }
        var key = service + "/" + method;
        if (!snapshot.Operations.TryGetValue(key, out var operation))
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Contract does not publish {key}."));
        if (operation.Request != requestShape || operation.Response != responseShape)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Local models do not match the contract for {key}; the operation was not sent."));
        return new PreparedContract(snapshot.Hash, service, operation.Route);
    }

    public async Task PreloadAsync(CancellationToken token) => _ = await LoadAsync(false, null, token).ConfigureAwait(false);
    public async Task RefreshAsync(string? previousHash, CancellationToken token)
        => _ = await LoadAsync(true, previousHash, token).ConfigureAwait(false);

    private async Task<Snapshot> LoadAsync(bool force, string? previousHash, CancellationToken token)
    {
        bool Fresh(Snapshot? value) => value is not null &&
            (force ? value.Hash != previousHash :
                _options.Contract.RefreshInterval is not { } interval || DateTime.UtcNow - value.Loaded < interval);
        if (Fresh(_snapshot)) return _snapshot!;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Fresh(_snapshot)) return _snapshot!;
            var address = _options.Contract.Address ?? new Uri(_options.Address, "/_grpcembed/schema.json");
            using var request = new HttpRequestMessage(HttpMethod.Get, address)
            {
                Version = _options.HttpClient!.DefaultRequestVersion,
                VersionPolicy = _options.HttpClient.DefaultVersionPolicy,
            };
            if (_snapshot is { } existing) request.Headers.TryAddWithoutValidation("If-None-Match", "\"" + existing.Hash + "\"");
            // Per-call metadata is never stored in the shared contract snapshot.
            foreach (var entry in _options.MetadataFactory?.Invoke() ?? new Metadata())
                if (!entry.IsBinary && !entry.Key.StartsWith("grpc", StringComparison.Ordinal))
                    request.Headers.TryAddWithoutValidation(entry.Key, entry.Value);
            using var response = await _options.HttpClient!.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && _snapshot is { } old)
                return _snapshot = old with { Loaded = DateTime.UtcNow };
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (_options.Routing.Mode != GrpcEmbedRoutingMode.Native)
            {
                if (!json.RootElement.TryGetProperty("routingMode", out var mode) || mode.GetString() != _options.Routing.Mode.ToString())
                    throw new InvalidOperationException("Client and server routing modes differ; the operation was not sent.");
                if (_options.Routing.Mode != GrpcEmbedRoutingMode.Rest &&
                    (!json.RootElement.TryGetProperty("routingPrefix", out var prefix) || prefix.GetString()?.Trim('/') != _options.Routing.Prefix.Trim('/')))
                    throw new InvalidOperationException("Client and server routing prefixes differ; the operation was not sent.");
            }
            var hash = json.RootElement.GetProperty("schemaHash").GetString();
            if (hash is null || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidOperationException("The server contract does not contain a valid SHA-256 hash.");
            if (response.Headers.TryGetValues(GrpcEmbedContractHeaders.ServerHash, out var headers) &&
                headers.Single() != hash)
                throw new InvalidOperationException("Contract body and response hash disagree.");
            var operations = new Dictionary<string, Operation>(StringComparer.Ordinal);
            foreach (var service in json.RootElement.GetProperty("services").EnumerateObject())
            foreach (var method in service.Value.GetProperty("methods").EnumerateObject())
                operations.Add("GrpcEmbed." + service.Name + "/" + method.Name, new Operation(
                    method.Value.GetProperty("requestShapeHash").GetString() ?? "",
                    method.Value.GetProperty("responseShapeHash").GetString() ?? "",
                    method.Value.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.Object ? ClientRoute.Parse(route) : null));
            return _snapshot = new Snapshot(hash, operations, DateTime.UtcNow);
        }
        finally { _gate.Release(); }
    }
}

internal static class ContractShapeCache<T>
{
    public static readonly string Hash = GrpcEmbedContractShape.Hash(typeof(T));
}

internal static class ResolvedContractMethods<TRequest, TResponse>
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Method<TRequest, TResponse>> Methods = new();
    public static Method<TRequest, TResponse> Get(Method<TRequest, TResponse> template, string service)
        => Methods.GetOrAdd(service + "/" + template.Name, _ => new Method<TRequest, TResponse>(
            template.Type, service, template.Name, template.RequestMarshaller, template.ResponseMarshaller));
}
