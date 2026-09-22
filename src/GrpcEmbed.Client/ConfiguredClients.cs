using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrpcEmbed.Client;

/// <summary>Configures existing named HTTP clients using runtime gRPC proxies.</summary>
public static class GrpcEmbedConfiguredClientsExtensions
{
    /// <summary>Call once, after all existing HTTP API client registrations.</summary>
    public static IServiceCollection AddConfiguredGrpcEmbedClients<TMarker>(this IServiceCollection services, IConfiguration configuration, Func<TMarker, string> getClientName) where TMarker : class
    {
        services.AddGrpcEmbedClients<TMarker>((provider, original) =>
        {
            var name = getClientName(original);
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A registered client must have a configuration name.");
            var section = configuration.GetSection(name);
            var enabled = section.GetValue<bool?>("Grpc:Enabled")
                ?? configuration.GetValue<bool>("GrpcEmbed:Clients:Enabled");
            if (!enabled) return null;
            var serviceName = section["Grpc:ServiceName"];
            var contract = new GrpcEmbedClientContractOptions();
            configuration.GetSection("GrpcEmbed:Clients:Contract").Bind(contract);
            section.GetSection("Grpc:Contract").Bind(contract);
            if (string.IsNullOrWhiteSpace(serviceName) && contract.Fetch == GrpcEmbedContractFetch.Never)
                throw new InvalidOperationException($"{name}:Grpc:ServiceName is required when contract fetching is disabled.");
            serviceName = string.IsNullOrWhiteSpace(serviceName) ? null : serviceName;

            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            try
            {
                var baseAddress = http.BaseAddress ??
                    throw new InvalidOperationException($"{name} has no HTTP BaseAddress.");
                var configuredAddress = section["Grpc:Address"];
                if (configuredAddress is null && baseAddress.AbsolutePath != "/")
                    throw new InvalidOperationException($"{name}:Grpc:Address is required when REST BaseAddress contains a path.");
                var address = configuredAddress is null ? baseAddress : new Uri(configuredAddress, UriKind.Absolute);
                RequireSecureAddress(address);
                var options = new GrpcEmbedClientOptions
                {
                    Address = address,
                    HttpClient = http,
                    DisposeHttpClient = true,
                    ServiceName = serviceName,
                    Contract = contract,
                    DefaultTimeout = http.Timeout == Timeout.InfiniteTimeSpan ? null : http.Timeout,
                };
                return options;
            }
            catch { http.Dispose(); throw; }
        });
        services.AddHostedService<ClientWarmup>();
        return services;
    }

    private static void RequireSecureAddress(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps &&
            !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
            throw new InvalidOperationException("Non-loopback GrpcEmbed endpoints require HTTPS.");
    }

    private sealed class ClientWarmup : IHostedService
    {
        private readonly IServiceProvider _services;
        public ClientWarmup(IServiceProvider services) => _services = services;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _services.InitializeGrpcEmbedClients();
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
