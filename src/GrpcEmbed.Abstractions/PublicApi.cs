using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GrpcEmbed;

public enum GrpcEmbedExportMode { All, ExplicitOnly }
/// <summary>Wire response for an operation that completes without a response body.</summary>
public sealed class GrpcEmbedEmpty { }
public enum GrpcEmbedMethodType { Unary, ServerStreaming, ClientStreaming, DuplexStreaming }
public enum GrpcEmbedTransportMode { GrpcOnly }
public enum GrpcEmbedStatusCode { Cancelled, InvalidArgument, DeadlineExceeded, NotFound, AlreadyExists, PermissionDenied, ResourceExhausted, FailedPrecondition, Unauthenticated, Unavailable, Internal }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class GrpcExportAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class GrpcIgnoreAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method, Inherited = true)]
public sealed class GrpcNameAttribute : Attribute
{
    public GrpcNameAttribute(string name) => Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A name is required.", nameof(name)) : name;
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true)]
public sealed class GrpcFieldNumberAttribute : Attribute
{
    public GrpcFieldNumberAttribute(int number)
    {
        if (number is < 1 or > 536_870_911 || number is >= 19_000 and <= 19_999) throw new ArgumentOutOfRangeException(nameof(number), "The value must be a valid non-reserved protobuf field number.");
        Number = number;
    }
    public int Number { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class GrpcReservedFieldAttribute : Attribute
{
    public GrpcReservedFieldAttribute(params int[] numbers)
    {
        if (numbers is null || numbers.Length == 0) throw new ArgumentException("At least one field number is required.", nameof(numbers));
        foreach (var number in numbers) _ = new GrpcFieldNumberAttribute(number);
        Numbers = Array.AsReadOnly(numbers.Distinct().OrderBy(x => x).ToArray());
    }
    public IReadOnlyList<int> Numbers { get; }
}

public sealed record GrpcEmbedParameterContext(
    string Name, Type ParameterType, string? BindingSource, bool IsTransportParameter,
    int? FieldNumber);

public sealed record GrpcEmbedActionContext(
    Type ControllerType, string ControllerName, string ActionName, MethodInfo Method,
    string? Route, IReadOnlyList<string> HttpMethods, Type ReturnType,
    IReadOnlyList<Attribute> Attributes,
    IReadOnlyList<GrpcEmbedParameterContext>? Parameters = null);

public sealed class GrpcEmbedOptions
{
    public GrpcEmbedServerContractOptions Contract { get; set; } = new();
    public bool ServerEnabled { get; set; } = true;
    public GrpcEmbedExportMode ExportMode { get; set; } = GrpcEmbedExportMode.All;
    public bool ThrowOnUnsupportedAction { get; set; }
    public bool EnableSchemaEndpoint { get; set; }
    public bool EnableReflection { get; set; }
    public string? SchemaAuthorizationPolicy { get; set; }
    public bool EnableDetailedErrors { get; set; }
    public Func<int, GrpcEmbedStatusCode>? StatusMapper { get; set; }
    public string? SchemaManifestPath { get; set; }
    public Func<GrpcEmbedActionContext, bool>? ShouldExport { get; set; }
}

public enum GrpcEmbedContractValidation { Disabled, IfPresent, Required }
public enum GrpcEmbedContractGeneration { Startup, FirstRequest }

public sealed class GrpcEmbedContractHashOptions
{
    public bool Enabled { get; set; } = true;
    public bool SendInResponses { get; set; } = true;
}

public sealed class GrpcEmbedServerContractOptions
{
    public bool Enabled { get; set; } = true;
    public bool ExposeEndpoint { get; set; }
    public GrpcEmbedContractGeneration Generate { get; set; } = GrpcEmbedContractGeneration.FirstRequest;
    public GrpcEmbedContractValidation Validation { get; set; } = GrpcEmbedContractValidation.Disabled;
    public GrpcEmbedContractHashOptions Hash { get; set; } = new();
}

public static class GrpcEmbedContractHeaders
{
    public const string RequestHash = "grpcembed-contract-hash";
    public const string ServerHash = "grpcembed-schema-hash";
    public const string Rejection = "grpcembed-contract-rejection";
    public const string NotExecuted = "not-executed";
}

/// <summary>Deterministic protobuf field numbering shared by server, schema tooling and clients.</summary>
public static class GrpcEmbedFieldNumbers
{
    public static IReadOnlyDictionary<string, int> Assign(IEnumerable<string> memberNames)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<int>();
        foreach (var name in memberNames.OrderBy(x => x, StringComparer.Ordinal))
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name));
            var number = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) % 536_870_911) + 1;
            if (number is >= 19_000 and <= 19_999) number += 1_000;
            while (!used.Add(number)) number = number == 536_870_911 ? 1 : number + 1;
            result.Add(name, number);
        }
        return result;
    }

    public static IReadOnlyDictionary<string, int> Assign(IEnumerable<PropertyInfo> properties) =>
        Assign(properties.Select(x => (x.Name, x.GetCustomAttribute<GrpcFieldNumberAttribute>(true)?.Number)));

    public static IReadOnlyDictionary<string, int> Assign(IEnumerable<ParameterInfo> parameters) =>
        Assign(parameters.Select(x => (x.Name ?? throw new InvalidOperationException("A protobuf parameter must have a name."), x.GetCustomAttribute<GrpcFieldNumberAttribute>(true)?.Number)));

    private static IReadOnlyDictionary<string, int> Assign(IEnumerable<(string Name, int? Explicit)> members)
    {
        var materialized = members.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<int>();
        foreach (var member in materialized.Where(x => x.Explicit.HasValue))
        {
            if (!used.Add(member.Explicit!.Value)) throw new InvalidOperationException($"Duplicate protobuf field number {member.Explicit.Value}.");
            result.Add(member.Name, member.Explicit.Value);
        }
        foreach (var member in materialized.Where(x => !x.Explicit.HasValue))
        {
            var number = Assign(new[] { member.Name })[member.Name];
            while (!used.Add(number)) number = number == 536_870_911 ? 1 : number + 1;
            result.Add(member.Name, number);
        }
        return result;
    }
}
