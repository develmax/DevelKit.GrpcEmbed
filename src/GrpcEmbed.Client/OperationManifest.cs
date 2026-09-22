using System.Reflection;
using System.Text.Json;

namespace GrpcEmbed.Client;

public sealed record GrpcEmbedOperation(string Service, string Method);

/// <summary>Resolves existing CLR methods against a startup snapshot of server operations.</summary>
public static class GrpcEmbedOperationManifest
{
    public static void UseOperationManifest(this GrpcEmbedClientOptions options, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The GrpcEmbed manifest must contain a services object.");
        var operations = new List<(GrpcEmbedOperation Operation, string[] Parameters)>();
        foreach (var service in services.EnumerateObject())
        foreach (var method in service.Value.GetProperty("methods").EnumerateObject())
        {
            var parameters = method.Value.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString()!).ToArray();
            operations.Add((new GrpcEmbedOperation(service.Name, method.Name), parameters));
        }
        var candidates = operations.ToArray();
        options.OperationResolver = method =>
        {
            var name = method.GetCustomAttribute<GrpcNameAttribute>()?.Name ??
                (options.StripAsyncSuffix && method.Name.EndsWith("Async", StringComparison.Ordinal)
                    ? method.Name[..^5] : method.Name);
            var serviceName = options.ServiceName ??
                method.DeclaringType?.GetCustomAttribute<GrpcNameAttribute>()?.Name;
            var parameters = method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .Select(parameter => parameter.Name!).ToArray();
            var matches = candidates.Where(candidate =>
                candidate.Operation.Method == name &&
                (serviceName is null || candidate.Operation.Service == serviceName) &&
                candidate.Parameters.SequenceEqual(parameters)).ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0].Operation,
                _ => throw new InvalidOperationException($"Ambiguous gRPC operation for {method}; specify a service identity."),
            };
        };
    }
}
