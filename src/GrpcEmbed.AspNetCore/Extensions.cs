using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace GrpcEmbed.AspNetCore;

public static class GrpcEmbedExtensions
{
    /// <summary>Configures publication using only GrpcEmbed:Server settings.</summary>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed generates protobuf request and response types at runtime.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed discovers MVC controllers and DTO members through reflection.")]
    public static IServiceCollection AddGrpcEmbed(this IServiceCollection services, IConfiguration configuration,
        Action<GrpcEmbedOptions>? configure = null)
    {
        return services.AddGrpcEmbed(options =>
        {
            configure?.Invoke(options);
            configuration.GetSection("GrpcEmbed:Server:Contract").Bind(options.Contract);
            configuration.GetSection("GrpcEmbed:Server:Routing").Bind(options.Routing);
            options.ServerEnabled = configuration.GetValue<bool>("GrpcEmbed:Server:Enabled");
            options.ExportMode = configuration.GetValue<bool>("GrpcEmbed:Server:ExposeAll")
                ? GrpcEmbedExportMode.All : GrpcEmbedExportMode.ExplicitOnly;
        });
    }

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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Routing.MatcherPolicy, GrpcEmbedRouteSelectionPolicy>());
        services.AddSingleton<IServiceMethodProvider<GrpcEmbedService>, GrpcEmbedMethodProvider>();
        return services;
    }

#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed registers runtime-generated gRPC method types.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed maps reflected MVC actions and DTO members.")]
    public static IEndpointConventionBuilder MapGrpcEmbed(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<GrpcEmbedOptions>>().Value;
        if (!options.ServerEnabled) return new DisabledEndpoints();
        if (!Enum.IsDefined(typeof(GrpcEmbedContractValidation), options.Contract.Validation) ||
            !Enum.IsDefined(typeof(GrpcEmbedContractGeneration), options.Contract.Generate))
            throw new InvalidOperationException("Unknown contract validation or generation mode.");
        if (options.EnableReflection && !options.Contract.Enabled)
            throw new InvalidOperationException("Reflection requires contract generation.");
        if (options.Contract.Validation != GrpcEmbedContractValidation.Disabled &&
            (!options.Contract.Enabled || !options.Contract.Hash.Enabled))
            throw new InvalidOperationException("Contract validation requires contract generation and hashing.");
        if (options.Contract.ExposeEndpoint && !options.Contract.Enabled)
            throw new InvalidOperationException("The contract endpoint requires contract generation.");
        var grpc = endpoints.MapGrpcService<GrpcEmbedService>();
        GrpcEmbedRouteMapping.Configure(grpc, endpoints.ServiceProvider, options);
        if (options.Contract.Enabled && options.Contract.Generate == GrpcEmbedContractGeneration.Startup)
            endpoints.ServiceProvider.GetRequiredService<RuntimeRegistry>().GetManifest(endpoints.ServiceProvider);
        if (options.Contract.Validation != GrpcEmbedContractValidation.Disabled)
            grpc.Add(endpoint =>
            {
                var next = endpoint.RequestDelegate!;
                endpoint.RequestDelegate = context => ContractRequestGate.Invoke(context, next, options);
            });
        if (options.Contract.Enabled && (options.EnableSchemaEndpoint || options.Contract.ExposeEndpoint))
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
            var manifest = endpoints.MapGet("/_grpcembed/schema.json", (HttpContext context, RuntimeRegistry registry, IServiceProvider services) =>
            {
                var hash = options.Contract.Hash.Enabled ? registry.GetSchema(services).Sha256 : null;
                if (hash is not null)
                {
                    var etag = "\"" + hash + "\"";
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Headers["Cache-Control"] = "private, no-cache";
                    context.Response.Headers[GrpcEmbedContractHeaders.ServerHash] = hash;
                    if (context.Request.Headers["If-None-Match"].ToString() == etag)
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                return Results.Text(registry.GetManifest(services), "application/json", Encoding.UTF8);
            });
            if (options.Contract.AllowAnonymous)
            {
                schemaProto.AllowAnonymous();
                metadata.AllowAnonymous();
                descriptor.AllowAnonymous();
                manifest.AllowAnonymous();
            }
            else if (options.SchemaAuthorizationPolicy is { Length: > 0 } policy)
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

    private sealed class DisabledEndpoints : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention) { }
    }
}
