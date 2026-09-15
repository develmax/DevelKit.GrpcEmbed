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
    public Uri Address { get; set; } = null!;
    public TimeSpan? DefaultTimeout { get; set; }
    public HttpMessageHandler? HttpHandler { get; set; }
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
        if (options.Address is null) throw new InvalidOperationException("GrpcEmbed client Address is required.");
        if (options.TransportMode != GrpcEmbedTransportMode.GrpcOnly) throw new InvalidOperationException($"Unsupported GrpcEmbed transport mode '{options.TransportMode}'.");
        services.AddSingleton(new GrpcEmbedChannel<TContract>(GrpcChannel.ForAddress(options.Address, new GrpcChannelOptions { HttpHandler = options.HttpHandler })));
        services.AddSingleton<TContract>(sp => GrpcEmbedProxy<TContract>.Create(sp.GetRequiredService<GrpcEmbedChannel<TContract>>().Channel.CreateCallInvoker(), options));
        return services;
    }
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
    private readonly Dictionary<MethodInfo, Func<object?[]?, object>> _calls = new();

    public static TContract Create(CallInvoker invoker, GrpcEmbedClientOptions options)
    {
        var proxy = Create<TContract, GrpcEmbedProxy<TContract>>();
        var implementation = (GrpcEmbedProxy<TContract>)(object)proxy;
        implementation._invoker = invoker; implementation._options = options; implementation.Build();
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _calls[targetMethod!](args);

    private void Build()
    {
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contractMethod in typeof(TContract).GetMethods())
        {
            var response = UnwrapAsync(contractMethod.ReturnType) ?? throw new NotSupportedException($"{contractMethod} must return Task<T> or ValueTask<T>.");
            var allParameters = contractMethod.GetParameters();
            var cancellationParameters = allParameters.Where(p => p.ParameterType == typeof(CancellationToken)).ToArray();
            if (cancellationParameters.Length > 1 || cancellationParameters.Length == 1 && cancellationParameters[0].Position != allParameters.Length - 1)
                throw new NotSupportedException($"{contractMethod} may have at most one CancellationToken and it must be the last parameter.");
            var wireMethodName = contractMethod.GetCustomAttribute<GrpcNameAttribute>()?.Name ?? contractMethod.Name;
            if (!methodNames.Add(wireMethodName)) throw new NotSupportedException($"Contract {typeof(TContract)} contains duplicate gRPC method name '{wireMethodName}'. Use GrpcNameAttribute to assign unique names; CLR overloads cannot map to one gRPC method.");
            var parameters = allParameters.Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
            var request = ClientRequestTypes.Create(typeof(TContract).Name + "_" + contractMethod.Name, parameters);
            var wireResponse = ClientResponseTypes.RequiresWrapper(response)
                ? ClientResponseTypes.Create(typeof(TContract).Name + "_" + contractMethod.Name + "_Response", response)
                : response;
            GrpcEmbedRuntimeModel.Configure(request, wireResponse, response);
            _calls[contractMethod] = (Func<object?[]?, object>)typeof(GrpcEmbedProxy<TContract>).GetMethod(nameof(BuildCall), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(request, wireResponse, response).Invoke(this, new object[] { contractMethod, parameters })!;
        }
    }

    private Func<object?[]?, object> BuildCall<TRequest, TWireResponse, TResponse>(MethodInfo contract, ParameterInfo[] parameters) where TRequest : class where TWireResponse : class
    {
        var serviceAttribute = typeof(TContract).GetCustomAttribute<GrpcNameAttribute>();
        var service = serviceAttribute?.Name ?? typeof(TContract).Name.TrimStart('I');
        if (serviceAttribute is null && service.EndsWith("Api", StringComparison.Ordinal)) service = service[..^3];
        var methodName = contract.GetCustomAttribute<GrpcNameAttribute>()?.Name ?? contract.Name;
        var method = new Method<TRequest, TWireResponse>(MethodType.Unary, "GrpcEmbed." + service, methodName, Marshaller<TRequest>(), Marshaller<TWireResponse>());
        var returnsValueTask = contract.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
        return args =>
        {
            var task = CallCore<TRequest, TWireResponse, TResponse>(method, parameters, args ?? Array.Empty<object?>());
            return returnsValueTask ? new ValueTask<TResponse>(task) : task;
        };
    }

    private async Task<TResponse> CallCore<TRequest, TWireResponse, TResponse>(Method<TRequest, TWireResponse> method, ParameterInfo[] parameters, object?[] args) where TRequest : class where TWireResponse : class
    {
        var request = ClientRequestFactories<TRequest>.Create(parameters, args);
        var token = contractCancellation(parameters.Length, args);
        var deadline = _options.DefaultTimeout is { } timeout ? DateTime.UtcNow + timeout : (DateTime?)null;
        var call = _invoker.AsyncUnaryCall(method, null, new CallOptions(headers: _options.MetadataFactory?.Invoke(), deadline: deadline, cancellationToken: token), request);
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
    private static CancellationToken contractCancellation(int nonCancellationCount, object?[] args) => args.Length > nonCancellationCount && args[^1] is CancellationToken token ? token : default;
    private static Type? UnwrapAsync(Type type) => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? type.GetGenericArguments()[0] : null;
    private static Marshaller<T> Marshaller<T>() => Marshallers.Create<T>((value, context) => { GrpcEmbedRuntimeModel.Model.Serialize(context.GetBufferWriter(), value); context.Complete(); }, context => (T)GrpcEmbedRuntimeModel.Model.Deserialize(context.PayloadAsReadOnlySequence(), typeof(T), null, null)!);
}

internal static class ClientRequestFactories<TRequest> where TRequest : class
{
    private static ParameterInfo[]? _parameters;
    private static Func<object?[], TRequest>? _factory;
    public static TRequest Create(ParameterInfo[] parameters, object?[] values)
    {
        if (_factory is null || !ReferenceEquals(_parameters, parameters))
        {
            var input = System.Linq.Expressions.Expression.Parameter(typeof(object[]));
            var bindings = parameters.Select((p, i) => System.Linq.Expressions.Expression.Bind(
                typeof(TRequest).GetProperty(p.Name!)!,
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.ArrayIndex(input, System.Linq.Expressions.Expression.Constant(i)), p.ParameterType)));
            _factory = System.Linq.Expressions.Expression.Lambda<Func<object?[], TRequest>>(
                System.Linq.Expressions.Expression.MemberInit(System.Linq.Expressions.Expression.New(typeof(TRequest)), bindings), input).Compile();
            _parameters = parameters;
        }
        return _factory(values);
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
        return System.Linq.Expressions.Expression.Lambda<Func<TWireResponse, TResponse>>(property, input).Compile();
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
