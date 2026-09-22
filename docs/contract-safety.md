# Contract safety

REST endpoints remain available when their actions are also published through gRPC.
The client transport switch is independent of server publication.

## Opt-in guarded configuration

The following configuration enables contract publication and requires the caller
to identify the contract used to serialize its request:

    GrpcEmbed:
      Server:
        Enabled: true
        ExposeAll: false
        Contract:
          Enabled: true
          Generate: Startup
          ExposeEndpoint: true
          Validation: Required
          Hash:
            Enabled: true
            SendInResponses: true
      Clients:
        Enabled: false
        Contract:
          Fetch: FirstCall
          SendHash: true
          OnMismatch: RefreshAndRetry
          MaxRetries: 1
          RefreshInterval: "00:05:00"
    ExistingClient:
      Grpc:
        Enabled: true
        Address: https://service.example
        Contract:
          Address: https://service.example/_grpcembed/schema.json

Register the server with AddGrpcEmbed(configuration) and MapGrpcEmbed().
Register existing clients first, then call
AddConfiguredGrpcEmbedClients<IClientMarker>(configuration, client => client.ApiName).
The marker and name selector belong to the application; no concrete client list
is required. Client-specific Grpc.Contract values override global Clients.Contract
values. An explicit Grpc.Enabled overrides Clients.Enabled, including false.
The old GrpcEmbed.Enabled key is not used.

ExposeAll=false selects GrpcExport-marked controllers/actions. ExposeAll=true
selects all supported actions. GrpcIgnore excludes controllers/actions.
REST behavior is unchanged by these attributes.

## Generation, fetching and hashes

Server Contract.Generate is Startup or FirstRequest (default). Metadata and its
serialized JSON are cached in memory; no schema.json file is written to disk.
Hash validation or response-hash emission can cause first-use generation before
any explicit metadata GET.

Client Contract.Fetch is Never (default), Startup or FirstCall. Startup requires
credentials available without an incoming request. FirstCall shares concurrent
initialization. RefreshInterval is optional and checked on the next active call;
there is no background polling. ETags avoid downloading unchanged metadata.

With Fetch=Never, configure Grpc.ServiceName explicitly. Fetching can resolve it
automatically when the operation is unambiguous. An ambiguous match fails.
Fetching requires the configured HttpClient; the named-client integration supplies it.

Hashes use canonical schema content, not process IDs, timestamps, assembly
versions or discovery order. Pods publishing the same contract produce the same
hash. Local request/response model fingerprints must match before an operation
is sent. This is deliberately conservative: even an additive DTO change can
require rebuilding the client. Downloading metadata does not upgrade compiled DTOs.

## Mismatch handling

Server Validation supports Disabled (default), IfPresent and Required.
IfPresent accepts callers without a hash; Required rejects them.
For a mismatch, the server rejects before protobuf body deserialization and before
controller execution. Its FailedPrecondition response contains the specific
grpcembed-contract-rejection=not-executed marker and the current server hash.
The rejection hash is sent even when ordinary response hash emission is disabled.

Client OnMismatch defaults to Fail. RefreshAndRetry requires fetching and SendHash.
Only the explicit not-executed rejection triggers a refresh and bounded retry.
This includes writes because the rejected attempt did not execute the action.
Other errors, timeouts and completed responses are not automatically retried,
and there is no REST fallback. MaxRetries is 0 through 5 (default 1).
Cancellation and the original call timeout cover fetching and retries together.
Configure any preexisting HTTP retry handlers separately: GrpcEmbed does not
override them or make their retries safe.

During rolling updates, metadata and RPC requests can reach different pod versions.
Retries are bounded and may fail safely; this mechanism does not guarantee
availability across incompatible deployments. Clients keep one active snapshot.

## Disable the additional work

For a coordinated deployment where compatibility is managed externally:

    GrpcEmbed:
      Server:
        Contract:
          Enabled: false
          ExposeEndpoint: false
          Validation: Disabled
          Hash:
            Enabled: false
            SendInResponses: false
      Clients:
        Contract:
          Fetch: Never
          SendHash: false
          OnMismatch: Fail

Omit RefreshInterval and specify each enabled client's Grpc.ServiceName.
Disable legacy EnableReflection and EnableSchemaEndpoint options as well.
Protobuf runtime types and reusable proxies are still built: disabling contract
metadata does not eliminate serialization setup. No contract-fetch network
requests or per-call hash validation are performed in this mode.

Validation requires generation and hashing; contradictory settings fail startup.
Reflection requires generation. Hash.Enabled=false also omits operation fingerprints.
Consequently clients using guarded fetching cannot consume a hashless manifest.

## Security and rollout

Protect metadata with the existing SchemaAuthorizationPolicy option and configure
the same authentication used by your application. Metadata is not automatically
protected when no policy is specified. For ingress path prefixes, set the metadata
address explicitly rather than guessing from REST paths.

This implementation is local until packaged and deployed. Pilot services and Helm
charts must be migrated separately; existing manual adapters do not gain these
policies merely by changing configuration.
