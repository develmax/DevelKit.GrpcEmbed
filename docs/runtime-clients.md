# Runtime client integration

The runtime client changes are implemented locally. Pilot services and Helm charts
have not yet been migrated to this API. No packages have been published.

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
must use HTTPS. Existing HTTP authentication and request handlers remain active.
Existing handler retry and certificate policies are not silently overwritten:
the application must configure them appropriately, especially for writes.

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

Library integration tests cover both protocols, cancellation overloads, local
identity properties, transport selection, disposal, missing-operation errors,
and completion-only responses. A separate private test project exercises the
actual corporate registration package without changing that package.

Pending: migrate the pilot service
registrations and configuration, remove manual adapters, update chart templates,
prepare package versions, and run service-level tests. Automatic compatibility
negotiation beyond exact model matching is intentionally deferred.
