using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using ProtoBuf.Meta;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Filters;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace GrpcEmbed.AspNetCore;

internal sealed class GrpcEmbedService
{
    private readonly IServiceProvider _services;
    private readonly IControllerFactory _controllers;
    private readonly GrpcEmbedOptions _options;
    private readonly ILogger<GrpcEmbedService> _logger;
    private readonly RuntimeRegistry _registry;
    public GrpcEmbedService(IServiceProvider services, IControllerFactory controllers, IOptions<GrpcEmbedOptions> options, ILogger<GrpcEmbedService> logger, RuntimeRegistry registry) { _services = services; _controllers = controllers; _options = options.Value; _logger = logger; _registry = registry; }

    public async Task<TResponse> Invoke<TRequest, TResponse>(RuntimeMethod method, TRequest request, ServerCallContext call)
        where TRequest : class where TResponse : class
    {
        var http = call.GetHttpContext();
        await call.WriteResponseHeadersAsync(new Metadata { { "grpcembed-schema-hash", _registry.GetSchema(_services).Sha256 } });
        var actionContext = new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), method.Action);
        var controllerContext = new ControllerContext(actionContext);
        var controller = _controllers.CreateController(controllerContext);
        try
        {
            var values = new object?[method.RequestReaders.Length];
            for (var i = 0; i < values.Length; i++) values[i] = method.RequestReaders[i](request);
            var arguments = new object?[method.Arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                var plan = method.Arguments[i];
                arguments[i] = plan.Source switch
                {
                    RuntimeArgumentSource.CancellationToken => call.CancellationToken,
                    RuntimeArgumentSource.HttpContext => http,
                    RuntimeArgumentSource.ClaimsPrincipal => http.User,
                    RuntimeArgumentSource.HttpRequest => http.Request,
                    RuntimeArgumentSource.HttpResponse => http.Response,
                    RuntimeArgumentSource.Services => http.RequestServices.GetRequiredService(plan.Parameter.ParameterType),
                    _ => values[plan.TransportIndex]
                };
            }
            Validate(arguments, method.Arguments);
            var filters = method.Action.FilterDescriptors.Select(x => x.Filter is IFilterFactory factory ? factory.CreateInstance(http.RequestServices) : x.Filter).ToArray();
            object? raw = null;
            try
            {
                raw = await InvokeWithActionFilters(method, controller, actionContext, filters, arguments);
            }
            catch (Exception exception)
            {
                var exceptionContext = new ExceptionContext(actionContext, filters) { Exception = exception };
                foreach (var filter in filters.AsEnumerable().Reverse())
                {
                    if (filter is IAsyncExceptionFilter asyncFilter) await asyncFilter.OnExceptionAsync(exceptionContext);
                    else if (filter is IExceptionFilter syncFilter) syncFilter.OnException(exceptionContext);
                }
                if (!exceptionContext.ExceptionHandled) throw;
                raw = exceptionContext.Result;
            }
            raw = UnwrapActionResult(raw, _options);
            if (raw is null) throw new RpcException(new Status(StatusCode.NotFound, "The MVC action returned no value."));
            if (method.WrapResponse is not null) return (TResponse)method.WrapResponse(raw);
            return (TResponse)raw;
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled.")); }
        catch (Exception ex) { _logger.LogError(ex, "GrpcEmbed MVC action {Action} failed.", method.Action.DisplayName); throw new RpcException(new Status(StatusCode.Internal, _options.EnableDetailedErrors ? ex.Message : "The MVC action failed.")); }
        finally { _controllers.ReleaseController(controllerContext, controller); }
    }

    private static async Task<object?> InvokeWithActionFilters(RuntimeMethod method, object controller, ActionContext actionContext, IFilterMetadata[] filters, object?[] arguments)
    {
        var filterList = filters.ToList();
        var authorization = new AuthorizationFilterContext(actionContext, filterList);
        foreach (var filter in filters)
        {
            if (filter is IAsyncAuthorizationFilter asyncAuthorization) await asyncAuthorization.OnAuthorizationAsync(authorization);
            else if (filter is IAuthorizationFilter syncAuthorization) syncAuthorization.OnAuthorization(authorization);
            if (authorization.Result is not null) break;
        }
        if (authorization.Result is not null) return await InvokeActionAndResultFilters(method, controller, actionContext, filters, arguments, authorization.Result);

        async Task<ResourceExecutedContext> Next(int index)
        {
            if (index == filters.Length)
            {
                var result = await InvokeActionAndResultFilters(method, controller, actionContext, filters, arguments);
                return new ResourceExecutedContext(actionContext, filterList) { Result = result as IActionResult ?? new ObjectResult(result) };
            }
            var executing = new ResourceExecutingContext(actionContext, filterList, new List<Microsoft.AspNetCore.Mvc.ModelBinding.IValueProviderFactory>());
            if (filters[index] is IAsyncResourceFilter asyncFilter)
            {
                ResourceExecutedContext? completed = null;
                await asyncFilter.OnResourceExecutionAsync(executing, async () => completed = await Next(index + 1));
                return completed ?? new ResourceExecutedContext(actionContext, filterList) { Canceled = true, Result = executing.Result };
            }
            if (filters[index] is IResourceFilter filter)
            {
                filter.OnResourceExecuting(executing);
                if (executing.Result is not null) return new ResourceExecutedContext(actionContext, filterList) { Canceled = true, Result = executing.Result };
                var completed = await Next(index + 1);
                filter.OnResourceExecuted(completed);
                return completed;
            }
            return await Next(index + 1);
        }
        return (await Next(0)).Result;
    }

    private static async Task<object?> InvokeActionAndResultFilters(RuntimeMethod method, object controller, ActionContext actionContext, IFilterMetadata[] filters, object?[] arguments, IActionResult? shortCircuit = null)
    {
        var dictionary = method.Action.MethodInfo.GetParameters().Select((p, i) => (p.Name!, arguments[i])).ToDictionary(x => x.Item1, x => x.Item2);
        var executing = new ActionExecutingContext(actionContext, filters.ToList(), dictionary, controller);
        async Task<ActionExecutedContext> Next(int index)
        {
            if (index == filters.Length)
            {
                for (var i = 0; i < arguments.Length; i++) arguments[i] = dictionary[method.Action.MethodInfo.GetParameters()[i].Name!];
                var value = await AwaitResult(method.Invoke(controller, arguments));
                return new ActionExecutedContext(actionContext, filters.ToList(), controller) { Result = value as IActionResult ?? new ObjectResult(value) };
            }
            if (filters[index] is IAsyncActionFilter asyncFilter)
            {
                ActionExecutedContext? completed = null;
                await asyncFilter.OnActionExecutionAsync(executing, async () => completed = await Next(index + 1));
                return completed ?? new ActionExecutedContext(actionContext, filters.ToList(), controller) { Canceled = true, Result = executing.Result };
            }
            if (filters[index] is IActionFilter filter)
            {
                filter.OnActionExecuting(executing);
                if (executing.Result is not null) return new ActionExecutedContext(actionContext, filters.ToList(), controller) { Canceled = true, Result = executing.Result };
                var completed = await Next(index + 1); filter.OnActionExecuted(completed); return completed;
            }
            return await Next(index + 1);
        }
        var actionResult = shortCircuit ?? (await Next(0)).Result;
        if (actionResult is null) return null;
        var resultExecuting = new ResultExecutingContext(actionContext, filters.ToList(), actionResult, controller);
        async Task<ResultExecutedContext> ResultNext(int index)
        {
            if (index == filters.Length) return new ResultExecutedContext(actionContext, filters.ToList(), resultExecuting.Result, controller);
            if (filters[index] is IAsyncResultFilter asyncFilter)
            {
                ResultExecutedContext? completed = null;
                await asyncFilter.OnResultExecutionAsync(resultExecuting, async () => completed = await ResultNext(index + 1));
                return completed ?? new ResultExecutedContext(actionContext, filters.ToList(), resultExecuting.Result, controller) { Canceled = true };
            }
            if (filters[index] is IResultFilter filter)
            {
                filter.OnResultExecuting(resultExecuting);
                if (resultExecuting.Cancel) return new ResultExecutedContext(actionContext, filters.ToList(), resultExecuting.Result, controller) { Canceled = true };
                var completed = await ResultNext(index + 1); filter.OnResultExecuted(completed); return completed;
            }
            return await ResultNext(index + 1);
        }
        return (await ResultNext(0)).Result;
    }

    private static async Task<object?> AwaitResult(object? pending)
    {
        if (pending is Task task) { await task.ConfigureAwait(false); return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null; }
        if (pending is ValueTask valueTask) { await valueTask.ConfigureAwait(false); return null; }
        if (pending is not null && pending.GetType().IsValueType && pending.GetType().IsGenericType && pending.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var convertedTask = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!; await convertedTask.ConfigureAwait(false); return convertedTask.GetType().GetProperty("Result")!.GetValue(convertedTask);
        }
        return pending;
    }

    private static object? UnwrapActionResult(object? raw, GrpcEmbedOptions options)
    {
        if (raw is ObjectResult objectResult)
        {
            if (objectResult.StatusCode is { } status && status >= 300) throw StatusException(status, objectResult.Value?.ToString(), options);
            return UnwrapActionResult(objectResult.Value, options);
        }
        if (raw is StatusCodeResult statusResult)
        {
            if (statusResult.StatusCode >= 300) throw StatusException(statusResult.StatusCode, null, options);
            return null;
        }
        if (raw is IActionResult result) throw new RpcException(new Status(StatusCode.Unimplemented, $"MVC result {result.GetType().Name} is not supported."));
        if (raw is not null && raw.GetType().IsGenericType && raw.GetType().GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            var innerResult = raw.GetType().GetProperty("Result")!.GetValue(raw);
            return innerResult is null ? raw.GetType().GetProperty("Value")!.GetValue(raw) : UnwrapActionResult(innerResult, options);
        }
        return raw;
    }

    private static RpcException StatusException(int status, string? detail, GrpcEmbedOptions options) => new(new Status(ToGrpc(options.StatusMapper?.Invoke(status) ?? status switch
    {
        400 => GrpcEmbedStatusCode.InvalidArgument, 401 => GrpcEmbedStatusCode.Unauthenticated, 403 => GrpcEmbedStatusCode.PermissionDenied,
        404 => GrpcEmbedStatusCode.NotFound, 409 => GrpcEmbedStatusCode.FailedPrecondition, 429 => GrpcEmbedStatusCode.ResourceExhausted,
        503 => GrpcEmbedStatusCode.Unavailable, _ => GrpcEmbedStatusCode.Internal
    }), detail ?? $"MVC action returned HTTP {status}."));

    private static StatusCode ToGrpc(GrpcEmbedStatusCode code) => code switch
    {
        GrpcEmbedStatusCode.Cancelled => StatusCode.Cancelled, GrpcEmbedStatusCode.InvalidArgument => StatusCode.InvalidArgument,
        GrpcEmbedStatusCode.DeadlineExceeded => StatusCode.DeadlineExceeded, GrpcEmbedStatusCode.NotFound => StatusCode.NotFound,
        GrpcEmbedStatusCode.AlreadyExists => StatusCode.AlreadyExists, GrpcEmbedStatusCode.PermissionDenied => StatusCode.PermissionDenied,
        GrpcEmbedStatusCode.ResourceExhausted => StatusCode.ResourceExhausted, GrpcEmbedStatusCode.FailedPrecondition => StatusCode.FailedPrecondition,
        GrpcEmbedStatusCode.Unauthenticated => StatusCode.Unauthenticated, GrpcEmbedStatusCode.Unavailable => StatusCode.Unavailable, _ => StatusCode.Internal
    };

    private static void Validate(object?[] arguments, RuntimeArgument[] parameters)
    {
        var errors = new List<ValidationResult>();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (parameters[i].Source == RuntimeArgumentSource.CancellationToken) continue;
            var parameter = parameters[i].Parameter;
            var parameterContext = new ValidationContext(arguments[i] ?? new object()) { MemberName = parameter.Name, DisplayName = parameter.Name ?? "parameter" };
            Validator.TryValidateValue(arguments[i]!, parameterContext, errors, parameters[i].ValidationAttributes);
            if (arguments[i] is not null) Validator.TryValidateObject(arguments[i]!, new ValidationContext(arguments[i]!), errors, true);
        }
        if (errors.Count > 0) throw new RpcException(new Status(StatusCode.InvalidArgument, string.Join("; ", errors.Select(x => x.ErrorMessage))));
    }
}

internal sealed class GrpcEmbedMethodProvider : IServiceMethodProvider<GrpcEmbedService>
{
    private readonly IServiceProvider _services;
    private readonly RuntimeRegistry _registry;
    public GrpcEmbedMethodProvider(IServiceProvider services, RuntimeRegistry registry) { _services = services; _registry = registry; }
    public void OnServiceMethodDiscovery(ServiceMethodProviderContext<GrpcEmbedService> context)
    {
        foreach (var method in _registry.Get(_services)) typeof(GrpcEmbedMethodProvider).GetMethod(nameof(Add), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(method.RequestType, method.ResponseType).Invoke(null, new object[] { context, method });
    }

    private static void Add<TRequest, TResponse>(ServiceMethodProviderContext<GrpcEmbedService> context, RuntimeMethod runtime)
        where TRequest : class where TResponse : class
    {
        var method = new Method<TRequest, TResponse>(MethodType.Unary, "GrpcEmbed." + runtime.ServiceName, runtime.MethodName, Marshaller<TRequest>(), Marshaller<TResponse>());
        var metadata = runtime.Action.EndpointMetadata.Where(x => x is not Microsoft.AspNetCore.Routing.IHttpMethodMetadata).ToArray();
        context.AddUnaryMethod(method, metadata, (service, request, call) => service.Invoke<TRequest, TResponse>(runtime, request, call));
    }

    private static Marshaller<T> Marshaller<T>() => Marshallers.Create<T>(
        (value, context) => { GrpcEmbedRuntimeModel.Model.Serialize(context.GetBufferWriter(), value); context.Complete(); },
        context => (T)GrpcEmbedRuntimeModel.Model.Deserialize(context.PayloadAsReadOnlySequence(), typeof(T), null, null)!);
}
