using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using ProtoBuf;
using ProtoBuf.Meta;

namespace GrpcEmbed;

// protobuf-net exposes presence wrappers as attributes, not as public ValueMember
// switches. Build an attributed wire surrogate once at startup and compile shallow
// conversion delegates. No private protobuf-net API or per-call reflection is used.
internal static class CollectionWireSurrogates
{
    private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName("GrpcEmbed.CollectionWire"), AssemblyBuilderAccess.Run).DefineDynamicModule("Wire");
    private static int sequence;

    internal static void Register(RuntimeTypeModel model, Type dto, PropertyInfo[] properties, IReadOnlyDictionary<string, int> numbers)
    {
        var builder = Module.DefineType("Wire" + ++sequence,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProtoContractAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
        foreach (var property in properties)
        {
            var field = builder.DefineField(property.Name, property.PropertyType, FieldAttributes.Public);
            field.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProtoMemberAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { numbers[property.Name] }));
            var element = Element(property.PropertyType);
            if (element is null) continue;
            field.SetCustomAttribute(new CustomAttributeBuilder(typeof(NullWrappedCollectionAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
            if (!element.IsValueType || Nullable.GetUnderlyingType(element) is not null)
                field.SetCustomAttribute(new CustomAttributeBuilder(typeof(NullWrappedValueAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
        }
        var wire = builder.CreateType()!;
        model.Add(wire, true, CompatibilityLevel.Level300);
        typeof(CollectionWireSurrogates).GetMethod(nameof(RegisterConversions), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(dto, wire).Invoke(null, new object[] { model, properties });
    }

    private static void RegisterConversions<T, TWire>(RuntimeTypeModel model, PropertyInfo[] properties) where TWire : new()
    {
        var source = Expression.Parameter(typeof(T), "source");
        var bindings = properties.Select(p => Expression.Bind(typeof(TWire).GetField(p.Name)!, Expression.Property(source, p)));
        Expression copy = Expression.MemberInit(Expression.New(typeof(TWire)), bindings);
        if (!typeof(T).IsValueType) copy = Expression.Condition(Expression.Equal(source, Expression.Constant(null, typeof(T))), Expression.Default(typeof(TWire)), copy);
        var toWire = Expression.Lambda<Func<T, TWire>>(copy, source).Compile();
        var input = Expression.Parameter(typeof(TWire), "wire");
        var target = Expression.Variable(typeof(T), "target");
#pragma warning disable SYSLIB0050 // Required on all supported TFMs; deliberately skip DTO initializers.
        var create = Expression.Convert(Expression.Call(typeof(FormatterServices), nameof(FormatterServices.GetUninitializedObject), Type.EmptyTypes, Expression.Constant(typeof(T))), typeof(T));
#pragma warning restore SYSLIB0050
        var assignments = properties.Select(p => (Expression)Expression.Assign(Expression.Property(target, p), Expression.Field(input, p.Name))).ToList();
        assignments.Insert(0, Expression.Assign(target, create)); assignments.Add(target);
        var fromWire = Expression.Lambda<Func<TWire, T>>(Expression.Block(new[] { target }, assignments), input).Compile();
        WireConversions<T, TWire>.ToWireDelegate = toWire;
        WireConversions<T, TWire>.FromWireDelegate = fromWire;
        model.SetSurrogate<T, TWire>(WireConversions<T, TWire>.ToWire, WireConversions<T, TWire>.FromWire, DataFormat.Default, CompatibilityLevel.Level300);
    }

    private static Type? Element(Type type) => type == typeof(string) || type == typeof(byte[]) ? null : type.IsArray
        ? type.GetElementType() : type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
}

internal static class WireConversions<T, TWire>
{
    internal static Func<T, TWire> ToWireDelegate = null!;
    internal static Func<TWire, T> FromWireDelegate = null!;
    public static TWire ToWire(T value) => ToWireDelegate(value);
    public static T FromWire(TWire value) => FromWireDelegate(value);
}
