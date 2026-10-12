using System.Threading;

namespace GrpcEmbed;

/// <summary>Opt-in per-call decoded payloads for integration with an application's own audit and masking policy.</summary>
/// <remarks>Payloads are not logged by GrpcEmbed. Consumers must apply their normal masking before persistence.</remarks>
public sealed class GrpcEmbedClientPayloadAudit : IDisposable
{
    private static readonly AsyncLocal<GrpcEmbedClientPayloadAudit?> Slot = new();
    private readonly GrpcEmbedClientPayloadAudit? _previous;
    private bool _completed;
    private bool _disposed;
    /// <summary>The current call, isolated across asynchronous and concurrent calls.</summary>
    public static GrpcEmbedClientPayloadAudit? Current => Slot.Value;
    /// <summary>Decoded logical request body; null for a REST-compatible operation with no body.</summary>
    public object? RequestBody { get; }
    /// <summary>Resolved gRPC service/method identity.</summary>
    public string Operation { get; }
    /// <summary>Original controller HTTP method, when published in the server contract.</summary>
    public string? HttpMethod { get; }
    /// <summary>Called after the response has been decoded and the final gRPC status verified.</summary>
    public Action<object?, int>? Completed { get; set; }
    /// <summary>Called on a failed attempt, including cancellation and contract rejection.</summary>
    public Action<Exception>? Failed { get; set; }
    /// <summary>Releases application-owned audit resources at the end of this attempt.</summary>
    public Action? Released { get; set; }
    /// <summary>Creates a payload carrier for one RPC attempt. Normally called by the runtime client.</summary>
    public GrpcEmbedClientPayloadAudit(string operation, object? requestBody, string? httpMethod)
    {
        Operation = operation; RequestBody = requestBody; HttpMethod = httpMethod;
        _previous = Slot.Value; Slot.Value = this;
    }
    /// <summary>Completes the application audit once, using the actual decoded response DTO.</summary>
    public void Complete(object? response, int status)
    {
        if (_completed || _disposed) return;
        _completed = true; Completed?.Invoke(response, status);
    }
    /// <summary>Completes a failed attempt without substituting or replaying the RPC.</summary>
    public void Fail(Exception error)
    {
        if (_completed || _disposed) return;
        _completed = true; Failed?.Invoke(error);
    }
    /// <summary>Clears the per-attempt state and releases audit resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Released?.Invoke(); }
        finally { Slot.Value = _previous; }
    }
}

/// <summary>Endpoint metadata for auditing decoded unary DTOs without reading or replacing binary streams.</summary>
public sealed class GrpcEmbedPayloadAuditContract
{
    /// <summary>HttpContext.Items key for the actual unwrapped response DTO. Only populated when requested by audit middleware.</summary>
    public const string ResponseKey = "GrpcEmbed.PayloadAudit.Response";
    /// <summary>HttpContext.Items key requesting capture of the actual logical response.</summary>
    public const string CaptureResponseKey = "GrpcEmbed.PayloadAudit.CaptureResponse";
    /// <summary>HttpContext.Items key for an Action&lt;object?, string?&gt; called with the decoded logical request body and original HTTP method.</summary>
    public const string RequestObserverKey = "GrpcEmbed.PayloadAudit.RequestObserver";
    private readonly Func<object, object?> _project;
    /// <summary>The original controller HTTP method.</summary>
    public string? HttpMethod { get; }
    /// <summary>Creates metadata using the already-decoded runtime request and logical body projection.</summary>
    public GrpcEmbedPayloadAuditContract(Func<object, object?> project, string? httpMethod)
    { _project = project; HttpMethod = httpMethod; }
    /// <summary>Projects the actual decoded transport request to the original controller body.</summary>
    public object? ProjectRequestBody(object request) => _project(request);
}
