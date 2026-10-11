# Runtime client integration

Install `DevelKit.GrpcEmbed.Client`, or the `DevelKit.GrpcEmbed` bundle if this
application also hosts gRPC controllers. Shared dependencies are included
automatically; see [packages and installation](../README.md#packages-and-installation).
Application registrations and deployment configuration must be migrated explicitly.

## Register once

After registering the application's existing named HTTP clients, call:

    services.AddConfiguredGrpcEmbedClients<IClientMarker>(
        configuration, client => client.ApiName);

The marker is an application interface, not a list of concrete client types.
GrpcEmbed has no dependency on the library implementing those registrations.
Configuration uses the existing client name and the existing named IHttpClientFactory.

    GrpcEmbed:
      Clients:
        Enabled: false
    ExistingClient:
      Grpc:
        Enabled: true
        Address: https://example.invalid
        ServiceName: Users

The optional local Enabled overrides the global flag, including explicit false.
Omitting both selects REST. There is no automatic REST fallback.

The current decorator supports singleton factory/type registrations and rejects
duplicate interface registrations or externally owned instance registrations.
It preserves local interface property getters through the original object.
It does not invoke REST action methods when gRPC is selected.

## Startup and naming

Hosted startup resolves proxies, compiles method delegates and creates reusable
channels. A standalone service provider can call InitializeGrpcEmbedClients explicitly.
Channels are disposed with the application container.

With Contract.Fetch set to Never and ServiceName configured, construction is local
and requires no network calls.
The normal method-name convention removes Async; GrpcName attributes override it.
Overload pairs with and without a trailing CancellationToken share an operation.
Task, ValueTask, Task<T>, ValueTask<T> and inherited contract members are supported.

Contract.Fetch selects Never (default), Startup or FirstCall. When fetching is
enabled, ServiceName can be omitted if the operation is unambiguous. Discovery
matches operation names and exact request/response model fingerprints.
Grpc.Contract.Address overrides the default /_grpcembed/schema.json URL at the
gRPC origin. Configure it explicitly when an ingress exposes metadata elsewhere.

Discovery requires a reachable receiver and credentials available without an
incoming request. It must not be used blindly with user-token propagation.
A failed discovery fails initialization; it never silently selects REST.
See [contract safety](contract-safety.md) for fetching, validation and retry policies.

Grpc.Address is required if the REST BaseAddress contains a path; integration
does not guess which path segments an ingress removes. Non-loopback endpoints
must use HTTPS unless explicitly opted into unencrypted HTTP/2 as described below.
Existing HTTP authentication and request handlers remain active.
Existing handler retry and certificate policies are not silently overwritten:
the application must configure them appropriately, especially for writes.

## Explicit unencrypted HTTP/2 (h2c) opt-in

Since 2.1.1, configured clients can opt into non-loopback HTTP endpoints. By default,
`GrpcEmbed:Clients:AllowUnencryptedHttp2` is `false`. The existing client's
`Grpc:AllowUnencryptedHttp2` overrides the global value, including explicit `false`.
The effective setting validates both the RPC address and the contract discovery
address before network access. Loopback HTTP remains available without an opt-in.

Prefer enabling only a specific trusted in-cluster client:

```yaml
GrpcEmbed:
  Clients:
    Enabled: false
    AllowUnencryptedHttp2: false
ExistingClient:
  Grpc:
    Enabled: true
    AllowUnencryptedHttp2: true
    Address: http://receiving-service:8085/api/v1
    Contract:
      Fetch: FirstCall
      Address: http://receiving-service:8085/_grpcembed/schema.json
      SendHash: true
```

Alternatively, enable the global flag and set an individual client's flag to
`false` to require HTTPS for that client. This setting neither enables gRPC itself
nor changes server listeners. The receiving listener and any intermediary must
support HTTP/2 without TLS; gRPC requests do not fall back to HTTP/1.1 or REST.
With the opt-in enabled, HTTP contract discovery also uses HTTP/2 exactly, so it
can reach an HTTP/2-only listener even when the named client's default is HTTP/1.1.
HTTPS discovery retains the named client's configured HTTP version policy. The
named client's defaults and unrelated REST requests are not changed.

**Security:** h2c does not encrypt traffic. Bearer tokens, headers, contract metadata
and payloads are visible to parties able to inspect the network path. Use this
only on a trusted network with appropriate access controls, or with independently
verified transport encryption. For external or untrusted networks, keep HTTPS.
The opt-in does not disable TLS certificate validation or allow non-HTTP schemes.

## Server

Use AddGrpcEmbed(configuration) and map REST controllers as before.
Only GrpcEmbed:Server:Enabled controls configuration-based gRPC publication.
GrpcEmbed:Server:ExposeAll selects all supported actions or only GrpcExport-marked
controllers/actions. GrpcIgnore excludes actions in either mode.
The old GrpcEmbed:Enabled key is not read by this API.

Remote discovery also requires enabling the existing schema endpoint explicitly
and applying an appropriate authorization policy. Disabling gRPC removes its
schema endpoints but leaves REST available.

Completion-only MVC actions return an empty protobuf response. An untyped
IActionResult returning a body is rejected instead of silently dropping data;
declare its response type explicitly.

## Verification and remaining work

### Empty wrapped responses (2.1.2)

Protobuf omits empty repeated fields and empty string/byte defaults. A successful
wrapped response is therefore restored as an empty CLR collection, array,
string or byte array instead of a null wrapper value. Standard collection
interfaces use assignable List/HashSet/Dictionary implementations; supported
concrete collections use their empty constructor. This does not recursively
replace nullable properties inside DTOs. Genuine null MVC action results are
still rejected by the server with NotFound and are not converted to success.

The correction is client-only and retains the existing wire contract and schema
hash. Updating the client is sufficient for an existing 2.1.1 server. It does not
weaken trailer validation, contract checking, authorization, or retry rules.

Library integration tests cover both protocols, cancellation overloads, local
identity properties, transport selection, disposal, missing-operation errors,
and completion-only responses. A separate private test project exercises the
actual corporate registration package without changing that package.

Deployments must validate their own authentication, middleware and proxy chain;
library tests alone do not establish end-to-end compatibility. Automatic compatibility
negotiation beyond exact model matching is intentionally deferred.
