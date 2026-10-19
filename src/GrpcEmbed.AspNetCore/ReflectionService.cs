using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using System.Runtime.CompilerServices;

namespace GrpcEmbed.AspNetCore;

internal sealed class GrpcEmbedReflectionService : ServerReflection.ServerReflectionBase
{
    private static readonly ConditionalWeakTable<GrpcEmbedSchema, ReflectionCatalog> Catalogs = new();
    private readonly RuntimeRegistry _registry;
    private readonly IServiceProvider _services;
    public GrpcEmbedReflectionService(RuntimeRegistry registry, IServiceProvider services) { _registry = registry; _services = services; }

    public override async Task ServerReflectionInfo(IAsyncStreamReader<ServerReflectionRequest> requestStream, IServerStreamWriter<ServerReflectionResponse> responseStream, ServerCallContext context)
    {
        // One immutable catalog per business schema; streams observe one snapshot.
        var catalog = Catalogs.GetValue(_registry.GetSchema(_services), schema => new ReflectionCatalog(schema));
        while (await requestStream.MoveNext(context.CancellationToken))
            await responseStream.WriteAsync(catalog.Resolve(requestStream.Current));
    }
}

// Both protocol versions have identical protobuf field numbers and wire types.
// Reuse the generated alpha messages with the stable service name as an alias.
internal sealed class GrpcEmbedReflectionV1MethodProvider : IServiceMethodProvider<GrpcEmbedReflectionService>
{
    public void OnServiceMethodDiscovery(ServiceMethodProviderContext<GrpcEmbedReflectionService> context)
    {
        var method = new Method<ServerReflectionRequest, ServerReflectionResponse>(MethodType.DuplexStreaming,
            "grpc.reflection.v1.ServerReflection", "ServerReflectionInfo",
            Marshallers.Create(request => request.ToByteArray(), bytes => ServerReflectionRequest.Parser.ParseFrom(bytes)),
            Marshallers.Create(response => response.ToByteArray(), bytes => ServerReflectionResponse.Parser.ParseFrom(bytes)));
        context.AddDuplexStreamingMethod(method, Array.Empty<object>(),
            (service, input, output, call) => service.ServerReflectionInfo(input, output, call));
    }
}

internal sealed class ReflectionCatalog
{
    private readonly Dictionary<string, FileDescriptorProto> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ByteString> _encoded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _symbols = new(StringComparer.Ordinal);
    private readonly HashSet<string> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, int Number), string> _extensions = new();
    private readonly string[] _services;

    public ReflectionCatalog(GrpcEmbedSchema schema)
    {
        foreach (var bytes in schema.FileDescriptors) Add(FileDescriptorProto.Parser.ParseFrom(bytes));
        var alpha = ReflectionReflection.Descriptor.ToProto();
        Add(alpha);
        Add(CreateV1Descriptor(alpha));
        _services = _files.Values.SelectMany(file => file.Service.Select(service => Qualify(file.Package, service.Name)))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        foreach (var file in _files.Values)
        {
            foreach (var message in file.MessageType) IndexMessage(file, file.Package, message);
            foreach (var item in file.EnumType) IndexEnum(file, file.Package, item);
            foreach (var extension in file.Extension) IndexExtension(file, file.Package, extension);
            foreach (var service in file.Service)
            {
                var name = Qualify(file.Package, service.Name);
                _symbols.Add(name, file.Name);
                foreach (var method in service.Method) _symbols.Add(Qualify(name, method.Name), file.Name);
            }
        }
    }

    public ServerReflectionResponse Resolve(ServerReflectionRequest request)
    {
        var response = new ServerReflectionResponse { OriginalRequest = request, ValidHost = request.Host };
        string? fileName = null;
        switch (request.MessageRequestCase)
        {
            case ServerReflectionRequest.MessageRequestOneofCase.ListServices:
                response.ListServicesResponse = new ListServiceResponse();
                response.ListServicesResponse.Service.Add(_services.Select(name => new ServiceResponse { Name = name }));
                return response;
            case ServerReflectionRequest.MessageRequestOneofCase.FileByFilename:
                if (_files.ContainsKey(request.FileByFilename)) fileName = request.FileByFilename;
                break;
            case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol:
                _symbols.TryGetValue(request.FileContainingSymbol, out fileName);
                break;
            case ServerReflectionRequest.MessageRequestOneofCase.FileContainingExtension:
                _extensions.TryGetValue((request.FileContainingExtension.ContainingType.TrimStart('.'), request.FileContainingExtension.ExtensionNumber), out fileName);
                break;
            case ServerReflectionRequest.MessageRequestOneofCase.AllExtensionNumbersOfType:
                var type = request.AllExtensionNumbersOfType.TrimStart('.');
                if (_messages.Contains(type))
                {
                    response.AllExtensionNumbersResponse = new ExtensionNumberResponse { BaseTypeName = type };
                    response.AllExtensionNumbersResponse.ExtensionNumber.Add(_extensions.Keys.Where(key => key.Type == type).Select(key => key.Number).OrderBy(number => number));
                    return response;
                }
                break;
        }
        if (fileName is null)
            response.ErrorResponse = new ErrorResponse { ErrorCode = (int)StatusCode.NotFound, ErrorMessage = "Symbol or file was not found." };
        else
        {
            response.FileDescriptorResponse = new FileDescriptorResponse();
            AddDependencies(fileName, response.FileDescriptorResponse, new HashSet<string>(StringComparer.Ordinal));
        }
        return response;
    }

    private void Add(FileDescriptorProto file)
    {
        _files.Add(file.Name, file);
        _encoded.Add(file.Name, file.ToByteString());
    }

    private void AddDependencies(string name, FileDescriptorResponse response, HashSet<string> seen)
    {
        if (!seen.Add(name)) return;
        foreach (var dependency in _files[name].Dependency) AddDependencies(dependency, response, seen);
        response.FileDescriptorProto.Add(_encoded[name]);
    }

    private void IndexMessage(FileDescriptorProto file, string prefix, DescriptorProto message)
    {
        var name = Qualify(prefix, message.Name);
        _symbols.Add(name, file.Name);
        _messages.Add(name);
        foreach (var field in message.Field) _symbols.Add(Qualify(name, field.Name), file.Name);
        foreach (var oneof in message.OneofDecl) _symbols.Add(Qualify(name, oneof.Name), file.Name);
        foreach (var nested in message.NestedType) IndexMessage(file, name, nested);
        foreach (var item in message.EnumType) IndexEnum(file, name, item);
        foreach (var extension in message.Extension) IndexExtension(file, name, extension);
    }

    private void IndexEnum(FileDescriptorProto file, string prefix, EnumDescriptorProto item)
    {
        _symbols.Add(Qualify(prefix, item.Name), file.Name);
        // Protobuf enum values are siblings of the enum, not children.
        foreach (var value in item.Value) _symbols.Add(Qualify(prefix, value.Name), file.Name);
    }

    private void IndexExtension(FileDescriptorProto file, string prefix, FieldDescriptorProto extension)
    {
        _symbols.Add(Qualify(prefix, extension.Name), file.Name);
        _extensions.Add((extension.Extendee.TrimStart('.'), extension.Number), file.Name);
    }

    private static string Qualify(string prefix, string name) => string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;

    internal static FileDescriptorProto CreateV1Descriptor(FileDescriptorProto alpha)
    {
        var stable = alpha.Clone();
        stable.Name = "grpc/reflection/v1/reflection.proto";
        stable.Package = "grpc.reflection.v1";
        stable.Options = null;
        foreach (var message in stable.MessageType) RewriteMessage(message);
        foreach (var service in stable.Service)
        {
            service.Options = null;
            foreach (var method in service.Method)
            {
                method.InputType = Rewrite(method.InputType);
                method.OutputType = Rewrite(method.OutputType);
            }
        }
        return stable;
    }

    private static void RewriteMessage(DescriptorProto message)
    {
        foreach (var field in message.Field)
            if (field.TypeName.Length != 0) field.TypeName = Rewrite(field.TypeName);
        foreach (var nested in message.NestedType) RewriteMessage(nested);
    }

    private static string Rewrite(string name) => name.Replace(".grpc.reflection.v1alpha.", ".grpc.reflection.v1.", StringComparison.Ordinal);
}
