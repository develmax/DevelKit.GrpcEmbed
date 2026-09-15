using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using System.Diagnostics.CodeAnalysis;

namespace GrpcEmbed.AspNetCore;

public static class GrpcEmbedExtensions
{
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed generates protobuf request and response types at runtime.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed discovers MVC controllers and DTO members through reflection.")]
    public static IServiceCollection AddGrpcEmbed(this IServiceCollection services, Action<GrpcEmbedOptions>? configure = null)
    {
        services.AddGrpc();
        if (configure is not null) services.Configure(configure); else services.Configure<GrpcEmbedOptions>(_ => { });
        services.TryAddSingleton<RuntimeRegistry>();
        services.TryAddSingleton<GrpcEmbedService>();
        services.AddSingleton<IServiceMethodProvider<GrpcEmbedService>, GrpcEmbedMethodProvider>();
        return services;
    }

#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed registers runtime-generated gRPC method types.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed maps reflected MVC actions and DTO members.")]
    public static IEndpointConventionBuilder MapGrpcEmbed(this IEndpointRouteBuilder endpoints)
    {
        var grpc = endpoints.MapGrpcService<GrpcEmbedService>();
        var options = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<GrpcEmbedOptions>>().Value;
        if (options.EnableSchemaEndpoint)
        {
            var schemaProto = endpoints.MapGet("/_grpcembed/schema.proto", (RuntimeRegistry registry, IServiceProvider services) =>
            {
                var schema = registry.GetSchema(services);
                return Results.Text(schema.Proto, "text/plain", Encoding.UTF8);
            });
            var metadata = endpoints.MapGet("/_grpcembed", (RuntimeRegistry registry, IServiceProvider services) =>
            {
                var schema = registry.GetSchema(services);
                return Results.Json(new { schemaHash = schema.Sha256, proto = "/_grpcembed/schema.proto" });
            });
            var descriptor = endpoints.MapGet("/_grpcembed/descriptor.pb", (RuntimeRegistry registry, IServiceProvider services) => Results.File(registry.GetSchema(services).DescriptorSet, "application/octet-stream", "grpcembed.protoset"));
            var manifest = endpoints.MapGet("/_grpcembed/schema.json", (RuntimeRegistry registry, IServiceProvider services) => Results.Text(registry.GetManifest(services), "application/json", Encoding.UTF8));
            if (options.SchemaAuthorizationPolicy is { Length: > 0 } policy)
            {
                schemaProto.RequireAuthorization(policy);
                metadata.RequireAuthorization(policy);
                descriptor.RequireAuthorization(policy);
                manifest.RequireAuthorization(policy);
            }
        }
        if (options.EnableReflection)
        {
            var reflection = endpoints.MapGrpcService<GrpcEmbedReflectionService>();
            if (options.SchemaAuthorizationPolicy is { Length: > 0 } policy) reflection.RequireAuthorization(policy);
        }
        return grpc;
    }
}
