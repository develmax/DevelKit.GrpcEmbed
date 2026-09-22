using GrpcEmbed.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ClientOwnershipAndSecurityTests
{
    [Theory]
    [InlineData("http://remote.example/schema.json", false)]
    [InlineData("//remote.example/schema.json", false)]
    [InlineData("https://remote.example/schema.json", true)]
    [InlineData("http://localhost/schema.json", true)]
    [InlineData("/_grpcembed/schema.json", true)]
    public void Configured_contract_address_is_validated_before_network_access(string address, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GrpcEmbed:Clients:Enabled"] = "true",
            ["Existing:Grpc:Address"] = "https://grpc.example/",
            ["Existing:Grpc:Contract:Address"] = address,
        }).Build();
        var services = new ServiceCollection();
        // A scheme-relative contract inherits HTTP from the named client's base URL.
        services.AddHttpClient("Existing", http => http.BaseAddress = new Uri("http://localhost/api/"));
        services.AddSingleton<ITestClient>(_ => new AsyncClient());
        services.AddConfiguredGrpcEmbedClients<IMarker>(configuration, _ => "Existing");
        using var provider = services.BuildServiceProvider();
        if (allowed) Assert.NotNull(provider.GetRequiredService<ITestClient>());
        else Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ITestClient>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Async_only_original_is_disposed_once(bool grpc, bool synchronous)
    {
        var original = new AsyncClient();
        var services = new ServiceCollection();
        services.AddSingleton<ITestClient>(_ => original);
        services.AddGrpcEmbedClients<IMarker>((_, _) => grpc ? new GrpcEmbedClientOptions
        { Address = new Uri("https://localhost"), ServiceName = "Test" } : null);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ITestClient>();
        if (synchronous) provider.Dispose();
        else await provider.DisposeAsync();
        await provider.DisposeAsync();
        Assert.Equal(1, original.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Async_only_original_is_disposed_when_initialization_fails(bool constructorFailure)
    {
        var original = new AsyncClient();
        var services = new ServiceCollection();
        services.AddSingleton<ITestClient>(_ => original);
        services.AddGrpcEmbedClients<IMarker>((_, _) => constructorFailure
            ? new GrpcEmbedClientOptions { Address = null!, ServiceName = "Test" }
            : throw new InvalidOperationException("Configuration failed"));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ITestClient>());
        Assert.Equal(1, original.Disposals);
    }

    public interface IMarker { }
    public interface ITestClient : IMarker { Task<int> GetAsync(int id); }
    private sealed class AsyncClient : ITestClient, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public Task<int> GetAsync(int id) => Task.FromResult(id);
        public async ValueTask DisposeAsync() { await Task.Yield(); Disposals++; }
    }
}
