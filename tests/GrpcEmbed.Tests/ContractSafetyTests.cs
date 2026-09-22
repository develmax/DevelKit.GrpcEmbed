using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Grpc.Core;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ContractSafetyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ContractSafetyTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> Server(GrpcEmbedContractValidation validation) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<GrpcEmbedOptions>(options => options.Contract.Validation = validation)));

    [Theory]
    [InlineData(null)]
    [InlineData("old-contract")]
    public async Task Required_hash_rejects_even_malformed_body_before_action(string? hash)
    {
        using var server = Server(GrpcEmbedContractValidation.Required);
        using var http = server.CreateClient();
        UsersController.FilterCount = 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/GrpcEmbed.Users/Get")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(new byte[] { 0, 0, 0, 0, 1, 128 }),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        if (hash is not null) request.Headers.Add(GrpcEmbedContractHeaders.RequestHash, hash);
        using var response = await http.SendAsync(request);
        Assert.Equal("9", response.Headers.GetValues("grpc-status").Single());
        Assert.Equal(GrpcEmbedContractHeaders.NotExecuted,
            response.Headers.GetValues(GrpcEmbedContractHeaders.Rejection).Single());
        Assert.Equal(0, UsersController.FilterCount);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Stale_contract_can_retry_a_write_only_after_pre_execution_rejection(bool alwaysStale, int executions)
    {
        using var server = Server(GrpcEmbedContractValidation.Required);
        using var handler = new ContractHandler { InnerHandler = server.Server.CreateHandler(), AlwaysStale = alwaysStale };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IWriteClient>(options =>
        {
            options.Address = http.BaseAddress;
            options.HttpClient = http;
            options.ServiceName = "Users";
            options.DefaultTimeout = TimeSpan.FromSeconds(5);
            // No service name: resolve it from the fetched contract, not a client-type convention.
            options.ServiceName = null;
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
            options.Contract.SendHash = true;
            options.Contract.OnMismatch = GrpcEmbedContractMismatch.RefreshAndRetry;
            options.Contract.MaxRetries = 1;
        });
        using var provider = services.BuildServiceProvider();
        provider.InitializeGrpcEmbedClients();
        Assert.Equal(0, handler.SchemaCalls);
        UsersController.CreateInvocationCount = 0;
        var client = provider.GetRequiredService<IWriteClient>();
        if (alwaysStale)
        {
            var error = await Assert.ThrowsAsync<RpcException>(() => client.CreateAsync(new CreateUserRequest { Name = "safe" }));
            Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        }
        else Assert.Equal("safe", (await client.CreateAsync(new CreateUserRequest { Name = "safe" })).Name);
        Assert.Equal(2, handler.SchemaCalls);
        Assert.Equal(2, handler.RpcCalls);
        Assert.Equal(executions, UsersController.CreateInvocationCount);
    }

    [Fact]
    public async Task Changed_message_shape_is_not_blessed_with_a_new_hash()
    {
        using var server = Server(GrpcEmbedContractValidation.Required);
        using var handler = new ContractHandler { InnerHandler = server.Server.CreateHandler(), Incompatible = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IWriteClient>(options =>
        {
            options.Address = http.BaseAddress;
            options.HttpClient = http;
            options.ServiceName = "Users";
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
            options.Contract.SendHash = true;
        });
        using var provider = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            provider.GetRequiredService<IWriteClient>().CreateAsync(new CreateUserRequest { Name = "unsafe" }));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal(0, handler.RpcCalls);
    }

    [Fact]
    public async Task Contract_endpoint_supports_conditional_requests()
    {
        using var http = _factory.CreateClient();
        using var first = await http.GetAsync("/_grpcembed/schema.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_grpcembed/schema.json");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(GrpcEmbedContractValidation.Disabled, null, true)]
    [InlineData(GrpcEmbedContractValidation.Disabled, "old", true)]
    [InlineData(GrpcEmbedContractValidation.IfPresent, null, true)]
    [InlineData(GrpcEmbedContractValidation.IfPresent, "old", false)]
    public async Task Validation_modes_are_independent(GrpcEmbedContractValidation validation, string? hash, bool allowed)
    {
        using var server = Server(validation);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost",
            new Grpc.Net.Client.GrpcChannelOptions { HttpHandler = server.Server.CreateHandler() });
        var client = new Generated.Users.UsersClient(channel);
        var headers = new Metadata();
        if (hash is not null) headers.Add(GrpcEmbedContractHeaders.RequestHash, hash);
        if (allowed) Assert.Equal(20, (await client.GetAsync(new Generated.Users_Get_Request { Id = 20 }, headers)).Id);
        else Assert.Equal(StatusCode.FailedPrecondition,
            (await Assert.ThrowsAsync<RpcException>(async () => await client.GetAsync(new Generated.Users_Get_Request { Id = 20 }, headers))).StatusCode);
    }

    [Fact]
    public async Task Disabled_contract_has_no_schema_endpoint_or_hash_headers()
    {
        using var server = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<GrpcEmbedOptions>(options =>
            {
                options.Contract.Enabled = false;
                options.EnableReflection = false;
            })));
        using var http = server.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_grpcembed/schema.json")).StatusCode);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(http.BaseAddress!,
            new Grpc.Net.Client.GrpcChannelOptions { HttpClient = http });
        using var call = new Generated.Users.UsersClient(channel).GetAsync(new Generated.Users_Get_Request { Id = 1 });
        Assert.Null((await call.ResponseHeadersAsync).GetValue(GrpcEmbedContractHeaders.ServerHash));
        Assert.Equal(1, (await call.ResponseAsync).Id);
    }

    [Fact]
    public async Task Ordinary_failed_precondition_does_not_authorize_a_retry()
    {
        using var server = Server(GrpcEmbedContractValidation.Required);
        using var handler = new ContractHandler { InnerHandler = server.Server.CreateHandler(), OrdinaryFailure = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IWriteClient>(options =>
        {
            options.Address = http.BaseAddress;
            options.HttpClient = http;
            options.ServiceName = "Users";
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
            options.Contract.SendHash = true;
            options.Contract.OnMismatch = GrpcEmbedContractMismatch.RefreshAndRetry;
        });
        using var provider = services.BuildServiceProvider();
        await Assert.ThrowsAsync<RpcException>(() =>
            provider.GetRequiredService<IWriteClient>().CreateAsync(new CreateUserRequest { Name = "one attempt" }));
        Assert.Equal(1, handler.RpcCalls);
        Assert.Equal(1, handler.SchemaCalls);
    }

    [Fact]
    public async Task Cancellation_during_refresh_prevents_the_write_retry()
    {
        using var server = Server(GrpcEmbedContractValidation.Required);
        using var handler = new ContractHandler { InnerHandler = server.Server.CreateHandler(), BlockRefresh = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<ICancellableWriteClient>(options =>
        {
            options.Address = http.BaseAddress;
            options.HttpClient = http;
            options.ServiceName = "Users";
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
            options.Contract.SendHash = true;
            options.Contract.OnMismatch = GrpcEmbedContractMismatch.RefreshAndRetry;
        });
        using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var call = provider.GetRequiredService<ICancellableWriteClient>()
            .CreateAsync(new CreateUserRequest { Name = "cancelled" }, cancellation.Token);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, handler.RpcCalls);
    }

    public interface ICancellableWriteClient
    {
        Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);
    }

    public interface IWriteClient
    {
        Task<UserDto> CreateAsync(CreateUserRequest request);
    }

    private sealed class ContractHandler : DelegatingHandler
    {
        public bool AlwaysStale { get; init; }
        public bool Incompatible { get; init; }
        public bool OrdinaryFailure { get; init; }
        public bool BlockRefresh { get; init; }
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SchemaCalls { get; private set; }
        public int RpcCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var schema = request.RequestUri!.AbsolutePath.EndsWith("schema.json", StringComparison.Ordinal);
            if (schema) SchemaCalls++; else RpcCalls++;
            if (schema && SchemaCalls == 2 && BlockRefresh)
            {
                RefreshEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (!schema && OrdinaryFailure)
            {
                var rejected = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = HttpVersion.Version20,
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                rejected.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
                rejected.Headers.Add("grpc-status", "9");
                return rejected;
            }
            var response = await base.SendAsync(request, token);
            if (schema && (SchemaCalls == 1 || AlwaysStale || Incompatible))
            {
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                var stale = new string('0', 64);
                json["schemaHash"] = stale;
                if (Incompatible) json["services"]!["Users"]!["methods"]!["Create"]!["requestShapeHash"] = stale;
                response.Content.Dispose();
                response.Content = new StringContent(json.ToJsonString());
                response.Headers.Remove(GrpcEmbedContractHeaders.ServerHash);
                response.Headers.Add(GrpcEmbedContractHeaders.ServerHash, stale);
                response.Headers.ETag = new EntityTagHeaderValue("\"" + stale + "\"");
            }
            return response;
        }
    }
}
