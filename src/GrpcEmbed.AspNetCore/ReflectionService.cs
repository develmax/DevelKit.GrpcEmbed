using Google.Protobuf;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;

namespace GrpcEmbed.AspNetCore;

internal sealed class GrpcEmbedReflectionService : ServerReflection.ServerReflectionBase
{
    private readonly RuntimeRegistry _registry;
    private readonly IServiceProvider _services;
    public GrpcEmbedReflectionService(RuntimeRegistry registry, IServiceProvider services) { _registry = registry; _services = services; }

    public override async Task ServerReflectionInfo(IAsyncStreamReader<ServerReflectionRequest> requestStream, IServerStreamWriter<ServerReflectionResponse> responseStream, ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var request = requestStream.Current;
            var schema = _registry.GetSchema(_services);
            var response = new ServerReflectionResponse { OriginalRequest = request, ValidHost = request.Host };
            switch (request.MessageRequestCase)
            {
                case ServerReflectionRequest.MessageRequestOneofCase.ListServices:
                    response.ListServicesResponse = new ListServiceResponse();
                    response.ListServicesResponse.Service.Add(schema.Services.Append("grpc.reflection.v1alpha.ServerReflection").Select(x => new ServiceResponse { Name = x }));
                    break;
                case ServerReflectionRequest.MessageRequestOneofCase.FileByFilename when request.FileByFilename == "grpcembed.proto":
                case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol when schema.Services.Contains(request.FileContainingSymbol) || request.FileContainingSymbol.StartsWith("GrpcEmbed.", StringComparison.Ordinal):
                    response.FileDescriptorResponse = new FileDescriptorResponse();
                    response.FileDescriptorResponse.FileDescriptorProto.Add(schema.FileDescriptors.Select(ByteString.CopyFrom));
                    break;
                default:
                    response.ErrorResponse = new ErrorResponse { ErrorCode = (int)StatusCode.NotFound, ErrorMessage = "Symbol or file was not found." };
                    break;
            }
            await responseStream.WriteAsync(response);
        }
    }
}
