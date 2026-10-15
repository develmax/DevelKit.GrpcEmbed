using System.Net;
using System.Text.Json;
using Grpc.Core;
using GrpcEmbed.Client;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ContractResolutionAllocationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("GrpcEmbed.Target")]
    public async Task Bound_resolution_is_allocation_free_when_warm_and_invalidated_on_refresh(string? service)
    {
        using var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var state = Create(http);
        var prepare = state.Bind(service, "Get", "request", "response");
        var first = await prepare(default, null);
        for (var i = 0; i < 1000; i++) _ = await prepare(default, null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = await prepare(default, null);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(first, await prepare(default, null));
        handler.Hash = new string('b', 64);
        await state.RefreshAsync(first!.Hash, default);
        var changed = await prepare(default, null);
        Assert.NotSame(first, changed);
        Assert.Equal(handler.Hash, changed!.Hash);
        handler.Incompatible = true;
        handler.Hash = new string('c', 64);
        await state.RefreshAsync(changed.Hash, default);
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () =>
            await prepare(default, null))).StatusCode);
    }

    [Fact]
    public async Task Bound_automatic_resolution_rejects_new_ambiguity_and_supports_parallel_calls()
    {
        using var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var state = Create(http);
        var prepare = state.Bind(null, "Get", "request", "response");
        var first = await prepare(default, null);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            Assert.Same(first, await prepare(default, null)))));
        handler.Duplicate = true;
        handler.Hash = new string('b', 64);
        await state.RefreshAsync(first!.Hash, default);
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () =>
            await prepare(default, null))).StatusCode);
    }

    [Fact]
    public async Task Automatic_resolution_does_not_allocate_per_unrelated_operation()
    {
        using var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var state = Create(http);
        await state.PrepareAsync(null, "Get", "request", "response", default);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            var pending = state.PrepareAsync(null, "Get", "request", "response", default);
            Assert.True(pending.IsCompletedSuccessfully);
            Assert.Equal("GrpcEmbed.Target", (await pending)!.Service);
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 256 * 100);
    }

    [Theory]
    [InlineData(false, false, "GrpcEmbed.Target")]
    [InlineData(true, false, "GrpcEmbed.Other")]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    public async Task Automatic_resolution_preserves_shape_and_ambiguity_guards(bool incompatible, bool duplicate, string? expected)
    {
        using var handler = new SchemaHandler { Incompatible = incompatible, Duplicate = duplicate, IncludeOther = incompatible && !duplicate };
        using var http = new HttpClient(handler);
        var state = Create(http);
        if (expected is not null)
            Assert.Equal(expected, (await state.PrepareAsync(null, "Get", "request", "response", default))!.Service);
        else
            Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () =>
                await state.PrepareAsync(null, "Get", "request", "response", default))).StatusCode);
    }

    [Fact]
    public async Task Refresh_replaces_the_resolved_operation()
    {
        using var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var state = Create(http);
        var first = (await state.PrepareAsync(null, "Get", "request", "response", default))!;
        handler.Incompatible = true;
        handler.IncludeOther = true;
        handler.Hash = new string('b', 64);
        await state.RefreshAsync(first.Hash, default);
        var updated = (await state.PrepareAsync(null, "Get", "request", "response", default))!;
        Assert.Equal("GrpcEmbed.Other", updated.Service);
        Assert.Equal(handler.Hash, updated.Hash);
    }

    private static ClientContractState Create(HttpClient http) => new(new GrpcEmbedClientOptions
    {
        Address = new Uri("http://localhost"), HttpClient = http,
        Contract = new GrpcEmbedClientContractOptions { Fetch = GrpcEmbedContractFetch.FirstCall },
    });

    private sealed class SchemaHandler : HttpMessageHandler
    {
        public bool Incompatible, Duplicate, IncludeOther;
        public string Hash = new('a', 64);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object Method(string shape = "request") => new { requestShapeHash = shape, responseShapeHash = "response" };
            var methods = Enumerable.Range(0, 200).ToDictionary(i => "Unrelated" + i, _ => Method());
            methods.Add("Get", Method(Incompatible ? "old-request" : "request"));
            var services = new Dictionary<string, object> { ["Target"] = new { methods } };
            if (Duplicate || IncludeOther)
                services.Add("Other", new { methods = new Dictionary<string, object> { ["Get"] = Method(Duplicate && Incompatible ? "old-request" : "request") } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { schemaHash = Hash, services })),
            });
        }
    }
}
