using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RuntimeClientTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public RuntimeClientTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Existing_async_overloads_use_one_proxy_and_existing_http_client()
    {
        using var http = _factory.CreateClient();
        var services = new ServiceCollection();
        var configurations = 0;
        services.AddSingleton(http);
        services.AddGrpcEmbedClient<IExistingClient>((provider, options) =>
        {
            configurations++;
            options.Address = http.BaseAddress!;
            options.ServiceName = "Users";
            options.HttpClient = provider.GetRequiredService<HttpClient>();
        });
        using (var provider = services.BuildServiceProvider())
        {
            provider.InitializeGrpcEmbedClients();
            var client = provider.GetRequiredService<IExistingClient>();
            Assert.Same(client, provider.GetRequiredService<IExistingClient>());
            Assert.Equal(31, (await client.GetAsync(31)).Id);
            Assert.Equal(32, (await client.GetAsync(32, CancellationToken.None)).Id);
            Assert.Equal(1, configurations);
        }
        // Disposing the gRPC channel must not dispose the application's HTTP client.
        Assert.True((await http.GetAsync("/api/users/33")).IsSuccessStatusCode);
    }

    [Fact]
    public void Startup_initialization_rejects_ambiguous_operations()
    {
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IAmbiguousClient>(options => options.Address = new Uri("http://localhost"));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<NotSupportedException>(() => provider.InitializeGrpcEmbedClients());
    }

    [Fact]
    public async Task Completion_only_methods_support_task_value_task_and_no_content()
    {
        using var http = _factory.CreateClient();
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<ICompletionClient>(options =>
        {
            options.Address = http.BaseAddress!;
            options.HttpClient = http;
            options.ServiceName = "Completion";
        });
        using var provider = services.BuildServiceProvider();
        provider.InitializeGrpcEmbedClients();
        var client = provider.GetRequiredService<ICompletionClient>();
        await client.CompleteAsync(default);
        await client.CompleteValueAsync();
        await client.CompleteResultAsync();
        Assert.True((await http.PostAsync("/api/completion/task", null)).IsSuccessStatusCode);
    }

    public interface ICompletionClient
    {
        Task CompleteAsync(CancellationToken cancellationToken);
        ValueTask CompleteValueAsync();
        Task CompleteResultAsync();
    }

    public interface IBaseClient
    {
        Task<UserResult> GetAsync(int id);
    }

    public interface IExistingClient : IBaseClient
    {
        Task<UserResult> GetAsync(int id, CancellationToken cancellationToken);
    }

    public interface IAmbiguousClient
    {
        Task<UserResult> GetAsync(int id);
        Task<UserResult> GetAsync(string id);
    }

    public sealed class UserResult
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
