# Client hot-path performance

## Comparable payload auditing

GrpcEmbed 3.1.0 supports opt-in integration with an application's existing audit and masking pipeline:

- Set `GrpcEmbed:Clients:CaptureAuditPayloads: true` (or a client's `Grpc:CaptureAuditPayloads`) for configured runtime clients; direct registrations use `GrpcEmbedClientOptions.CaptureAuditPayloads`.
- An HTTP audit adapter can read `GrpcEmbedClientPayloadAudit.Current`, serialize its logical request body using the application's JSON contract, and register completion/failure/release callbacks. Completion receives the actual decoded response DTO after final gRPC status verification, not a second Protobuf decode.
- Server audit middleware can install an `Action<object?, string?>` under `GrpcEmbedPayloadAuditContract.RequestObserverKey` and set `CaptureResponseKey` in `HttpContext.Items`. The request callback runs after contract validation and decoding; `ResponseKey` holds the unwrapped DTO after MVC filters. Rejected calls may have no decoded body. Remove per-request hooks when releasing resources.
- Keep the same masking rules, body limits and sinks as REST. Never persist raw DTOs, binary frames, tokens or callback exceptions without applying the application's privacy policy. Capture itself performs no logging.

Do not compare a fully redacted gRPC marker against REST's field-level JSON audit and describe the result as transport-only performance. An application comparison must execute equivalent serialization, masking and logging on both sides. JSON projection for gRPC auditing is part of its measured cost; database/cache work, HTTP version, warmup, concurrency and resources must also remain comparable.

The carrier and captured server DTOs are transient memory, not a persistent audit store. Callbacks run inline, not in the background. An audit failure must not replay an already-executed business call. For canonical body projection, fetch the server contract: native calls without a fetched contract expose the transport request envelope instead.

## Transport hot path

GrpcEmbed clients reuse their DI registrations, proxies, call invokers and gRPC
channels. They do not construct a service provider or channel for every RPC.

For routed calls, route-template parsing, API-prefix inference, parameter-name
matching and request-constructor compilation are performed when binding the
method, not on each invocation. Each invocation still reads its own arguments
and URI-escapes route values; no request-specific parameters are shared.

A bound route captures startup routing configuration. A newly fetched contract
snapshot replaces the binding, so contract refresh cannot keep using the old
server route. Concurrent calls hold independent immutable binding references.
The unreleased client also binds the resolved RPC method and operation identity
to that snapshot. Routing metadata and payload-audit callbacks reuse the same
identity instead of rebuilding it for each request; no DTO or argument values
are retained in the binding.
Configure routing options before resolving/initializing the client, rather than
mutating the options object while it is serving requests.

Loaded contracts use a synchronous `ValueTask` fast path and share immutable
prepared-operation records. Local request/response shape checks and refresh
interval checks still apply. Contract hash guards and pre-execution rejection
remain enabled according to configuration; performance does not require turning
them off. Metadata is copied once without modifying a caller-owned collection.

Since 3.0.2, only actual contract fetches, refreshes or their semaphore waits create
a linked cancellation source. A warmed RPC does not allocate a contract timeout
timer; the gRPC deadline still bounds the RPC. The original absolute deadline is
also passed to contract loading and guarded refresh, so a retry cannot reset the
end-to-end timeout budget.
Caller cancellation and gRPC deadlines remain propagated. Request constructors
are compiled during proxy initialization rather than lazily switching a shared
static factory between client instances.

On the server, action arguments use the parameter metadata cached during startup.
Resource-filter contexts are created only when resource filters participate.
Authorization and MVC action/result-filter behavior are unchanged; these changes
do not bypass access controls, validation or audit logging.

These changes reduce routing overhead, not database or network latency. Measure
the same operations and payloads, warm up contract fetching separately, compare
against REST over HTTP/2 as well as HTTP/1.1, and record CPU throttling and DTO
equivalence. Shared-host tail latency and process-wide allocation totals must not
be presented as isolated serializer costs or a universal gRPC speedup.
