using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace GrpcEmbed.AspNetCore;

internal sealed class RuntimeRegistry
{
    private readonly object _gate = new();
    private readonly ILogger<RuntimeRegistry> _logger;
    private IReadOnlyList<RuntimeMethod>? _methods;
    private SchemaManifest? _manifest;
    private GrpcEmbedSchema? _schema;
    private string? _manifestJson;
    public RuntimeRegistry(ILogger<RuntimeRegistry> logger) => _logger = logger;
    public IReadOnlyList<RuntimeMethod> Get(IServiceProvider services)
    {
        if (_methods is not null) return _methods;
        lock (_gate)
        {
            if (_methods is not null) return _methods;
            _methods = Discover(services, _logger);
            var options = services.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.SchemaManifestPath) && File.Exists(options.SchemaManifestPath))
            {
                _manifest = SchemaManifestManager.Create(_methods, options.Contract.Hash.Enabled);
                var errors = SchemaManifestManager.ValidateFile(options.SchemaManifestPath, _manifest);
                foreach (var error in errors) _logger.LogWarning("GrpcEmbed schema compatibility: {Error}", error);
                if (errors.Count > 0 && options.ThrowOnUnsupportedAction) throw new InvalidOperationException("GrpcEmbed schema compatibility validation failed: " + string.Join(" ", errors));
            }
            return _methods;
        }
    }

    public GrpcEmbedSchema GetSchema(IServiceProvider services)
    {
        Get(services);
        lock (_gate)
            return _schema ??= SchemaGenerator.Generate(_methods!,
                services.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value.Contract.Hash.Enabled);
    }
    public string GetManifest(IServiceProvider services)
    {
        Get(services);
        lock (_gate)
        {
            if (_manifestJson is not null) return _manifestJson;
            var options = services.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value;
            _manifest ??= SchemaManifestManager.Create(_methods!, options.Contract.Hash.Enabled);
            return _manifestJson = SchemaManifestManager.Serialize(_manifest with
            {
                SchemaHash = options.Contract.Hash.Enabled ? GetSchema(services).Sha256 : null,
            });
        }
    }

    private static IReadOnlyList<RuntimeMethod> Discover(IServiceProvider services, ILogger logger)
    {
        var descriptors = services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items.OfType<ControllerActionDescriptor>();
        var options = services.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value;
        var result = new List<RuntimeMethod>();
        foreach (var action in descriptors)
        {
            var controllerAttributes = action.ControllerTypeInfo.GetCustomAttributes(true).OfType<Attribute>().ToArray();
            var methodAttributes = action.MethodInfo.GetCustomAttributes(true).OfType<Attribute>().ToArray();
            if (controllerAttributes.OfType<GrpcIgnoreAttribute>().Any() || methodAttributes.OfType<GrpcIgnoreAttribute>().Any()) continue;
            var explicitExport = controllerAttributes.OfType<GrpcExportAttribute>().Any() || methodAttributes.OfType<GrpcExportAttribute>().Any();
            if (options.ExportMode == GrpcEmbedExportMode.ExplicitOnly && !explicitExport) continue;
            var response = ReturnTypes.Unwrap(action.MethodInfo.ReturnType);
            var unsupported = response is null || typeof(Microsoft.AspNetCore.Mvc.FileResult).IsAssignableFrom(response) || typeof(Stream).IsAssignableFrom(response);
            if (unsupported)
            {
                if (options.ThrowOnUnsupportedAction) throw new InvalidOperationException($"GrpcEmbed cannot export {action.DisplayName}: unsupported return type {action.MethodInfo.ReturnType}.");
                logger.LogWarning("GrpcEmbed skipped unsupported MVC action {Action}: return type {ReturnType}.", action.DisplayName, action.MethodInfo.ReturnType);
                continue;
            }
            var parameters = action.MethodInfo.GetParameters().Where(IsTransportParameter).ToArray();
            var service = controllerAttributes.OfType<GrpcNameAttribute>().LastOrDefault()?.Name ?? TrimController(action.ControllerName);
            var method = methodAttributes.OfType<GrpcNameAttribute>().LastOrDefault()?.Name ?? action.ActionName;
            var parameterNumbers = GrpcEmbedFieldNumbers.Assign(parameters);
            var parameterContexts = action.Parameters.OfType<ControllerParameterDescriptor>().Select(p => new GrpcEmbedParameterContext(
                p.Name, p.ParameterInfo.ParameterType, p.BindingInfo?.BindingSource?.Id, IsTransportParameter(p.ParameterInfo),
                IsTransportParameter(p.ParameterInfo) ? parameterNumbers[p.Name] : null)).ToArray();
            var context = new GrpcEmbedActionContext(action.ControllerTypeInfo.AsType(), service, method, action.MethodInfo,
                action.AttributeRouteInfo?.Template, action.ActionConstraints?.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>().SelectMany(x => x.HttpMethods).ToArray() ?? Array.Empty<string>(), response!, controllerAttributes.Concat(methodAttributes).ToArray(), parameterContexts);
            if (options.ShouldExport is not null && !options.ShouldExport(context)) continue;
            if (result.Any(x => string.Equals(x.ServiceName, service, StringComparison.Ordinal) && string.Equals(x.MethodName, method, StringComparison.Ordinal)))
            {
                var message = $"GrpcEmbed cannot export {action.DisplayName}: duplicate gRPC method GrpcEmbed.{service}/{method}. Use GrpcNameAttribute to assign a unique name.";
                if (options.ThrowOnUnsupportedAction) throw new InvalidOperationException(message);
                logger.LogWarning("{Message}", message);
                continue;
            }
            var unsupportedParameter = parameters.FirstOrDefault(p => IsUnsupportedTransportType(p.ParameterType));
            if (unsupportedParameter is not null)
            {
                var message = $"GrpcEmbed cannot export {action.DisplayName}: parameter '{unsupportedParameter.Name}' has unsupported type {unsupportedParameter.ParameterType}.";
                if (options.ThrowOnUnsupportedAction) throw new InvalidOperationException(message);
                logger.LogWarning("{Message}", message);
                continue;
            }
            var request = RuntimeRequestTypes.Create(service + "_" + method + "_Request", parameters);
            var wireResponse = RuntimeResponseTypes.RequiresWrapper(response!)
                ? RuntimeResponseTypes.GetOrCreate(service + "_" + method + "_Response", response!)
                : response!;
            GrpcEmbedRuntimeModel.Configure(request, wireResponse, response!);
            var readers = parameters.Select(p => ActionDelegates.CompileGetter(request, p.Name!)).ToArray();
            var transportIndex = 0;
            var arguments = action.MethodInfo.GetParameters().Select(p =>
            {
                var source = ArgumentSource(p);
                return new RuntimeArgument(p, source, source == RuntimeArgumentSource.Transport ? transportIndex++ : -1,
                    p.GetCustomAttributes(true).OfType<System.ComponentModel.DataAnnotations.ValidationAttribute>().ToArray());
            }).ToArray();
            var wrapper = wireResponse == response ? null : ActionDelegates.CompileWrapper(wireResponse, response!);
            result.Add(new RuntimeMethod(action, service, method, request, wireResponse, response!, parameters,
                ActionDelegates.Compile(action.MethodInfo), readers, arguments, wrapper));
        }
        return result;
    }

    private static bool IsTransportParameter(System.Reflection.ParameterInfo parameter) =>
        parameter.ParameterType != typeof(CancellationToken) &&
        parameter.ParameterType != typeof(HttpContext) &&
        parameter.ParameterType != typeof(ClaimsPrincipal) &&
        parameter.ParameterType != typeof(HttpRequest) &&
        parameter.ParameterType != typeof(HttpResponse) &&
        parameter.GetCustomAttributes(true).All(a => a.GetType().Name is not "FromServicesAttribute" and not "FromKeyedServicesAttribute");
    private static RuntimeArgumentSource ArgumentSource(System.Reflection.ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(CancellationToken)) return RuntimeArgumentSource.CancellationToken;
        if (parameter.ParameterType == typeof(HttpContext)) return RuntimeArgumentSource.HttpContext;
        if (parameter.ParameterType == typeof(ClaimsPrincipal)) return RuntimeArgumentSource.ClaimsPrincipal;
        if (parameter.ParameterType == typeof(HttpRequest)) return RuntimeArgumentSource.HttpRequest;
        if (parameter.ParameterType == typeof(HttpResponse)) return RuntimeArgumentSource.HttpResponse;
        if (parameter.GetCustomAttributes(true).Any(a => a.GetType().Name is "FromServicesAttribute" or "FromKeyedServicesAttribute")) return RuntimeArgumentSource.Services;
        return RuntimeArgumentSource.Transport;
    }
    private static bool IsUnsupportedTransportType(Type type) =>
        typeof(Stream).IsAssignableFrom(type) ||
        typeof(IFormFile).IsAssignableFrom(type) ||
        typeof(IFormFileCollection).IsAssignableFrom(type) ||
        type.IsPointer || type.IsByRef || type.ContainsGenericParameters;
    private static string TrimController(string name) => name.EndsWith("Controller", StringComparison.Ordinal) ? name[..^10] : name;
}
