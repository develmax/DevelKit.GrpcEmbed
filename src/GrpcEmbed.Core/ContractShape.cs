using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GrpcEmbed;

/// <summary>Version-independent, conservative fingerprint of a CLR protobuf shape.</summary>
public static class GrpcEmbedContractShape
{
    public static string Hash(Type root)
    {
        var nodes = new Dictionary<Type, int>();
        var text = new StringBuilder("grpcembed-shape-v1;");
        void Visit(Type type)
        {
            if (Nullable.GetUnderlyingType(type) is { } nullable)
            {
                text.Append("nullable("); Visit(nullable); text.Append(')'); return;
            }
            if (type.IsEnum)
            {
                text.Append("enum{");
                foreach (var name in Enum.GetNames(type).OrderBy(name => name, StringComparer.Ordinal))
                    text.Append(name).Append('=').Append(Convert.ToString(Convert.ChangeType(
                        Enum.Parse(type, name), Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).Append(';');
                text.Append('}'); return;
            }
            if (type.IsPrimitive || type == typeof(string) || type == typeof(byte[]) ||
                type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) || type == typeof(TimeSpan))
            { text.Append(type.FullName); return; }
            var collection = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType &&
                item.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (collection is not null)
            { text.Append("repeated("); Visit(collection.GetGenericArguments()[0]); text.Append(')'); return; }
            if (nodes.TryGetValue(type, out var existing))
            { text.Append('@').Append(existing.ToString(CultureInfo.InvariantCulture)); return; }
            var id = nodes.Count; nodes.Add(type, id);
            text.Append('m').Append(id.ToString(CultureInfo.InvariantCulture)).Append('{');
            var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0).ToArray();
            var numbers = GrpcEmbedFieldNumbers.Assign(fields);
            foreach (var field in fields.OrderBy(property => numbers[property.Name]))
            {
                text.Append(numbers[field.Name].ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(field.Name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field.Name).Append('=');
                Visit(field.PropertyType); text.Append(';');
            }
            text.Append('}');
        }
        Visit(root);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
