using System.Text.Json;

namespace GrpcEmbed.AspNetCore;

internal sealed record SchemaManifest(IReadOnlyDictionary<string, SchemaManifestType> Types, IReadOnlyDictionary<string, SchemaManifestService>? Services = null, string? SchemaHash = null);
internal sealed record SchemaManifestType(IReadOnlyDictionary<string, SchemaManifestField> Fields, IReadOnlyList<int>? Reserved = null);
internal sealed record SchemaManifestField(int Number, string ClrType);
internal sealed record SchemaManifestService(IReadOnlyDictionary<string, SchemaManifestMethod> Methods);
internal sealed record SchemaManifestMethod(string RequestType, string ResponseType, IReadOnlyList<SchemaManifestParameter>? Parameters = null, string? RequestShapeHash = null, string? ResponseShapeHash = null);
internal sealed record SchemaManifestParameter(string Name, string ClrType, string? BindingSource, int FieldNumber);

internal static class SchemaManifestManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SchemaManifest Create(IReadOnlyList<RuntimeMethod> methods, bool computeHash = true)
    {
        var roots = methods.SelectMany(x => new[] { x.RequestType, x.ResponseType });
        var types = new Dictionary<string, SchemaManifestType>(StringComparer.Ordinal);
        foreach (var root in roots) Add(root, types);
        var services = methods.GroupBy(x => x.ServiceName).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(
            group => group.Key,
            group => new SchemaManifestService(group.OrderBy(x => x.MethodName, StringComparer.Ordinal).ToDictionary(
                method => method.MethodName,
                method => new SchemaManifestMethod(TypeIdentity(method.RequestType), TypeIdentity(method.ResponseType),
                    method.Parameters.Select(parameter => new SchemaManifestParameter(
                        parameter.Name!, FriendlyType(parameter.ParameterType),
                        method.Action.Parameters.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerParameterDescriptor>()
                            .FirstOrDefault(x => x.ParameterInfo == parameter)?.BindingInfo?.BindingSource?.Id,
                        GrpcEmbedFieldNumbers.Assign(method.Parameters)[parameter.Name!])).ToArray(),
                    computeHash ? GrpcEmbedContractShape.Hash(method.RequestType) : null,
                    computeHash ? GrpcEmbedContractShape.Hash(method.ResponseType) : null),
                StringComparer.Ordinal)),
            StringComparer.Ordinal);
        return new SchemaManifest(types, services);
    }

    public static string Serialize(SchemaManifest manifest) => JsonSerializer.Serialize(manifest, JsonOptions);

    public static IReadOnlyList<string> ValidateFile(string path, SchemaManifest current)
    {
        var expected = JsonSerializer.Deserialize<SchemaManifest>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidOperationException($"Invalid GrpcEmbed schema manifest '{path}'.");
        var errors = new List<string>();
        foreach (var (typeName, expectedType) in expected.Types)
        {
            if (!current.Types.TryGetValue(typeName, out var currentType)) { errors.Add($"Type removed: {typeName}."); continue; }
            foreach (var (fieldName, expectedField) in expectedType.Fields)
            {
                if (!currentType.Fields.TryGetValue(fieldName, out var currentField))
                {
                    currentField = currentType.Fields.Values.FirstOrDefault(x => x.Number == expectedField.Number);
                    if (currentField is null && !(currentType.Reserved?.Contains(expectedField.Number) ?? false)) errors.Add($"Field removed without reserving its number: {typeName}.{fieldName} (number {expectedField.Number}).");
                    if (currentField is not null && !string.Equals(expectedField.ClrType, currentField.ClrType, StringComparison.Ordinal)) errors.Add($"Renamed field type changed: {typeName}.{fieldName} number {expectedField.Number} {expectedField.ClrType} -> {currentField.ClrType}.");
                    continue;
                }
                if (expectedField.Number != currentField.Number) errors.Add($"Field number changed: {typeName}.{fieldName} {expectedField.Number} -> {currentField.Number}.");
                if (!string.Equals(expectedField.ClrType, currentField.ClrType, StringComparison.Ordinal)) errors.Add($"Field type changed: {typeName}.{fieldName} {expectedField.ClrType} -> {currentField.ClrType}.");
            }
        }
        foreach (var (serviceName, expectedService) in expected.Services ?? new Dictionary<string, SchemaManifestService>())
        {
            if (current.Services is null || !current.Services.TryGetValue(serviceName, out var currentService)) { errors.Add($"Service removed or renamed: {serviceName}."); continue; }
            foreach (var (methodName, expectedMethod) in expectedService.Methods)
            {
                if (!currentService.Methods.TryGetValue(methodName, out var currentMethod)) { errors.Add($"Method removed or renamed: {serviceName}/{methodName}."); continue; }
                if (!string.Equals(expectedMethod.RequestType, currentMethod.RequestType, StringComparison.Ordinal)) errors.Add($"Request type changed: {serviceName}/{methodName} {expectedMethod.RequestType} -> {currentMethod.RequestType}.");
                if (!string.Equals(expectedMethod.ResponseType, currentMethod.ResponseType, StringComparison.Ordinal)) errors.Add($"Response type changed: {serviceName}/{methodName} {expectedMethod.ResponseType} -> {currentMethod.ResponseType}.");
            }
        }
        return errors;
    }

    private static void Add(Type declared, IDictionary<string, SchemaManifestType> types)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type.IsArray && type != typeof(byte[])) { Add(type.GetElementType()!, types); return; }
        var element = type.GetInterfaces().Append(type).FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (element is not null && type != typeof(string)) { Add(element.GetGenericArguments()[0], types); return; }
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(byte[]) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return;
        var name = type.FullName ?? type.Name;
        if (types.ContainsKey(name)) return;
        var properties = type.GetProperties().Where(x => x.CanRead && x.CanWrite).ToArray();
        var numbers = GrpcEmbedFieldNumbers.Assign(properties);
        var reserved = type.GetCustomAttributes(typeof(GrpcReservedFieldAttribute), true).Cast<GrpcReservedFieldAttribute>().SelectMany(x => x.Numbers).Distinct().OrderBy(x => x).ToArray();
        if (reserved.Intersect(numbers.Values).Any()) throw new InvalidOperationException($"Type {name} reserves a protobuf field number that is still in use.");
        types[name] = new SchemaManifestType(properties.ToDictionary(x => x.Name, x => new SchemaManifestField(numbers[x.Name], FriendlyType(x.PropertyType)), StringComparer.Ordinal), reserved);
        foreach (var property in properties) Add(property.PropertyType, types);
    }

    private static string FriendlyType(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    private static string TypeIdentity(Type type) => type.FullName ?? type.Name;
}
