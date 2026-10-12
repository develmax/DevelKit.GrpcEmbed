using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Meta;
using System.Reflection;
using System.Reflection.Emit;
using ProtoBuf;
using System.Diagnostics.CodeAnalysis;

namespace GrpcEmbed.Client;

public sealed class GrpcEmbedClientOptions
{
    public GrpcEmbedClientRoutingOptions Routing { get; set; } = new();
    public GrpcEmbedClientContractOptions Contract { get; set; } = new();
    internal bool RequireHttp2ForContract { get; set; }
    public Uri Address { get; set; } = null!;
    public TimeSpan? DefaultTimeout { get; set; }
    public HttpMessageHandler? HttpHandler { get; set; }
    /// <summary>An existing configured HTTP client. Its lifetime remains owned by the caller.</summary>
    public HttpClient? HttpClient { get; set; }
    public bool DisposeHttpClient { get; set; }
    public string? ServiceName { get; set; }
    public bool StripAsyncSuffix { get; set; } = true;
    /// <summary>Target for local interface property getters; never used for RPC fallback.</summary>
    public object? LocalPropertyTarget { get; set; }
    public Func<MethodInfo, GrpcEmbedOperation?>? OperationResolver { get; set; }
    public Func<Metadata>? MetadataFactory { get; set; }
    public string? ExpectedSchemaHash { get; set; }
    public GrpcEmbedTransportMode TransportMode { get; set; } = GrpcEmbedTransportMode.GrpcOnly;
}

public static class GrpcEmbedClientExtensions
{
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed creates a DispatchProxy and protobuf request types at runtime.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed inspects contract methods and DTO members through reflection.")]
    public static IServiceCollection AddGrpcEmbedClient<TContract>(this IServiceCollection services, Action<GrpcEmbedClientOptions> configure) where TContract : class
    {
        var options = new GrpcEmbedClientOptions(); configure(options);
        Validate(options);
        return Register<TContract>(services, _ => options);
    }

#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed creates a DispatchProxy and protobuf request types at runtime.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed inspects contract methods and DTO members through reflection.")]
    public static IServiceCollection AddGrpcEmbedClient<TContract>(this IServiceCollection services,
        Action<IServiceProvider, GrpcEmbedClientOptions> configure) where TContract : class
    {
        return Register<TContract>(services, provider =>
        {
            var options = new GrpcEmbedClientOptions();
            configure(provider, options);
            Validate(options);
            return options;
        });
    }

    private static void Validate(GrpcEmbedClientOptions options)
    {
        if (options.Address is null) throw new InvalidOperationException("GrpcEmbed client Address is required.");
        if (options.TransportMode != GrpcEmbedTransportMode.GrpcOnly) throw new InvalidOperationException($"Unsupported GrpcEmbed transport mode '{options.TransportMode}'.");
        if (options.HttpClient is not null && options.HttpHandler is not null)
            throw new InvalidOperationException("Specify HttpClient or HttpHandler, not both.");
    }

    private static IServiceCollection Register<TContract>(IServiceCollection services,
        Func<IServiceProvider, GrpcEmbedClientOptions> configure) where TContract : class
    {
        services.AddSingleton<GrpcEmbedClientSettings<TContract>>(provider => new(configure(provider)));
        services.AddSingleton<GrpcEmbedChannel<TContract>>(provider =>
        {
            var options = provider.GetRequiredService<GrpcEmbedClientSettings<TContract>>().Options;
            return new GrpcEmbedChannel<TContract>(
            GrpcEmbedChannelFactory.Create(options));
        });
        services.AddSingleton<TContract>(sp => GrpcEmbedProxy<TContract>.Create(
            sp.GetRequiredService<GrpcEmbedChannel<TContract>>().Channel.CreateCallInvoker(),
            sp.GetRequiredService<GrpcEmbedClientSettings<TContract>>().Options));
        services.AddSingleton<IGrpcEmbedClientInitializer>(new GrpcEmbedClientInitializer<TContract>());
        return services;
    }

    /// <summary>Builds all registered proxies and channels before serving application requests.</summary>
    public static void InitializeGrpcEmbedClients(this IServiceProvider services)
    {
        foreach (var initializer in services.GetServices<IGrpcEmbedClientInitializer>())
            initializer.Initialize(services);
    }
}

internal sealed record GrpcEmbedClientSettings<TContract>(GrpcEmbedClientOptions Options);

internal interface IGrpcEmbedClientInitializer
{
    void Initialize(IServiceProvider services);
}

internal sealed class GrpcEmbedClientInitializer<TContract> : IGrpcEmbedClientInitializer where TContract : class
{
    public void Initialize(IServiceProvider services) => services.GetRequiredService<TContract>();
}

internal sealed class GrpcEmbedChannel<TContract> : IDisposable where TContract : class
{
    public GrpcEmbedChannel(GrpcChannel channel) => Channel = channel;
    public GrpcChannel Channel { get; }
    public void Dispose() => Channel.Dispose();
}

internal class GrpcEmbedProxy<TContract> : DispatchProxy where TContract : class
{
    private CallInvoker _invoker = null!;
    private GrpcEmbedClientOptions _options = null!;
    private ClientContractState _contractState = null!;
    private readonly Dictionary<MethodInfo, Func<object?[]?, object>> _calls = new();
    private readonly Dictionary<MethodInfo, GrpcEmbedOperation> _operations = new();
    private sealed record BoundRoute(ClientContractState.PreparedContract Contract, Func<object?[], string> Path);

    public static TContract Create(CallInvoker invoker, GrpcEmbedClientOptions options)
    {
        var proxy = Create<TContract, GrpcEmbedProxy<TContract>>();
        var implementation = (GrpcEmbedProxy<TContract>)(object)proxy;
        implementation._invoker = invoker; implementation._options = options;
        implementation._contractState = new ClientContractState(options);
        implementation.Build();
        if (options.Contract.Fetch == GrpcEmbedContractFetch.Startup)
            implementation._contractState.PreloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _calls[targetMethod!](args);

    private void Build()
    {
        var methodNames = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var contractMethod in typeof(TContract).GetInterfaces().Append(typeof(TContract))
                     .SelectMany(type => type.GetMethods()).Distinct())
        {
            if (contractMethod.IsSpecialName && contractMethod.Name.StartsWith("get_", StringComparison.Ordinal)
                && contractMethod.GetParameters().Length == 0 && _options.LocalPropertyTarget is { } target
                && contractMethod.DeclaringType!.IsInstanceOfType(target))
            {
                var instance = System.Linq.Expressions.Expression.Constant(target, contractMethod.DeclaringType);
                var getter = System.Linq.Expressions.Expression.Lambda<Func<object>>(
                    System.Linq.Expressions.Expression.Convert(
                        System.Linq.Expressions.Expression.Call(instance, contractMethod), typeof(object))).Compile();
                _calls[contractMethod] = _ => getter();
                continue;
            }
            if (_options.OperationResolver is { } resolve)
            {
                var operation = resolve(contractMethod);
                if (operation is null)
                {
                    _calls[contractMethod] = MissingOperation(contractMethod);
                    continue;
                }
                _operations.Add(contractMethod, operation);
            }
            var response = UnwrapAsync(contractMethod.ReturnType) ?? throw new NotSupportedException($"{contractMethod} must return Task<T> or ValueTask<T>.");
            var allParameters = contractMethod.GetParameters();
            var cancellationParameters = allParameters.Where(p => p.ParameterType == typeof(CancellationToken)).ToArray();
            if (cancellationParameters.Length > 1 || cancellationParameters.Length == 1 && cancellationParameters[0].Position != allParameters.Length - 1)
                throw new NotSupportedException($"{contractMethod} may have at most one CancellationToken and it must be the last parameter.");
            var wireMethodName = _operations.TryGetValue(contractMethod, out var resolved)
                ? resolved.Service + "/" + resolved.Method : GetMethodName(contractMethod);
            if (methodNames.TryGetValue(wireMethodName, out var previous))
            {
                var previousParameters = previous.GetParameters();
                var samePayload = previousParameters.Where(p => p.ParameterType != typeof(CancellationToken))
                    .Select(p => (p.Name, p.ParameterType))
                    .SequenceEqual(allParameters.Where(p => p.ParameterType != typeof(CancellationToken))
                        .Select(p => (p.Name, p.ParameterType)));
                if (!samePayload || previous.ReturnType != contractMethod.ReturnType ||
                    previousParameters.Length == allParameters.Length)
                    throw new NotSupportedException($"Contract {typeof(TContract)} contains ambiguous gRPC method '{wireMethodName}'. Only overloads differing by a trailing CancellationToken can share an operation.");
            }
            else methodNames.Add(wireMethodName, contractMethod);
            var parameters = allParameters.Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(typeof(TContract).AssemblyQualifiedName + "|" + contractMethod)));
            var request = ClientRequestTypes.Create("Request_" + identity, parameters);
            var wireResponse = ClientResponseTypes.RequiresWrapper(response)
                ? ClientResponseTypes.Create("Response_" + identity, response)
                : response;
            GrpcEmbedRuntimeModel.Configure(request, wireResponse, response);
            _calls[contractMethod] = (Func<object?[]?, object>)typeof(GrpcEmbedProxy<TContract>).GetMethod(nameof(BuildCall), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(request, wireResponse, response).Invoke(this, new object[] { contractMethod, parameters })!;
        }
    }

    private Func<object?[]?, object> BuildCall<TRequest, TWireResponse, TResponse>(MethodInfo contract, ParameterInfo[] parameters) where TRequest : class where TWireResponse : class
    {
        var serviceAttribute = typeof(TContract).GetCustomAttribute<GrpcNameAttribute>();
        var service = _options.ServiceName ?? serviceAttribute?.Name ?? typeof(TContract).Name.TrimStart('I');
        if (_options.ServiceName is null && serviceAttribute is null && service.EndsWith("Api", StringComparison.Ordinal)) service = service[..^3];
        var methodName = GetMethodName(contract);
        if (_operations.TryGetValue(contract, out var operation))
        {
            service = operation.Service;
            methodName = operation.Method;
        }
        var method = new Method<TRequest, TWireResponse>(MethodType.Unary, "GrpcEmbed." + service, methodName, Marshaller<TRequest>(), Marshaller<TWireResponse>());
        if (_options.Contract.Fetch != GrpcEmbedContractFetch.Never)
        {
            _ = ContractShapeCache<TRequest>.Hash;
            _ = ContractShapeCache<TWireResponse>.Hash;
        }
        var returnsValueTask = contract.ReturnType.IsGenericType && contract.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
        var requestFactory = ClientRequestFactories<TRequest>.Build(parameters);
        var contractService = _options.ServiceName is null && serviceAttribute is null && _options.OperationResolver is null
            ? null : method.ServiceName;
        BoundRoute? boundRoute = null;
        string RoutePath(ClientContractState.PreparedContract prepared, object?[] args)
        {
            var current = Volatile.Read(ref boundRoute);
            if (current is null || !ReferenceEquals(current.Contract, prepared))
            {
                current = new BoundRoute(prepared, ClientRoute.Bind(_options, prepared.Route, prepared.Service, method.Name, parameters));
                Volatile.Write(ref boundRoute, current);
            }
            return current.Path(args);
        }
        return args =>
        {
            var task = CallCore<TRequest, TWireResponse, TResponse>(method, contractService, parameters.Length,
                requestFactory, RoutePath, args ?? Array.Empty<object?>());
            if (contract.ReturnType == typeof(ValueTask)) return new ValueTask(task);
            return returnsValueTask ? new ValueTask<TResponse>(task) : task;
        };
    }

    private async Task<TResponse> CallCore<TRequest, TWireResponse, TResponse>(Method<TRequest, TWireResponse> method,
        string? contractService, int parameterCount, Func<object?[], TRequest> requestFactory,
        Func<ClientContractState.PreparedContract, object?[], string> routePath, object?[] args) where TRequest : class where TWireResponse : class
    {
        var request = requestFactory(args);
        var cancellation = contractCancellation(parameterCount, args);
        var token = cancellation;
        var deadline = _options.DefaultTimeout is { } timeout ? DateTime.UtcNow + timeout : (DateTime?)null;
        for (var attempt = 0; ; attempt++)
        {
        token.ThrowIfCancellationRequested();
        var prepared = _options.Contract.Fetch == GrpcEmbedContractFetch.Never ? null :
            await _contractState.PrepareAsync(contractService, method.Name,
                ContractShapeCache<TRequest>.Hash, ContractShapeCache<TWireResponse>.Hash, token, deadline).ConfigureAwait(false);
        var hash = prepared?.Hash;
        var activeMethod = prepared is not null && prepared.Service != method.ServiceName
            ? ResolvedContractMethods<TRequest, TWireResponse>.Get(method, prepared.Service) : method;
        var metadata = _options.MetadataFactory?.Invoke();
        var routedCall = _options.Routing.Mode != GrpcEmbedRoutingMode.Native;
        if (routedCall || _options.Contract.SendHash)
        {
            var guarded = new Metadata();
            if (metadata is not null)
                foreach (var item in metadata)
                    if ((!routedCall || item.Key != GrpcEmbedContractHeaders.Operation && item.Key != GrpcEmbedChannelFactory.PathHeader) &&
                        (!_options.Contract.SendHash || item.Key != GrpcEmbedContractHeaders.RequestHash)) guarded.Add(item);
            if (routedCall)
            {
                if (prepared is null) throw new InvalidOperationException("URL routing requires a fetched contract.");
                guarded.Add(GrpcEmbedContractHeaders.Operation, activeMethod.ServiceName + "/" + activeMethod.Name);
                guarded.Add(GrpcEmbedChannelFactory.PathHeader, routePath(prepared, args));
            }
            if (_options.Contract.SendHash) guarded.Add(GrpcEmbedContractHeaders.RequestHash, hash!);
            metadata = guarded;
        }
        using var call = _invoker.AsyncUnaryCall(activeMethod, null, new CallOptions(headers: metadata, deadline: deadline, cancellationToken: token), request);
        try
        {
        if (_options.ExpectedSchemaHash is not null)
        {
            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var actual = headers.FirstOrDefault(x => x.Key == "grpcembed-schema-hash")?.Value;
            if (!string.Equals(actual, _options.ExpectedSchemaHash, StringComparison.OrdinalIgnoreCase))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"GrpcEmbed schema hash mismatch. Expected '{_options.ExpectedSchemaHash}', received '{actual ?? "<missing>"}'."));
        }
        var response = await call.ResponseAsync.ConfigureAwait(false);
        if (typeof(TWireResponse) == typeof(TResponse)) return (TResponse)(object)response;
        return ClientResponseReaders<TWireResponse, TResponse>.Read(response);
        }
        catch (RpcException error) when (_options.Contract.SendHash &&
            _options.Contract.OnMismatch == GrpcEmbedContractMismatch.RefreshAndRetry &&
            attempt < _options.Contract.MaxRetries && error.StatusCode == StatusCode.FailedPrecondition &&
            error.Trailers.GetValue(GrpcEmbedContractHeaders.Rejection) == GrpcEmbedContractHeaders.NotExecuted &&
            error.Trailers.GetValue(GrpcEmbedContractHeaders.ServerHash) is { Length: 64 })
        {
            await _contractState.RefreshAsync(hash, token, deadline).ConfigureAwait(false);
        }
        }
    }
    private static CancellationToken contractCancellation(int nonCancellationCount, object?[] args) => args.Length > nonCancellationCount && args[^1] is CancellationToken token ? token : default;
    private string GetMethodName(MethodInfo method)
    {
        if (method.GetCustomAttribute<GrpcNameAttribute>() is { } name) return name.Name;
        return _options.StripAsyncSuffix && method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5] : method.Name;
    }
    private static Func<object?[]?, object> MissingOperation(MethodInfo method)
    {
        var error = new RpcException(new Status(StatusCode.Unimplemented,
            $"No published gRPC operation matches {method.DeclaringType?.Name}.{method.Name}. REST fallback is disabled."));
        if (method.ReturnType == typeof(Task)) return _ => Task.FromException(error);
        if (method.ReturnType == typeof(ValueTask)) return _ => new ValueTask(Task.FromException(error));
        var result = UnwrapAsync(method.ReturnType)
            ?? throw new NotSupportedException($"Unsupported client member {method}.");
        return (Func<object?[]?, object>)typeof(GrpcEmbedProxy<TContract>)
            .GetMethod(nameof(MissingAsyncOperation), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(result).Invoke(null, new object[] { error,
                method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>) })!;
    }
    private static Func<object?[]?, object> MissingAsyncOperation<TResult>(Exception error, bool valueTask)
        => _ => valueTask ? (object)new ValueTask<TResult>(Task.FromException<TResult>(error)) : Task.FromException<TResult>(error);
    private static Type? UnwrapAsync(Type type) => type == typeof(Task) || type == typeof(ValueTask)
        ? typeof(GrpcEmbedEmpty)
        : type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GetGenericArguments()[0] : null;
    private static Marshaller<T> Marshaller<T>() => Marshallers.Create<T>((value, context) => { GrpcEmbedRuntimeModel.Model.Serialize(context.GetBufferWriter(), value); context.Complete(); }, context => (T)GrpcEmbedRuntimeModel.Model.Deserialize(context.PayloadAsReadOnlySequence(), typeof(T), null, null)!);
}

internal static class ClientRequestFactories<TRequest> where TRequest : class
{
    public static Func<object?[], TRequest> Build(ParameterInfo[] parameters)
    {
            var input = System.Linq.Expressions.Expression.Parameter(typeof(object[]));
            var bindings = parameters.Select((p, i) => System.Linq.Expressions.Expression.Bind(
                typeof(TRequest).GetProperty(p.Name!)!,
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.ArrayIndex(input, System.Linq.Expressions.Expression.Constant(i)), p.ParameterType)));
            return System.Linq.Expressions.Expression.Lambda<Func<object?[], TRequest>>(
                System.Linq.Expressions.Expression.MemberInit(System.Linq.Expressions.Expression.New(typeof(TRequest)), bindings), input).Compile();
    }
}

internal static class ClientResponseReaders<TWireResponse, TResponse> where TWireResponse : class
{
    private static readonly Func<TWireResponse, TResponse> Reader = Build();
    public static TResponse Read(TWireResponse value) => Reader(value);
    private static Func<TWireResponse, TResponse> Build()
    {
        var input = System.Linq.Expressions.Expression.Parameter(typeof(TWireResponse));
        var property = System.Linq.Expressions.Expression.Property(input, "Value");
        // A successful wrapped response cannot represent a null MVC result: the server
        // rejects it with NotFound. Protobuf omits empty strings/bytes/repeated fields,
        // so restore their empty CLR value, not a null accidentally left by the reader.
        var empty = EmptyValue(typeof(TResponse));
        var result = empty is null ? (System.Linq.Expressions.Expression)property
            : System.Linq.Expressions.Expression.Coalesce(property, empty);
        return System.Linq.Expressions.Expression.Lambda<Func<TWireResponse, TResponse>>(result, input).Compile();
    }

    private static System.Linq.Expressions.Expression? EmptyValue(Type type)
    {
        if (type == typeof(string)) return System.Linq.Expressions.Expression.Constant(string.Empty);
        if (type.IsArray && type.GetArrayRank() == 1)
            return System.Linq.Expressions.Expression.NewArrayBounds(type.GetElementType()!, System.Linq.Expressions.Expression.Constant(0));
        if (type.IsValueType) return null;
        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(x =>
            x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is null) return null;
        var element = enumerable.GetGenericArguments()[0];
        Type? concrete = !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null ? type : null;
        if (concrete is null)
        {
            var candidates = new List<Type> { typeof(List<>).MakeGenericType(element), typeof(HashSet<>).MakeGenericType(element) };
            if (element.IsGenericType && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                candidates.Add(typeof(Dictionary<,>).MakeGenericType(element.GetGenericArguments()));
            concrete = candidates.FirstOrDefault(type.IsAssignableFrom);
        }
        if (concrete is not null)
            return System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.New(concrete), type);
        var listType = typeof(List<>).MakeGenericType(element);
        var listInterface = typeof(IList<>).MakeGenericType(element);
        var constructor = type.GetConstructor(new[] { listInterface });
        return constructor is null ? null : System.Linq.Expressions.Expression.New(constructor,
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.New(listType), listInterface));
    }
}

internal static class ClientRequestTypes
{
    private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GrpcEmbed.ClientContracts"), AssemblyBuilderAccess.Run).DefineDynamicModule("Contracts");
    private static readonly Dictionary<string, Type> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    public static Type Create(string name, ParameterInfo[] parameters)
    {
        lock (Gate)
        {
        if (Cache.TryGetValue(name, out var existing)) return existing;
        var type = Module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed); type.DefineDefaultConstructor(MethodAttributes.Public);
        var numbers = GrpcEmbedFieldNumbers.Assign(parameters);
        foreach (var parameter in parameters)
        {
            var field = type.DefineField("_" + parameter.Name, parameter.ParameterType, FieldAttributes.Private);
            var property = type.DefineProperty(parameter.Name!, PropertyAttributes.None, parameter.ParameterType, null);
            property.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProtoMemberAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { numbers[parameter.Name!] }));
            var get = type.DefineMethod("get_" + parameter.Name, MethodAttributes.Public | MethodAttributes.SpecialName, parameter.ParameterType, Type.EmptyTypes);
            var il = get.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var set = type.DefineMethod("set_" + parameter.Name, MethodAttributes.Public | MethodAttributes.SpecialName, null, new[] { parameter.ParameterType });
            il = set.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            property.SetGetMethod(get); property.SetSetMethod(set);
        }
        var created = type.CreateType()!; Cache.Add(name, created); return created;
        }
    }
}

internal static class ClientResponseTypes
{
    private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GrpcEmbed.ClientResponses"), AssemblyBuilderAccess.Run).DefineDynamicModule("Responses");
    private static readonly Dictionary<string, Type> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool RequiresWrapper(Type type) =>
        type.IsValueType || type == typeof(string) || type == typeof(byte[]) || type.IsArray ||
        type.GetInterfaces().Append(type).Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    public static Type Create(string name, Type valueType)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var existing)) return existing;
            var type = Module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed); type.DefineDefaultConstructor(MethodAttributes.Public);
            var field = type.DefineField("_value", valueType, FieldAttributes.Private);
            var property = type.DefineProperty("Value", PropertyAttributes.None, valueType, null);
            property.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProtoMemberAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { GrpcEmbedFieldNumbers.Assign(new[] { "Value" })["Value"] }));
            var get = type.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName, valueType, Type.EmptyTypes);
            var il = get.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var set = type.DefineMethod("set_Value", MethodAttributes.Public | MethodAttributes.SpecialName, null, new[] { valueType });
            il = set.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            property.SetGetMethod(get); property.SetSetMethod(set);
            var created = type.CreateType()!; Cache.Add(name, created); return created;
        }
    }
}
