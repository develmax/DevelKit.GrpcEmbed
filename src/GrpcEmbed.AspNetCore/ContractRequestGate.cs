using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GrpcEmbed.AspNetCore;

internal static class ContractRequestGate
{
    // Wraps the routed endpoint delegate, before the gRPC handler reads/deserializes the body.
    public static async Task Invoke(HttpContext context, RequestDelegate next, GrpcEmbedOptions options)
    {
        var supplied = context.Request.Headers[GrpcEmbedContractHeaders.RequestHash];
        if (supplied.Count == 0 && options.Contract.Validation == GrpcEmbedContractValidation.IfPresent)
        {
            await next(context);
            return;
        }
        var hash = context.RequestServices.GetRequiredService<RuntimeRegistry>().GetSchema(context.RequestServices).Sha256;
        if (supplied.Count == 1 && string.Equals(supplied[0], hash, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }
        // gRPC trailers-only response. No body access, action invocation, or automatic fallback.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/grpc";
        context.Response.Headers["grpc-status"] = "9";
        context.Response.Headers["grpc-message"] = "Contract%20mismatch%3B%20operation%20not%20executed";
        context.Response.Headers[GrpcEmbedContractHeaders.Rejection] = GrpcEmbedContractHeaders.NotExecuted;
        context.Response.Headers[GrpcEmbedContractHeaders.ServerHash] = hash;
        context.Response.ContentLength = 0;
    }
}
