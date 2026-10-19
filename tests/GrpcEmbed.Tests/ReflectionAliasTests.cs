using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using GrpcEmbed.AspNetCore;
using GrpcEmbed.Tests.Generated;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ReflectionAliasTests
{
    private static WebApplicationFactory<Program> Create(bool aliases = true, bool anonymous = true,
        GrpcEmbedContractValidation validation = GrpcEmbedContractValidation.IfPresent) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            services.PostConfigure<GrpcEmbedOptions>(options =>
            {
                options.Routing.Mode = GrpcEmbedRoutingMode.Rest;
                options.Routing.EnableNativeAliases = aliases;
                options.Contract.AllowAnonymous = anonymous;
                options.Contract.Validation = validation;
            });
        }));

    private static GrpcChannel Channel(WebApplicationFactory<Program> server) => GrpcChannel.ForAddress("http://localhost",
        new GrpcChannelOptions { HttpHandler = server.Server.CreateHandler() });

    private static Method<ServerReflectionRequest, ServerReflectionResponse> Reflection(string version) =>
        new(MethodType.DuplexStreaming, "grpc.reflection." + version + ".ServerReflection", "ServerReflectionInfo",
            Marshallers.Create(request => request.ToByteArray(), bytes => ServerReflectionRequest.Parser.ParseFrom(bytes)),
            Marshallers.Create(response => response.ToByteArray(), bytes => ServerReflectionResponse.Parser.ParseFrom(bytes)));

    [Theory]
    [InlineData("v1")]
    [InlineData("v1alpha")]
    public async Task Discovery_resolves_services_methods_types_files_and_dependencies_without_local_proto(string version)
    {
        using var server = Create();
        using var channel = Channel(server);
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(Reflection(version), null, new CallOptions());
        async Task<ServerReflectionResponse> Query(ServerReflectionRequest request)
        {
            await call.RequestStream.WriteAsync(request);
            Assert.True(await call.ResponseStream.MoveNext(default));
            Assert.Equal(request, call.ResponseStream.Current.OriginalRequest);
            return call.ResponseStream.Current;
        }
        var list = await Query(new ServerReflectionRequest { ListServices = "" });
        Assert.Contains(list.ListServicesResponse.Service, service => service.Name == "GrpcEmbed.Users");
        Assert.Contains(list.ListServicesResponse.Service, service => service.Name == "grpc.reflection.v1.ServerReflection");
        Assert.Contains(list.ListServicesResponse.Service, service => service.Name == "grpc.reflection.v1alpha.ServerReflection");
        foreach (var symbol in new[] { "GrpcEmbed.Users", "GrpcEmbed.Users.Get", "GrpcEmbed.UserDto", "grpc.reflection." + version + ".ServerReflection" })
        {
            var result = await Query(new ServerReflectionRequest { FileContainingSymbol = symbol });
            Assert.Null(result.ErrorResponse);
            var files = result.FileDescriptorResponse.FileDescriptorProto.Select(bytes => FileDescriptorProto.Parser.ParseFrom(bytes)).ToArray();
            foreach (var file in files)
                foreach (var dependency in file.Dependency) Assert.Contains(files, candidate => candidate.Name == dependency);
            Assert.NotEmpty(FileDescriptor.BuildFromByteStrings(result.FileDescriptorResponse.FileDescriptorProto));
        }
        var unknown = await Query(new ServerReflectionRequest { FileContainingSymbol = "GrpcEmbed.DoesNotExist" });
        Assert.Equal((int)StatusCode.NotFound, unknown.ErrorResponse.ErrorCode);
        var fileResult = await Query(new ServerReflectionRequest { FileByFilename = "grpc/reflection/" + version + "/reflection.proto" });
        Assert.Equal("grpc.reflection." + version, FileDescriptorProto.Parser.ParseFrom(fileResult.FileDescriptorResponse.FileDescriptorProto[0]).Package);
        var extensions = await Query(new ServerReflectionRequest { AllExtensionNumbersOfType = "GrpcEmbed.UserDto" });
        Assert.Empty(extensions.AllExtensionNumbersResponse.ExtensionNumber);
        await call.RequestStream.CompleteAsync();
        Assert.False(await call.ResponseStream.MoveNext(default));
        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
    }

    [Fact]
    public async Task Aliases_preserve_action_authentication_audit_metadata_and_required_hash()
    {
        using var server = Create(validation: GrpcEmbedContractValidation.Required);
        using var channel = Channel(server);
        var client = new Users.UsersClient(channel);
        var headers = new Metadata { { "x-user", "max" } };
        var stale = await Assert.ThrowsAsync<RpcException>(async () => await client.GetAsync(new Users_Get_Request { Id = 42 }, headers));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);
        headers.Add(GrpcEmbedContractHeaders.RequestHash, server.Services.GetRequiredService<RuntimeRegistry>().GetSchema(server.Services).Sha256);
        Assert.Equal(42, (await client.GetAsync(new Users_Get_Request { Id = 42 }, headers)).Id);
        Assert.Equal("max", (await client.SecureAsync(new Users_Secure_Request { Id = 7 }, headers)).Name);
        Assert.Equal(StatusCode.Unauthenticated,
            (await Assert.ThrowsAsync<RpcException>(async () => await client.SecureAsync(new Users_Secure_Request { Id = 7 }))).StatusCode);
        var endpoint = server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/GrpcEmbed.Users/Get");
        Assert.NotNull(endpoint.Metadata.GetMetadata<ControllerActionDescriptor>());
        Assert.NotNull(endpoint.Metadata.GetMetadata<GrpcEmbedPayloadAuditContract>());
        // Aliases never replace or remove the normal REST route.
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("x-user", "max");
        Assert.Contains("\"id\":42", await http.GetStringAsync("/api/users/42"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v1alpha")]
    public async Task Reflection_respects_fallback_authorization_when_not_anonymous(string version)
    {
        using var server = Create(anonymous: false);
        using var channel = Channel(server);
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(Reflection(version), null, new CallOptions());
        // Auth may finish before the first stream write; reading is sufficient to observe it.
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(default));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public void Aliases_are_opt_in_and_do_not_change_business_schema_or_hash()
    {
        using var original = Create(aliases: false);
        using var aliases = Create();
        original.CreateClient().Dispose();
        aliases.CreateClient().Dispose();
        var before = original.Services.GetRequiredService<RuntimeRegistry>().GetSchema(original.Services);
        var after = aliases.Services.GetRequiredService<RuntimeRegistry>().GetSchema(aliases.Services);
        Assert.Equal(before.Proto, after.Proto);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.DoesNotContain(original.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/GrpcEmbed.Users/Get");
    }

    [Fact]
    public void Reflection_and_aliases_bind_from_server_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GrpcEmbed:Server:Enabled"] = "true",
            ["GrpcEmbed:Server:EnableReflection"] = "true",
            ["GrpcEmbed:Server:Routing:Mode"] = "Rest",
            ["GrpcEmbed:Server:Routing:EnableNativeAliases"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGrpcEmbed(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value;
        Assert.True(options.EnableReflection);
        Assert.True(options.Routing.EnableNativeAliases);
        Assert.Equal(GrpcEmbedRoutingMode.Rest, options.Routing.Mode);
    }
}
