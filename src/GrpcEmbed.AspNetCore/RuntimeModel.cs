using System.Reflection;
using System.Reflection.Emit;
using System.Linq.Expressions;
using ProtoBuf;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.ComponentModel.DataAnnotations;

namespace GrpcEmbed.AspNetCore;

internal sealed record RuntimeMethod(
    ControllerActionDescriptor Action, string ServiceName, string MethodName,
    Type RequestType, Type ResponseType, Type ActionResponseType, IReadOnlyList<ParameterInfo> Parameters,
    Func<object, object?[], object?> Invoke, Func<object, object?>[] RequestReaders,
    RuntimeArgument[] Arguments, Func<object, object>? WrapResponse);

internal enum RuntimeArgumentSource { Transport, CancellationToken, HttpContext, ClaimsPrincipal, HttpRequest, HttpResponse, Services }

internal sealed record RuntimeArgument(
    ParameterInfo Parameter, RuntimeArgumentSource Source, int TransportIndex,
    IReadOnlyList<ValidationAttribute> ValidationAttributes);

internal static class ActionDelegates
{
    public static Func<object, object?[], object?> Compile(MethodInfo method)
    {
        var target = Expression.Parameter(typeof(object));
        var arguments = Expression.Parameter(typeof(object[]));
        var callArguments = method.GetParameters().Select((p, i) => Expression.Convert(Expression.ArrayIndex(arguments, Expression.Constant(i)), p.ParameterType));
        var call = Expression.Call(Expression.Convert(target, method.DeclaringType!), method, callArguments);
        Expression result = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));
        return Expression.Lambda<Func<object, object?[], object?>>(result, target, arguments).Compile();
    }

    public static Func<object, object?> CompileGetter(Type declaringType, string propertyName)
    {
        var target = Expression.Parameter(typeof(object));
        var property = Expression.Property(Expression.Convert(target, declaringType), propertyName);
        return Expression.Lambda<Func<object, object?>>(Expression.Convert(property, typeof(object)), target).Compile();
    }

    public static Func<object, object> CompileWrapper(Type wrapperType, Type valueType)
    {
        var value = Expression.Parameter(typeof(object));
        var created = Expression.Variable(wrapperType);
        var body = Expression.Block(new[] { created },
            Expression.Assign(created, Expression.New(wrapperType)),
            Expression.Assign(Expression.Property(created, "Value"), Expression.Convert(value, valueType)),
            Expression.Convert(created, typeof(object)));
        return Expression.Lambda<Func<object, object>>(body, value).Compile();
    }
}

internal static class RuntimeRequestTypes
{
    private static readonly AssemblyBuilder Assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GrpcEmbed.RuntimeContracts"), AssemblyBuilderAccess.Run);
    private static readonly ModuleBuilder Module = Assembly.DefineDynamicModule("Contracts");
    private static readonly Dictionary<string, Type> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static Type Create(string name, IReadOnlyList<ParameterInfo> parameters)
    {
        var runtimeName = Sanitize(name);
        lock (Gate)
        {
        if (Cache.TryGetValue(runtimeName, out var existing)) return existing;
        var type = Module.DefineType(runtimeName, TypeAttributes.Public | TypeAttributes.Sealed);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var numbers = GrpcEmbedFieldNumbers.Assign(parameters);
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var field = type.DefineField("_" + parameter.Name, parameter.ParameterType, FieldAttributes.Private);
            var property = type.DefineProperty(parameter.Name!, PropertyAttributes.None, parameter.ParameterType, null);
            property.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProtoMemberAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { numbers[parameter.Name!] }));
            var getter = type.DefineMethod("get_" + parameter.Name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, parameter.ParameterType, Type.EmptyTypes);
            var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var setter = type.DefineMethod("set_" + parameter.Name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, new[] { parameter.ParameterType });
            il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            property.SetGetMethod(getter); property.SetSetMethod(setter);
        }
        var created = type.CreateType()!; Cache.Add(runtimeName, created); return created;
        }
    }

    private static string Sanitize(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

internal static class RuntimeResponseTypes
{
    private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GrpcEmbed.RuntimeResponses"), AssemblyBuilderAccess.Run).DefineDynamicModule("Responses");
    private static readonly Dictionary<string, Type> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool RequiresWrapper(Type type) =>
        type.IsValueType || type == typeof(string) || type == typeof(byte[]) || type.IsArray ||
        type.GetInterfaces().Append(type).Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    public static Type GetOrCreate(string name, Type valueType)
    {
        var runtimeName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        lock (Gate)
        {
            if (Cache.TryGetValue(runtimeName, out var existing)) return existing;
            var type = Module.DefineType(runtimeName, TypeAttributes.Public | TypeAttributes.Sealed);
            type.DefineDefaultConstructor(MethodAttributes.Public);
            var field = type.DefineField("_value", valueType, FieldAttributes.Private);
            var property = type.DefineProperty("Value", PropertyAttributes.None, valueType, null);
            property.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProtoMemberAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { GrpcEmbedFieldNumbers.Assign(new[] { "Value" })["Value"] }));
            var getter = type.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, valueType, Type.EmptyTypes);
            var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var setter = type.DefineMethod("set_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, new[] { valueType });
            il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            property.SetGetMethod(getter); property.SetSetMethod(setter);
            var created = type.CreateType()!; Cache.Add(runtimeName, created); return created;
        }
    }
}

internal static class ReturnTypes
{
    public static Type? Unwrap(Type type)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask)) return typeof(GrpcEmbedEmpty);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))) type = type.GetGenericArguments()[0];
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Microsoft.AspNetCore.Mvc.ActionResult`1") type = type.GetGenericArguments()[0];
        if (type == typeof(Microsoft.AspNetCore.Mvc.IActionResult)) return typeof(GrpcEmbedEmpty);
        return type;
    }
}
