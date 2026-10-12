using System.Net;
using System.Text.Json;
using Grpc.Core;
using GrpcEmbed.Client;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ClientHotPathTests
{
    [Theory]
    [InlineData("123", "api/v{version}/clients/{id}", null, "/api/v1/clients/123")]
    [InlineData("a/b ?#%", "api/v{version}/clients/{id}", null, "/api/v1/clients/a%2Fb%20%3F%23%25")]
    [InlineData(null, "api/v{version}/clients/{id=42}", null, "/api/v1/clients/42")]
    [InlineData(null, "api/v{version}/clients/{id?}", null, "/api/v1/clients")]
    [InlineData("a/b", "api/v{version}/clients/{**id}", null, "/api/v1/clients/a/b")]
    [InlineData("123", "api/v{version}/clients/{id}", "/mount", "/mount/api/v1/clients/123")]
    public void Bound_routes_preserve_escaping_defaults_optional_and_mount(string? value, string template, string? mount, string expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { template, parameters = new { id = "key" } }));
        var route = ClientRoute.Parse(json.RootElement);
        var options = new GrpcEmbedClientOptions { Address = new Uri("http://localhost/api/v1") };
        options.Routing.Mode = GrpcEmbedRoutingMode.Rest;
        options.Routing.PathBase = mount;
        if (mount is not null) options.Routing.Values["version"] = "1";
        var parameters = typeof(ClientHotPathTests).GetMethod(nameof(Arguments), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetParameters();
        var path = ClientRoute.Bind(options, route, "GrpcEmbed.Client", "Get", parameters);
        Assert.Equal(expected, path(new object?[] { value }));
        Assert.Equal(expected, ClientRoute.Build(options, route, "GrpcEmbed.Client", "Get", parameters, new object?[] { value }));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData(null)]
    public void Bound_routes_still_reject_unsafe_or_missing_values(string? value)
    {
        using var json = JsonDocument.Parse("{\"template\":\"clients/{id}\",\"parameters\":{\"id\":\"key\"}}");
        var options = new GrpcEmbedClientOptions { Address = new Uri("http://localhost/") };
        options.Routing.Mode = GrpcEmbedRoutingMode.Rest;
        var path = ClientRoute.Bind(options, ClientRoute.Parse(json.RootElement), "GrpcEmbed.Client", "Get", typeof(ClientHotPathTests).GetMethod(nameof(Arguments), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetParameters());
        Assert.Throws<InvalidOperationException>(() => path(new object?[] { value }));
    }

    [Fact]
    public async Task Prepared_contract_is_reused_but_replaced_after_refresh_and_revalidates_shapes()
    {
        var handler = new ManifestHandler();
        using var http = new HttpClient(handler);
        var options = new GrpcEmbedClientOptions { Address = new Uri("http://localhost"), HttpClient = http };
        options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
        var state = new ClientContractState(options);
        var first = await state.PrepareAsync("GrpcEmbed.Client", "Get", "rq", "rs", default);
        var next = await state.PrepareAsync("GrpcEmbed.Client", "Get", "rq", "rs", default);
        Assert.Same(first, next);
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<RpcException>(async () => await state.PrepareAsync("GrpcEmbed.Client", "Get", "wrong", "rs", default));
        handler.Hash = new string('b', 64);
        await state.RefreshAsync(first!.Hash, default);
        var changed = await state.PrepareAsync("GrpcEmbed.Client", "Get", "rq", "rs", default);
        Assert.NotSame(first, changed);
        Assert.Equal(handler.Hash, changed!.Hash);
        Assert.Equal(2, handler.Calls);
    }

    private static void Arguments(string? key) { }

    [Fact]
    public async Task Cold_contract_fetch_observes_call_deadline_and_cancels_underlying_io()
    {
        var handler = new DelayedManifestHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new GrpcEmbedClientOptions { Address = new Uri("http://localhost"), HttpClient = http };
        options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
        var state = new ClientContractState(options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await state.PrepareAsync("GrpcEmbed.Client", "Get", "rq", "rs", default, DateTime.UtcNow.AddMilliseconds(100)));
        Assert.True(handler.Cancelled);
    }

    [Fact]
    public async Task Expired_refresh_deadline_keeps_snapshot_and_does_not_start_new_io()
    {
        var handler = new ManifestHandler();
        using var http = new HttpClient(handler);
        var options = new GrpcEmbedClientOptions { Address = new Uri("http://localhost"), HttpClient = http };
        options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
        var state = new ClientContractState(options);
        var first = await state.PrepareAsync("GrpcEmbed.Client", "Get", "rq", "rs", default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.RefreshAsync(first!.Hash, default, DateTime.UtcNow.AddSeconds(-1)));
        Assert.Equal(1, handler.Calls);
        Assert.Same(first, await state.PrepareAsync("GrpcEmbed.Client", "Get", "rq", "rs", default));
    }

    private sealed class DelayedManifestHandler : HttpMessageHandler
    {
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Cancelled = token.IsCancellationRequested; throw; }
            throw new InvalidOperationException("Unexpected completion");
        }
    }

    private sealed class ManifestHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Hash { get; set; } = new('a', 64);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                schemaHash = Hash,
                services = new { Client = new { methods = new { Get = new { requestShapeHash = "rq", responseShapeHash = "rs" } } } }
            })) });
        }
    }
}
