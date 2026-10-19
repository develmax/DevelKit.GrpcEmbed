using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GrpcEmbed.AspNetCore;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ConfiguredH2cTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opted_in_remote_named_client_fetches_contract_and_calls_over_real_h2c(bool customFactory)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listener => listener.Protocols = HttpProtocols.Http2));
        builder.Services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddGrpcEmbed(options =>
        {
            options.Routing.Mode = GrpcEmbedRoutingMode.Rest;
            options.EnableSchemaEndpoint = true;
        });
        await using var app = builder.Build();
        var requests = new ConcurrentQueue<(string Protocol, string Path)>();
        app.Use(async (context, next) =>
        {
            requests.Enqueue((context.Request.Protocol, context.Request.Path.Value!));
            await next(context);
        });
        app.MapGrpcEmbed();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StartAsync(timeout.Token);
        try
        {
            var endpoint = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Existing:Grpc:Enabled"] = "true",
                ["Existing:Grpc:AllowUnencryptedHttp2"] = "true",
                ["Existing:Grpc:Contract:Fetch"] = "FirstCall",
                ["Existing:Grpc:Contract:SendHash"] = "true",
            }).Build();
            var services = new ServiceCollection();
            SocketsHttpHandler CreateHandler() => new()
            {
                UseProxy = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            };
            var baseAddress = new Uri($"http://cluster.example:{endpoint.Port}/api/users");
            services.AddHttpClient("Existing", http =>
            {
                // Keep a non-loopback URI so the configured-client security gate is exercised.
                http.BaseAddress = baseAddress;
                Assert.Equal(HttpVersion.Version11, http.DefaultRequestVersion);
            }).ConfigurePrimaryHttpMessageHandler(() => CreateHandler());
            if (customFactory)
            {
                var factory = new BypassingFactory(() => CreateHandler(), baseAddress);
                services.AddSingleton<IHttpClientFactory>(factory);
                services.AddSingleton<IHttpMessageHandlerFactory>(factory);
            }
            services.AddSingleton<IUsers>(_ => new RestClient());
            services.AddConfiguredGrpcEmbedClients<IMarker>(configuration, _ => "Existing");
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IUsers>();
            Assert.Equal(42, (await client.GetAsync(42, timeout.Token)).Id);
            Assert.Equal(43, (await client.GetAsync(43, timeout.Token)).Id);
            Assert.Equal(1, requests.Count(request => request.Path == "/_grpcembed/schema.json"));
            Assert.Contains(requests, request => request.Path == "/api/users/42");
            Assert.Contains(requests, request => request.Path == "/api/users/43");
            Assert.All(requests, request => Assert.Equal("HTTP/2", request.Protocol));
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    public interface IMarker { }
    // Intentionally bypasses HttpClientFactoryOptions, despite implementing both interfaces.
    private sealed class BypassingFactory(Func<HttpMessageHandler> handler, Uri address) : IHttpClientFactory, IHttpMessageHandlerFactory
    {
        public HttpClient CreateClient(string name) => new(CreateHandler(name)) { BaseAddress = address };
        public HttpMessageHandler CreateHandler(string name) => handler();
    }
    public interface IUsers : IMarker { Task<UserDto> GetAsync(int id, CancellationToken cancellationToken); }
    private sealed class RestClient : IUsers
    {
        public Task<UserDto> GetAsync(int id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("REST must not be called when gRPC is selected.");
    }
}
