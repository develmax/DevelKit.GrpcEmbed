using System.Collections.Concurrent;
using Grpc.Core;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class PayloadAuditTests
{
    [Fact]
    public async Task Decoded_request_and_response_are_exposed_without_reading_wire_streams()
    {
        var requests = new ConcurrentQueue<object?>();
        var responses = new ConcurrentQueue<object?>();
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<GrpcEmbedOptions>(options => options.Routing.Mode = GrpcEmbedRoutingMode.Rest);
            services.AddSingleton<IStartupFilter>(new AuditStartup(requests, responses));
        }));
        using var handler = new AuditHandler { InnerHandler = server.Server.CreateHandler() };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/api/users") };
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<RestRoutingTests.IUsers>(options =>
        {
            options.Address = http.BaseAddress; options.HttpClient = http;
            options.ServiceName = "Users"; options.Routing.Mode = GrpcEmbedRoutingMode.Rest;
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
            options.CaptureAuditPayloads = true;
        });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<RestRoutingTests.IUsers>();
        var dto = new CreateUserRequest { Name = "synthetic-audit-name" };
        var created = await client.CreateAsync(dto);
        Assert.Same(dto, handler.Requests.Single(x => x is CreateUserRequest));
        Assert.Same(created, handler.Responses.Single());
        Assert.Equal(dto.Name, Assert.IsType<CreateUserRequest>(requests.Single()).Name);
        Assert.Equal(created.Name, Assert.IsType<UserDto>(responses.Single()).Name);
        Assert.Null(GrpcEmbedClientPayloadAudit.Current);
        var values = await Task.WhenAll(Enumerable.Range(1, 20).Select(id => client.GetAsync(id, default)));
        Assert.Equal(Enumerable.Range(1, 20), values.Select(x => x.Id));
        Assert.Equal(21, handler.Completions); Assert.Equal(21, handler.Releases);
        Assert.Null(GrpcEmbedClientPayloadAudit.Current);
        Assert.All(handler.Statuses, status => Assert.Equal(0, status));
    }

    [Fact]
    public void Carrier_restores_parent_and_releases_once_after_callback_failure()
    {
        using var parent = new GrpcEmbedClientPayloadAudit("outer", null, "GET");
        var released = 0;
        var call = new GrpcEmbedClientPayloadAudit("inner", new object(), "POST")
        { Completed = (_, _) => throw new InvalidOperationException("synthetic"), Released = () => released++ };
        Assert.Throws<InvalidOperationException>(() => call.Complete(new object(), 0));
        call.Dispose(); call.Dispose();
        Assert.Equal(1, released); Assert.Same(parent, GrpcEmbedClientPayloadAudit.Current);
    }

    private sealed class AuditHandler : DelegatingHandler
    {
        public ConcurrentQueue<object?> Requests { get; } = new();
        public ConcurrentQueue<object?> Responses { get; } = new();
        public ConcurrentQueue<int> Statuses { get; } = new();
        public int Completions, Releases;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Content?.Headers.ContentType?.MediaType == "application/grpc")
            {
                var audit = GrpcEmbedClientPayloadAudit.Current;
                Assert.NotNull(audit);
                Requests.Enqueue(audit.RequestBody);
                audit.Completed = (value, status) => { Responses.Enqueue(value); Statuses.Enqueue(status); Interlocked.Increment(ref Completions); };
                audit.Released = () => Interlocked.Increment(ref Releases);
            }
            return base.SendAsync(request, token);
        }
    }

    private sealed class AuditStartup(ConcurrentQueue<object?> requests, ConcurrentQueue<object?> responses) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.ContentType == "application/grpc")
                {
                    context.Items[GrpcEmbedPayloadAuditContract.CaptureResponseKey] = true;
                    context.Items[GrpcEmbedPayloadAuditContract.RequestObserverKey] = new Action<object?, string?>((body, _) => requests.Enqueue(body));
                    await continuation();
                    if (context.Items.TryGetValue(GrpcEmbedPayloadAuditContract.ResponseKey, out var result)) responses.Enqueue(result);
                }
                else await continuation();
            });
            next(app);
        };
    }
}
