using System.Reflection;
using ProtoBuf.Meta;
using ProtoBuf;
using System.Globalization;

namespace GrpcEmbed;

public static class GrpcEmbedRuntimeModel
{
    private static readonly object Gate = new();
    private static readonly HashSet<Type> Configured = new();
    private static readonly RuntimeTypeModel RuntimeModel = RuntimeTypeModel.Create();
    public static RuntimeTypeModel Model => RuntimeModel;

    static GrpcEmbedRuntimeModel()
    {
        Model.DefaultCompatibilityLevel = CompatibilityLevel.Level300;
        Model.SetSurrogate<DateTimeOffset, string>(DateTimeOffsetToString, StringToDateTimeOffset, DataFormat.Default, CompatibilityLevel.Level300);
    }

    private static string DateTimeOffsetToString(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset StringToDateTimeOffset(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static void Configure(params Type[] rootTypes)
    {
        Validate(rootTypes);
        lock (Gate) foreach (var type in rootTypes) ConfigureCore(type);
    }

    /// <summary>Reject unsupported shapes before registering or exporting a partial wire model.</summary>
    public static void Validate(params Type[] rootTypes)
    {
        var seen = new HashSet<Type>();
        void Visit(Type declared)
        {
            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            if (!seen.Add(type) || IsBuiltIn(type) || type.IsEnum) return;
            if (typeof(System.Collections.IDictionary).IsAssignableFrom(type) || type.GetInterfaces().Append(type).Any(t =>
                t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))))
                throw new NotSupportedException($"GrpcEmbed cannot encode dictionary/map type {type}: map key/value presence has no supported exported wire contract. Use an explicit entry DTO collection or REST.");
            if (TryElement(type, out var element)) { Visit(element!); return; }
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)) Visit(property.PropertyType);
        }
        foreach (var root in rootTypes) Visit(root);
    }

    private static void ConfigureCore(Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (TryElement(type, out var element)) { ConfigureCore(element!); return; }
        if (IsBuiltIn(type) || type.IsEnum || !Configured.Add(type)) return;
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(x => x.CanRead && x.CanWrite && x.GetIndexParameters().Length == 0).ToArray();
        var numbers = GrpcEmbedFieldNumbers.Assign(properties);
        // Add(..., false) skips default behavior, including compatibility-level
        // resolution. Set it explicitly before adding fields so Guid/decimal
        // and DateTime/TimeSpan match the exported standard Protobuf schema.
        if (properties.Any(p => TryElement(p.PropertyType, out _)))
            CollectionWireSurrogates.Register(Model, type, properties, numbers);
        else
        {
            var meta = Model.Add(type, false, CompatibilityLevel.Level300);
            meta.UseConstructor = false;
            foreach (var property in properties) meta.Add(numbers[property.Name], property.Name);
        }
        foreach (var property in properties)
        {
            ConfigureCore(property.PropertyType);
        }
    }

    private static bool TryElement(Type type, out Type? element)
    {
        if (type != typeof(byte[]) && type.IsArray) { element = type.GetElementType(); return true; }
        var enumerable = type == typeof(string) ? null : type.GetInterfaces().Append(type).FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        element = enumerable?.GetGenericArguments()[0]; return element is not null;
    }

    private static bool IsBuiltIn(Type type) => type.IsPrimitive || type == typeof(string) || type == typeof(byte[]) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan);
}
