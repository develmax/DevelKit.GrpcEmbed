using Grpc.Net.Client;
using GrpcEmbed.Sample.Server;
using GrpcEmbed.Tests.Generated;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using GrpcEmbed.Client;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Google.Protobuf.Reflection;
using Grpc.Reflection.V1Alpha;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class InteroperabilityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public InteroperabilityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Rest_and_standard_generated_grpc_client_reach_the_same_action()
    {
        UsersController.InvocationCount = 0;
        UsersController.FilterCount = 0;
        var rest = _factory.CreateClient();
        var json = await rest.GetStringAsync("/api/users/41");
        Assert.Contains("\"id\":41", json, StringComparison.Ordinal);

        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(x => $"{x.RoutePattern.RawText}|{x.DisplayName}").ToArray();
        Assert.Contains(routes, x => x.StartsWith("/GrpcEmbed.Users/Get|gRPC - /GrpcEmbed.Users/Get", StringComparison.Ordinal));

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var grpc = new Users.UsersClient(channel);
        var response = await grpc.GetAsync(new Users_Get_Request { Id = 42 });

        Assert.Equal(42, response.Id);
        Assert.Equal("Test", response.Name);
        Assert.Equal(2, UsersController.InvocationCount);
        Assert.Equal(2, UsersController.FilterCount);
    }

    [Fact]
    public async Task Validation_and_action_results_map_to_grpc_statuses()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var grpc = new Users.UsersClient(channel);
        var invalid = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await grpc.CreateAsync(new Users_Create_Request { Request = new Generated.CreateUserRequest() }));
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, invalid.StatusCode);
        var missing = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await grpc.MissingAsync(new Users_Missing_Request { Id = 9 }));
        Assert.Equal(Grpc.Core.StatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Authorization_metadata_is_preserved_on_grpc_endpoint()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var grpc = new Users.UsersClient(channel);
        var denied = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await grpc.SecureAsync(new Users_Secure_Request { Id = 7 }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, denied.StatusCode);
        var headers = new Grpc.Core.Metadata { { "x-user", "max" } };
        var allowed = await grpc.SecureAsync(new Users_Secure_Request { Id = 7 }, headers);
        Assert.Equal("max", allowed.Name);
    }

    [Fact]
    public async Task Descriptor_set_and_standard_server_reflection_are_available()
    {
        var http = _factory.CreateClient();
        var bytes = await http.GetByteArrayAsync("/_grpcembed/descriptor.pb");
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        Assert.Contains(set.File, file => file.Service.Any(service => service.Name == "Users"));

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var reflection = new ServerReflection.ServerReflectionClient(channel);
        using var call = reflection.ServerReflectionInfo();
        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" });
        await call.RequestStream.CompleteAsync();
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Contains(call.ResponseStream.Current.ListServicesResponse.Service, service => service.Name == "GrpcEmbed.Users");
    }

    [Fact]
    public async Task Exported_schema_is_deterministic_and_matches_external_contract()
    {
        var client = _factory.CreateClient();
        var first = await client.GetStringAsync("/_grpcembed/schema.proto");
        var second = await client.GetStringAsync("/_grpcembed/schema.proto");
        Assert.Equal(first, second);
        Assert.Contains("rpc Get (Users_Get_Request) returns (UserDto);", first, StringComparison.Ordinal);
        Assert.Contains("int32 id = 121987495;", first, StringComparison.Ordinal);
        Assert.Contains("rpc Find (Users_Find_Request) returns (UserDto);", first, StringComparison.Ordinal);
        Assert.Contains("rpc Count (Users_Count_Request) returns (Users_Count_Response);", first, StringComparison.Ordinal);
        Assert.Contains("message Users_Count_Response", first, StringComparison.Ordinal);
        Assert.Contains("int32 value = ", first, StringComparison.Ordinal);
        Assert.Contains("message VersionedDto", first, StringComparison.Ordinal);
        Assert.Contains("reserved 7, 8;", first, StringComparison.Ordinal);
        Assert.Contains("int32 renamed_id = 10;", first, StringComparison.Ordinal);
        Assert.Contains("enum UserState", first, StringComparison.Ordinal);
        Assert.Contains("USERSTATE_ACTIVE = 1;", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_context_preserves_mvc_binding_sources_and_field_numbers()
    {
        GrpcEmbedActionContext? captured = null;
        using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<GrpcEmbedOptions>(options => options.ShouldExport = context =>
            {
                if (context.ControllerName == "Orders" && context.ActionName == "Get") captured = context;
                return true;
            })));
        _ = await app.CreateClient().GetStringAsync("/_grpcembed/schema.proto");
        Assert.NotNull(captured?.Parameters);
        Assert.Equal("Query", captured!.Parameters!.Single(x => x.Name == "includeItems").BindingSource);
        Assert.NotNull(captured.Parameters.Single(x => x.Name == "id").FieldNumber);
    }

    [Fact]
    public async Task Native_grpc_path_does_not_invoke_system_text_json()
    {
        using var isolated = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => options.JsonSerializerOptions.Converters.Add(new ThrowingUserDtoConverter()))));
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = isolated.Server.CreateHandler() });
        var response = await new Users.UsersClient(channel).GetAsync(new Users_Get_Request { Id = 88 });
        Assert.Equal(88, response.Id);
    }

    [Fact]
    public async Task Explicit_only_and_programmatic_export_policies_are_applied_at_startup()
    {
        using var explicitOnly = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<GrpcEmbedOptions>(options => options.ExportMode = GrpcEmbedExportMode.ExplicitOnly)));
        var explicitSchema = await explicitOnly.CreateClient().GetStringAsync("/_grpcembed/schema.proto");
        Assert.Contains("rpc Find", explicitSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("rpc Get", explicitSchema, StringComparison.Ordinal);

        using var predicate = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<GrpcEmbedOptions>(options => options.ShouldExport = context => context.ActionName == "Get")));
        var predicateSchema = await predicate.CreateClient().GetStringAsync("/_grpcembed/schema.proto");
        Assert.Contains("rpc Get", predicateSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("rpc Create", predicateSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_mode_rejects_an_unsupported_multipart_action_with_diagnostics()
    {
        using var strict = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<GrpcEmbedOptions>(options => options.ThrowOnUnsupportedAction = true)));

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await strict.CreateClient().GetAsync("/_grpcembed/schema.proto"));

        Assert.Contains("Upload", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("IFormFile", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contract_proxy_calls_the_same_native_grpc_method()
    {
        var metadata = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync("/_grpcembed"));
        var schemaHash = metadata.RootElement.GetProperty("schemaHash").GetString();
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IUsersApi>(options =>
        {
            options.Address = new Uri("http://localhost");
            options.HttpHandler = _factory.Server.CreateHandler();
            options.DefaultTimeout = TimeSpan.FromSeconds(5);
            options.ExpectedSchemaHash = schemaHash;
            options.MetadataFactory = () => new Grpc.Core.Metadata { { "x-user", "max" }, { "x-filter-auth", "true" } };
        });
        await using var provider = services.BuildServiceProvider();
        var user = await provider.GetRequiredService<IUsersApi>().Get(73);
        Assert.Equal(73, user.Id);
        Assert.Equal("Test", user.Name);
        Assert.Equal(74, await provider.GetRequiredService<IUsersApi>().Count(73));
        Assert.Equal(5, await provider.GetRequiredService<IUsersApi>().ValidatedScalar(5));
        var invalidScalar = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => provider.GetRequiredService<IUsersApi>().ValidatedScalar(20));
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, invalidScalar.StatusCode);
        Assert.Equal("user-73", await provider.GetRequiredService<IUsersApi>().Label(73));
        Assert.Equal(new[] { 1, 2, 3 }, (await provider.GetRequiredService<IUsersApi>().List(3)).Select(x => x.Id));
        Assert.Equal("max|/GrpcEmbed.Users/Context|73", await provider.GetRequiredService<IUsersApi>().Context(73));
        var valueTaskUser = await provider.GetRequiredService<IUsersApi>().ValueTaskGet(75);
        Assert.Equal(75, valueTaskUser.Id);
        Assert.Equal("ValueTask", valueTaskUser.Name);
        var named = await provider.GetRequiredService<IUsersApi>().Named(78);
        Assert.Equal("Named", named.Name);
        UsersController.AuthorizationFilterCount = 0;
        var filtered = await provider.GetRequiredService<IUsersApi>().AuthorizationFiltered(76);
        Assert.Equal(76, filtered.Id);
        Assert.Equal(1, UsersController.AuthorizationFilterCount);
        UsersController.ResourceFilterBeforeCount = 0;
        UsersController.ResourceFilterAfterCount = 0;
        var resourceFiltered = await provider.GetRequiredService<IUsersApi>().ResourceFiltered(79);
        Assert.Equal(79, resourceFiltered.Id);
        Assert.Equal(1, UsersController.ResourceFilterBeforeCount);
        Assert.Equal(1, UsersController.ResourceFilterAfterCount);
        UsersController.ResourceShortCircuitActionCount = 0;
        var shortCircuited = await provider.GetRequiredService<IUsersApi>().ResourceShortCircuited(80);
        Assert.Equal(900, shortCircuited.Id);
        Assert.Equal("Resource short circuit", shortCircuited.Name);
        Assert.Equal(0, UsersController.ResourceShortCircuitActionCount);

        var deniedServices = new ServiceCollection();
        deniedServices.AddGrpcEmbedClient<IUsersApi>(options =>
        {
            options.Address = new Uri("http://localhost");
            options.HttpHandler = _factory.Server.CreateHandler();
        });
        await using var deniedProvider = deniedServices.BuildServiceProvider();
        var denied = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => deniedProvider.GetRequiredService<IUsersApi>().AuthorizationFiltered(77));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, denied.StatusCode);
    }

    [Fact]
    public async Task Client_rejects_an_unexpected_server_schema_hash()
    {
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IUsersApi>(options => { options.Address = new Uri("http://localhost"); options.HttpHandler = _factory.Server.CreateHandler(); options.ExpectedSchemaHash = new string('0', 64); });
        await using var provider = services.BuildServiceProvider();
        var exception = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => provider.GetRequiredService<IUsersApi>().Get(1));
        Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Fact]
    public async Task Contract_proxy_reuses_channel_for_concurrent_calls_and_honors_deadline()
    {
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IUsersApi>(options => { options.Address = new Uri("http://localhost"); options.HttpHandler = _factory.Server.CreateHandler(); options.DefaultTimeout = TimeSpan.FromMilliseconds(50); });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IUsersApi>();
        var users = await Task.WhenAll(Enumerable.Range(1, 20).Select(id => client.Get(id)));
        Assert.Equal(Enumerable.Range(1, 20), users.Select(x => x.Id));
        var timeout = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => client.Delay(1));
        Assert.Equal(Grpc.Core.StatusCode.DeadlineExceeded, timeout.StatusCode);
    }

    [Fact]
    public async Task Schema_manifest_contains_stable_field_contracts()
    {
        var json = await _factory.CreateClient().GetStringAsync("/_grpcembed/schema.json");
        using var document = JsonDocument.Parse(json);
        var types = document.RootElement.GetProperty("types");
        Assert.True(types.TryGetProperty("GrpcEmbed.Sample.Server.UserDto", out var user));
        Assert.Equal(309204247, user.GetProperty("fields").GetProperty("Id").GetProperty("number").GetInt32());
        var users = document.RootElement.GetProperty("services").GetProperty("Users").GetProperty("methods");
        Assert.Equal("Users_Get_Request", users.GetProperty("Get").GetProperty("requestType").GetString());
        Assert.Equal("GrpcEmbed.Sample.Server.UserDto", users.GetProperty("Get").GetProperty("responseType").GetString());
    }

    public interface IUsersApi
    {
        Task<GrpcEmbed.Sample.Server.UserDto> Get(int id, CancellationToken cancellationToken = default);
        Task<GrpcEmbed.Sample.Server.UserDto> Delay(int id, CancellationToken cancellationToken = default);
        Task<int> Count(int id, CancellationToken cancellationToken = default);
        Task<int> ValidatedScalar(int value, CancellationToken cancellationToken = default);
        Task<string> Label(int id, CancellationToken cancellationToken = default);
        Task<List<GrpcEmbed.Sample.Server.UserDto>> List(int count, CancellationToken cancellationToken = default);
        Task<string> Context(int id, CancellationToken cancellationToken = default);
        ValueTask<GrpcEmbed.Sample.Server.UserDto> ValueTaskGet(int id, CancellationToken cancellationToken = default);
        [GrpcName("Find")]
        Task<GrpcEmbed.Sample.Server.UserDto> Named(int id, CancellationToken cancellationToken = default);
        Task<GrpcEmbed.Sample.Server.UserDto> AuthorizationFiltered(int id, CancellationToken cancellationToken = default);
        Task<GrpcEmbed.Sample.Server.UserDto> ResourceFiltered(int id, CancellationToken cancellationToken = default);
        Task<GrpcEmbed.Sample.Server.UserDto> ResourceShortCircuited(int id, CancellationToken cancellationToken = default);
    }

    private sealed class ThrowingUserDtoConverter : JsonConverter<GrpcEmbed.Sample.Server.UserDto>
    {
        public override GrpcEmbed.Sample.Server.UserDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new InvalidOperationException("JSON used in native path");
        public override void Write(Utf8JsonWriter writer, GrpcEmbed.Sample.Server.UserDto value, JsonSerializerOptions options) => throw new InvalidOperationException("JSON used in native path");
    }
}
