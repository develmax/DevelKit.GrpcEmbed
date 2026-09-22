using GrpcEmbed.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RegisteredClientsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public RegisteredClientsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Existing_registrations_choose_one_transport_and_keep_identity(
        bool global, bool? local, bool expectedGrpc)
    {
        using var http = _factory.CreateClient();
        var manifest = await http.GetStringAsync("/_grpcembed/schema.json");
        var values = new Dictionary<string, string?> { ["GrpcEmbed:Clients:Enabled"] = global.ToString() };
        if (local.HasValue) values["ExistingClient:Grpc:Enabled"] = local.Value.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        var creations = 0;
        RestClient? original = null;
        services.AddSingleton<IExistingClient>(_ => { creations++; return original = new RestClient(); });
        services.AddGrpcEmbedClients<IClientIdentity>((_, client) =>
        {
            var enabled = configuration.GetValue<bool?>(client.ApiName + ":Grpc:Enabled")
                ?? configuration.GetValue<bool>("GrpcEmbed:Clients:Enabled");
            if (!enabled) return null;
            var options = new GrpcEmbedClientOptions
            {
                Address = http.BaseAddress!,
                HttpClient = http,
            };
            options.UseOperationManifest(manifest);
            return options;
        });
        using (var provider = services.BuildServiceProvider())
        {
            provider.InitializeGrpcEmbedClients();
            var client = provider.GetRequiredService<IExistingClient>();
            Assert.Equal("ExistingClient", client.ApiName);
            Assert.Equal("stable-identity", client.ClientInstanceId);
            Assert.Equal(expectedGrpc ? 41 : -1, (await client.GetAsync(41)).Id);
            Assert.Equal(expectedGrpc ? 42 : -1, (await client.GetAsync(42, default)).Id);
            Assert.Equal(expectedGrpc ? 0 : 2, original!.Calls);
            if (expectedGrpc)
            {
                var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => client.NotPublishedAsync());
                Assert.Equal(Grpc.Core.StatusCode.Unimplemented, error.StatusCode);
                Assert.Equal(0, original.Calls);
            }
            Assert.Equal(1, creations);
            Assert.Same(client, provider.GetRequiredService<IExistingClient>());
        }
        Assert.Equal(1, original!.Disposals);
    }

    public interface IClientIdentity
    {
        string ApiName { get; }
        string ClientInstanceId { get; }
    }

    public interface IExistingClient : IClientIdentity
    {
        Task NotPublishedAsync();
        Task<RuntimeClientTests.UserResult> GetAsync(int id);
        Task<RuntimeClientTests.UserResult> GetAsync(int id, CancellationToken cancellationToken);
    }

    private sealed class RestClient : IExistingClient, IDisposable
    {
        public string ApiName => "ExistingClient";
        public string ClientInstanceId => "stable-identity";
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public Task NotPublishedAsync() { Calls++; return Task.CompletedTask; }
        public Task<RuntimeClientTests.UserResult> GetAsync(int id) => GetAsync(id, default);
        public Task<RuntimeClientTests.UserResult> GetAsync(int id, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new RuntimeClientTests.UserResult { Id = -1 });
        }
        public void Dispose() => Disposals++;
    }
}
