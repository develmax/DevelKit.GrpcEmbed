using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Controllers;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using TimestampMessage = Google.Protobuf.WellKnownTypes.Timestamp;
using DurationMessage = Google.Protobuf.WellKnownTypes.Duration;

namespace GrpcEmbed.AspNetCore;

internal sealed record GrpcEmbedSchema(string Proto, string Sha256, byte[] DescriptorSet, IReadOnlyList<byte[]> FileDescriptors, IReadOnlyList<string> Services);

internal static class SchemaGenerator
{
    public static GrpcEmbedSchema Generate(IReadOnlyList<RuntimeMethod> methods, bool computeHash = true, GrpcEmbedRoutingOptions? routing = null)
    {
        var messages = new Dictionary<Type, string>();
        foreach (var method in methods)
        {
            AddMessage(method.RequestType, messages);
            AddMessage(method.ResponseType, messages);
        }
        var usesTimestamp = methods.Any(x => ContainsType(x.RequestType, typeof(DateTime)) || ContainsType(x.ResponseType, typeof(DateTime)));
        var usesDuration = methods.Any(x => ContainsType(x.RequestType, typeof(TimeSpan)) || ContainsType(x.ResponseType, typeof(TimeSpan)));
        var text = new StringBuilder("syntax = \"proto3\";\npackage GrpcEmbed;\n");
        if (usesTimestamp) text.Append("import \"google/protobuf/timestamp.proto\";\n");
        if (usesDuration) text.Append("import \"google/protobuf/duration.proto\";\n");
        text.Append('\n');
        foreach (var message in messages.OrderBy(x => TypeName(x.Key), StringComparer.Ordinal)) text.Append(message.Value);
        foreach (var service in methods.GroupBy(x => x.ServiceName).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            text.Append("service ").Append(service.Key).Append(" {\n");
            foreach (var method in service.OrderBy(x => x.MethodName, StringComparer.Ordinal))
                text.Append("  rpc ").Append(method.MethodName).Append(" (").Append(TypeName(method.RequestType)).Append(") returns (").Append(TypeName(method.ResponseType)).Append(");\n");
            text.Append("}\n\n");
        }
        var proto = text.ToString();
        var descriptor = BuildDescriptor(methods);
        var set = new FileDescriptorSet();
        if (usesTimestamp) { descriptor.Dependency.Add("google/protobuf/timestamp.proto"); set.File.Add(TimestampMessage.Descriptor.File.ToProto()); }
        if (usesDuration) { descriptor.Dependency.Add("google/protobuf/duration.proto"); set.File.Add(DurationMessage.Descriptor.File.ToProto()); }
        set.File.Add(descriptor);
        var routes = System.Text.Json.JsonSerializer.Serialize(methods
            .OrderBy(method => method.ServiceName, StringComparer.Ordinal)
            .ThenBy(method => method.MethodName, StringComparer.Ordinal)
            .Select(SchemaManifestManager.GetRoute));
        var hash = computeHash ? Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("grpcembed-contract-v2\n" + proto + "\n" + routes + "\n" +
                (routing?.Mode ?? GrpcEmbedRoutingMode.Native) + "\n" + (routing?.Prefix ?? "grpc")))).ToLowerInvariant() : string.Empty;
        return new GrpcEmbedSchema(proto, hash, set.ToByteArray(), set.File.Select(x => x.ToByteArray()).ToArray(), methods.Select(x => "GrpcEmbed." + x.ServiceName).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static void AddMessage(Type type, IDictionary<Type, string> messages)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsScalar(type) || messages.ContainsKey(type)) return;
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().Select(value => (Name: Enum.GetName(type, value)!, Number: Convert.ToInt32(value, CultureInfo.InvariantCulture))).ToList();
            var enumText = new StringBuilder("enum ").Append(TypeName(type)).Append(" {\n");
            if (values.All(x => x.Number != 0)) enumText.Append("  ").Append(TypeName(type).ToUpperInvariant()).Append("_UNSPECIFIED = 0;\n");
            foreach (var value in values.OrderBy(x => x.Number).ThenBy(x => x.Name, StringComparer.Ordinal))
                enumText.Append("  ").Append(ToEnumName(type, value.Name)).Append(" = ").Append(value.Number.ToString(CultureInfo.InvariantCulture)).Append(";\n");
            enumText.Append("}\n\n");
            messages[type] = enumText.ToString();
            return;
        }
        var members = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(x => x.CanRead && x.CanWrite).ToArray();
        var numbers = GrpcEmbedFieldNumbers.Assign(members);
        messages[type] = string.Empty;
        var text = new StringBuilder("message ").Append(TypeName(type)).Append(" {\n");
        var reserved = ReservedNumbers(type);
        if (reserved.Count > 0) text.Append("  reserved ").Append(string.Join(", ", reserved)).Append(";\n");
        foreach (var member in members.OrderBy(x => numbers[x.Name]))
        {
            var memberType = Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType;
            Type? element = null;
            var repeated = memberType != typeof(string) && memberType != typeof(byte[]) && TryElement(memberType, out element);
            var actual = repeated ? element! : memberType;
            AddMessage(actual, messages);
            text.Append("  ").Append(repeated ? "repeated " : string.Empty).Append(ProtoType(actual)).Append(' ').Append(ToSnake(member.Name)).Append(" = ").Append(numbers[member.Name].ToString(CultureInfo.InvariantCulture)).Append(";\n");
        }
        text.Append("}\n\n"); messages[type] = text.ToString();
    }

    private static bool TryElement(Type type, out Type? element)
    {
        if (type.IsArray) { element = type.GetElementType(); return true; }
        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        element = enumerable?.GetGenericArguments()[0]; return element is not null;
    }
    private static bool IsScalar(Type t) => t.IsPrimitive || t == typeof(string) || t == typeof(byte[]) || t == typeof(decimal) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan);
    private static string ProtoType(Type type) => type.IsEnum ? TypeName(type) : type == typeof(bool) ? "bool" : type == typeof(byte[]) ? "bytes" : type == typeof(DateTime) ? "google.protobuf.Timestamp" : type == typeof(TimeSpan) ? "google.protobuf.Duration" : type == typeof(string) || type == typeof(Guid) || type == typeof(decimal) || type == typeof(DateTimeOffset) ? "string" : type == typeof(float) ? "float" : type == typeof(double) ? "double" : type == typeof(long) ? "int64" : type == typeof(ulong) ? "uint64" : type == typeof(uint) || type == typeof(ushort) || type == typeof(byte) ? "uint32" : type.IsPrimitive ? "int32" : TypeName(type);
    private static string TypeName(Type type) => new(type.Name.TakeWhile(c => c != '`').Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private static string ToSnake(string name) => string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    private static bool ContainsType(Type declared, Type sought, ISet<Type>? visited = null)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type == sought) return true;
        visited ??= new HashSet<Type>(); if (!visited.Add(type) || IsScalar(type) || type.IsEnum) return false;
        if (TryElement(type, out var element)) return ContainsType(element!, sought, visited);
        return type.GetProperties().Any(x => ContainsType(x.PropertyType, sought, visited));
    }

    private static FileDescriptorProto BuildDescriptor(IReadOnlyList<RuntimeMethod> methods)
    {
        var file = new FileDescriptorProto { Name = "grpcembed.proto", Package = "GrpcEmbed", Syntax = "proto3" };
        var seen = new HashSet<Type>();
        foreach (var method in methods) { AddDescriptorMessage(method.RequestType, file, seen); AddDescriptorMessage(method.ResponseType, file, seen); }
        foreach (var group in methods.GroupBy(x => x.ServiceName).OrderBy(x => x.Key))
        {
            var service = new ServiceDescriptorProto { Name = group.Key };
            foreach (var method in group.OrderBy(x => x.MethodName)) service.Method.Add(new MethodDescriptorProto { Name = method.MethodName, InputType = ".GrpcEmbed." + TypeName(method.RequestType), OutputType = ".GrpcEmbed." + TypeName(method.ResponseType) });
            file.Service.Add(service);
        }
        return file;
    }

    private static void AddDescriptorMessage(Type type, FileDescriptorProto file, ISet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsScalar(type) || !seen.Add(type)) return;
        if (type.IsEnum)
        {
            var descriptor = new EnumDescriptorProto { Name = TypeName(type) };
            var values = Enum.GetValues(type).Cast<object>().Select(value => (Name: Enum.GetName(type, value)!, Number: Convert.ToInt32(value, CultureInfo.InvariantCulture))).ToList();
            if (values.All(x => x.Number != 0)) descriptor.Value.Add(new EnumValueDescriptorProto { Name = TypeName(type).ToUpperInvariant() + "_UNSPECIFIED", Number = 0 });
            foreach (var value in values.OrderBy(x => x.Number).ThenBy(x => x.Name, StringComparer.Ordinal))
                descriptor.Value.Add(new EnumValueDescriptorProto { Name = ToEnumName(type, value.Name), Number = value.Number });
            file.EnumType.Add(descriptor);
            return;
        }
        var message = new DescriptorProto { Name = TypeName(type) };
        foreach (var number in ReservedNumbers(type)) message.ReservedRange.Add(new DescriptorProto.Types.ReservedRange { Start = number, End = number + 1 });
        var members = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(x => x.CanRead && x.CanWrite).ToArray();
        var numbers = GrpcEmbedFieldNumbers.Assign(members);
        foreach (var member in members)
        {
            var memberType = Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType; Type? element = null;
            var repeated = memberType != typeof(string) && memberType != typeof(byte[]) && TryElement(memberType, out element);
            var actual = repeated ? element! : memberType; AddDescriptorMessage(actual, file, seen);
            var field = new FieldDescriptorProto { Name = ToSnake(member.Name), Number = numbers[member.Name], Label = repeated ? FieldDescriptorProto.Types.Label.Repeated : FieldDescriptorProto.Types.Label.Optional };
            (field.Type, field.TypeName) = DescriptorType(actual);
            message.Field.Add(field);
        }
        file.MessageType.Add(message);
    }

    private static (FieldDescriptorProto.Types.Type, string) DescriptorType(Type type)
    {
        if (!IsScalar(type) && !type.IsEnum) return (FieldDescriptorProto.Types.Type.Message, ".GrpcEmbed." + TypeName(type));
        if (type.IsEnum) return (FieldDescriptorProto.Types.Type.Enum, ".GrpcEmbed." + TypeName(type));
        return type == typeof(bool) ? (FieldDescriptorProto.Types.Type.Bool, "") : type == typeof(DateTime) ? (FieldDescriptorProto.Types.Type.Message, ".google.protobuf.Timestamp") : type == typeof(TimeSpan) ? (FieldDescriptorProto.Types.Type.Message, ".google.protobuf.Duration") : type == typeof(string) || type == typeof(Guid) || type == typeof(decimal) || type == typeof(DateTimeOffset) ? (FieldDescriptorProto.Types.Type.String, "") : type == typeof(byte[]) ? (FieldDescriptorProto.Types.Type.Bytes, "") : type == typeof(float) ? (FieldDescriptorProto.Types.Type.Float, "") : type == typeof(double) ? (FieldDescriptorProto.Types.Type.Double, "") : type == typeof(long) ? (FieldDescriptorProto.Types.Type.Int64, "") : type == typeof(ulong) ? (FieldDescriptorProto.Types.Type.Uint64, "") : type == typeof(uint) || type == typeof(ushort) || type == typeof(byte) ? (FieldDescriptorProto.Types.Type.Uint32, "") : (FieldDescriptorProto.Types.Type.Int32, "");
    }

    private static IReadOnlyList<int> ReservedNumbers(Type type) => type
        .GetCustomAttributes(typeof(GrpcReservedFieldAttribute), true)
        .Cast<GrpcReservedFieldAttribute>()
        .SelectMany(x => x.Numbers)
        .Distinct()
        .OrderBy(x => x)
        .ToArray();

    private static string ToEnumName(Type type, string value) =>
        TypeName(type).ToUpperInvariant() + "_" + string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_'));
}
