using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace GrpcEmbed.AspNetCore;

internal sealed class GrpcEmbedRouteMetadata
{
    public RuntimeMethod Method { get; }
    private readonly (string Name, int Index, System.ComponentModel.TypeConverter Converter, object? Default)[] _bindings;
    public GrpcEmbedRouteMetadata(RuntimeMethod method, RoutePattern pattern)
    {
        Method = method;
        _bindings = pattern.Parameters.Select(parameter =>
        {
            var descriptor = method.Action.Parameters.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerParameterDescriptor>()
                .FirstOrDefault(item => string.Equals(item.BindingInfo?.BinderModelName ?? item.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            var index = descriptor is null ? -1 : Array.FindIndex(method.Parameters.ToArray(), item => item == descriptor.ParameterInfo);
            return (parameter.Name, Index: index);
        }).Where(item => item.Index >= 0).Select(item =>
        {
            var parameter = method.Parameters[item.Index];
            var type = parameter.ParameterType;
            var fallback = parameter.HasDefaultValue ? parameter.DefaultValue : type.IsValueType ? Activator.CreateInstance(type) : null;
            return (item.Name, item.Index, System.ComponentModel.TypeDescriptor.GetConverter(type), fallback);
        }).ToArray();
    }

    public void Validate(HttpContext context, object request)
    {
        foreach (var binding in _bindings)
        {
            var actual = Method.RequestReaders[binding.Index](request);
            object? expected = binding.Default;
            try
            {
                if (context.Request.RouteValues.TryGetValue(binding.Name, out var value) && value is not null)
                    expected = binding.Converter.ConvertFromInvariantString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
            }
            catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException)
            { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Invalid route parameter.")); }
            if (!Equals(expected, actual))
                throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Route and Protobuf parameters disagree."));
        }
    }
}

internal static class GrpcEmbedRouteMapping
{
    public static void Configure(IEndpointConventionBuilder builder, IServiceProvider services, GrpcEmbedOptions options)
    {
        if (!Enum.IsDefined(typeof(GrpcEmbedRoutingMode), options.Routing.Mode))
            throw new InvalidOperationException("Unknown GrpcEmbed routing mode.");
        if (options.Routing.Mode == GrpcEmbedRoutingMode.Native) return;
        var methods = services.GetRequiredService<RuntimeRegistry>().Get(services);
        var paths = new Dictionary<string, (RuntimeMethod Method, string Path)>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var path = options.Routing.Mode switch
            {
                GrpcEmbedRoutingMode.Rest => method.Action.AttributeRouteInfo?.Template
                    ?? throw new InvalidOperationException($"REST routing requires an attribute route: {method.Action.DisplayName}."),
                GrpcEmbedRoutingMode.Method => options.Routing.Prefix.Trim('/') + "/" + method.MethodName,
                _ => options.Routing.Prefix.Trim('/') + "/" + method.ServiceName + "/" + method.MethodName,
            };
            if (options.Routing.Mode == GrpcEmbedRoutingMode.Method && paths.Values.Any(value => value.Path == path))
                throw new InvalidOperationException($"Duplicate GrpcEmbed method route: {path}. Use ControllerMethod routing.");
            paths.Add("/GrpcEmbed." + method.ServiceName + "/" + method.MethodName, (method, path));
        }
        builder.Add(endpoint =>
        {
            if (endpoint is RouteEndpointBuilder route && paths.TryGetValue(route.RoutePattern.RawText ?? "", out var mapped))
            {
                route.RoutePattern = RoutePatternFactory.Parse(mapped.Path);
                route.Metadata.Add(new GrpcEmbedRouteMetadata(mapped.Method, route.RoutePattern));
            }
        });
    }
}

// Run before HTTP verb matching: a gRPC POST must never fall through to a REST POST.
internal sealed class GrpcEmbedRouteSelectionPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    private readonly Microsoft.Extensions.Options.IOptions<GrpcEmbedOptions> _options;
    public GrpcEmbedRouteSelectionPolicy(Microsoft.Extensions.Options.IOptions<GrpcEmbedOptions> options) => _options = options;
    public override int Order => -2000;
    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) =>
        _options.Value.ServerEnabled && _options.Value.Routing.Mode != GrpcEmbedRoutingMode.Native;

    public Task ApplyAsync(HttpContext context, CandidateSet candidates)
    {
        var contentType = context.Request.ContentType?.Split(';')[0].Trim();
        var grpc = string.Equals(contentType, "application/grpc", StringComparison.OrdinalIgnoreCase) ||
            contentType?.StartsWith("application/grpc+", StringComparison.OrdinalIgnoreCase) == true;
        var operation = context.Request.Headers[GrpcEmbedContractHeaders.Operation];
        for (var i = 0; i < candidates.Count; i++)
        {
            var metadata = candidates[i].Endpoint.Metadata.GetMetadata<GrpcEmbedRouteMetadata>();
            if (metadata is null)
            {
                if (grpc && candidates[i].Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is not null)
                    candidates.SetValidity(i, false);
            }
            else if (!grpc || operation.Count != 1 || operation[0] != "GrpcEmbed." + metadata.Method.ServiceName + "/" + metadata.Method.MethodName)
                candidates.SetValidity(i, false);
        }
        return Task.CompletedTask;
    }
}
