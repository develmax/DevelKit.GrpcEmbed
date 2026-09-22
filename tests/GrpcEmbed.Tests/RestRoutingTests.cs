using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RestRoutingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("GrpcEmbed.Users/DoesNotExist")]
    public async Task Missing_or_unknown_operation_never_falls_back_to_rest_post(string? operation)
    {
        using var server = Server();
        using var http = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Version = HttpVersion.Version20,
            Content = new StringContent("{\"name\":\"must-not-execute\"}"),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        if (operation is not null) request.Headers.Add(GrpcEmbedContractHeaders.Operation, operation);
        using var response = await http.SendAsync(request);
        Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("must-not-execute", await response.Content.ReadAsStringAsync());
        Assert.True(response.StatusCode == HttpStatusCode.NotFound || response.Headers.Contains("grpc-status"));
    }

    [Fact]
    public void Method_mode_rejects_ambiguous_method_names_at_startup()
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<GrpcEmbedOptions>(options => options.Routing.Mode = GrpcEmbedRoutingMode.Method)));
        var error = Assert.Throws<InvalidOperationException>(() => server.CreateClient());
        Assert.Contains("Duplicate GrpcEmbed method route", error.Message);
    }
    [Theory]
    [InlineData(GrpcEmbedRoutingMode.Method, "/grpc/Get")]
    [InlineData(GrpcEmbedRoutingMode.ControllerMethod, "/grpc/Users/Get")]
    public async Task Alternative_routes_remain_configurable(GrpcEmbedRoutingMode mode, string path)
    {
        using var server = Server(mode);
        using var recorder = new RecordingHandler { InnerHandler = server.Server.CreateHandler() };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost") };
        using var provider = Client(http, mode);
        Assert.Equal(12, (await provider.GetRequiredService<IUsers>().GetAsync(12, default)).Id);
        Assert.Equal(path, recorder.LastRpcPath);
    }

    [Fact]
    public async Task Parallel_urls_do_not_leak_parameters_or_private_headers()
    {
        using var server = Server();
        using var recorder = new RecordingHandler { InnerHandler = server.Server.CreateHandler() };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost/api/users") };
        using var provider = Client(http);
        var client = provider.GetRequiredService<IUsers>();
        var results = await Task.WhenAll(Enumerable.Range(1, 100).Select(id => client.GetAsync(id, default)));
        Assert.Equal(Enumerable.Range(1, 100), results.Select(result => result.Id));
    }

    [Fact]
    public async Task Stale_hash_is_rejected_before_deserializing_routed_body()
    {
        using var server = Server();
        using var http = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/123")
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(new byte[] { 0, 0, 0, 0, 1, 128 }),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        request.Headers.Add(GrpcEmbedContractHeaders.Operation, "GrpcEmbed.Users/Get");
        request.Headers.Add(GrpcEmbedContractHeaders.RequestHash, "stale");
        using var response = await http.SendAsync(request);
        Assert.Equal("9", response.Headers.GetValues("grpc-status").Single());
        Assert.Equal("not-executed", response.Headers.GetValues(GrpcEmbedContractHeaders.Rejection).Single());
    }
    [Fact]
    public async Task Rest_and_grpc_use_identical_paths_and_preserve_parameters()
    {
        using var server = Server();
        using var recorder = new RecordingHandler { InnerHandler = server.Server.CreateHandler() };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost/api/users") };
        using var provider = Client(http);
        var rpc = await provider.GetRequiredService<IUsers>().GetAsync(123, default);
        Assert.Equal(123, rpc.Id);
        Assert.Equal("/api/users/123", recorder.LastRpcPath);
        using var rest = server.CreateClient();
        var json = await rest.GetFromJsonAsync<UserDto>("/api/users/123");
        Assert.Equal(rpc.Id, json!.Id);
        Assert.Equal(rpc.Name, json.Name);
    }

    [Fact]
    public async Task Mismatching_url_cannot_execute_the_action()
    {
        using var server = Server();
        using var recorder = new RecordingHandler { InnerHandler = server.Server.CreateHandler(), TamperPath = true };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost/api/users") };
        using var provider = Client(http);
        var error = await Assert.ThrowsAsync<RpcException>(() => provider.GetRequiredService<IUsers>().GetAsync(123, default));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Rpc_post_and_rest_post_on_same_path_use_different_deserializers()
    {
        using var server = Server();
        using var recorder = new RecordingHandler { InnerHandler = server.Server.CreateHandler() };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost/api/users") };
        using var provider = Client(http);
        var rpc = await provider.GetRequiredService<IUsers>().CreateAsync(new CreateUserRequest { Name = "rpc" });
        Assert.Equal("rpc", rpc.Name);
        Assert.Equal("/api/users", recorder.LastRpcPath);
        using var rest = server.CreateClient();
        using var response = await rest.PostAsJsonAsync("/api/users", new CreateUserRequest { Name = "rest" });
        response.EnsureSuccessStatusCode();
        Assert.Equal("rest", (await response.Content.ReadFromJsonAsync<UserDto>())!.Name);
    }

    private static WebApplicationFactory<Program> Server(GrpcEmbedRoutingMode mode = GrpcEmbedRoutingMode.Rest) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.PostConfigure<GrpcEmbedOptions>(options =>
        {
            options.Routing.Mode = mode;
            if (mode == GrpcEmbedRoutingMode.Method) options.ShouldExport = action => action.ControllerName == "Users";
            options.Contract.Validation = GrpcEmbedContractValidation.Required;
        })));

    private static ServiceProvider Client(HttpClient http, GrpcEmbedRoutingMode mode = GrpcEmbedRoutingMode.Rest)
    {
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IUsers>(options =>
        {
            options.Address = http.BaseAddress!;
            options.HttpClient = http;
            options.Routing.Mode = mode;
            options.ServiceName = "Users";
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
            options.Contract.SendHash = true;
        });
        return services.BuildServiceProvider();
    }

    public interface IUsers
    {
        Task<UserDto> GetAsync(int id, CancellationToken cancellationToken);
        Task<UserDto> CreateAsync(CreateUserRequest request);
    }

    private sealed class RecordingHandler : DelegatingHandler
    {
        public string? LastRpcPath { get; private set; }
        public bool TamperPath { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Content?.Headers.ContentType?.MediaType == "application/grpc")
            {
                Assert.False(request.Headers.Contains("grpcembed-internal-path"));
                LastRpcPath = request.RequestUri!.AbsolutePath;
                if (TamperPath) request.RequestUri = new Uri(request.RequestUri, "/api/users/456");
            }
            return base.SendAsync(request, token);
        }
    }
}
