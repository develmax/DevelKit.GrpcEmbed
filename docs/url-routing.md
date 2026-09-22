# REST-compatible gRPC URLs (2.1)

GrpcEmbed can expose a unary gRPC operation at its MVC route, including route
parameters. REST continues to use JSON and its original HTTP verb. gRPC uses
POST, `application/grpc`, Protobuf, and the `grpcembed-operation` metadata header.
This is a GrpcEmbed routing adapter, not the standard gRPC `/Service/Method`
addressing convention. Ordinary generated gRPC clients require `Native` mode.

## Server

```yaml
GrpcEmbed:
  Server:
    Enabled: true
    ExposeAll: true
    Routing:
      Mode: Rest
    Contract:
      Enabled: true
      ExposeEndpoint: true
      AllowAnonymous: true
      Validation: Required
```

`AllowAnonymous` applies only to schema download endpoints, including when the
host has a fallback authorization policy. It overrides `SchemaAuthorizationPolicy`
for these endpoints, not for reflection or business operations. Do not put secrets
in contract metadata. The default remains false for compatibility.

`ExposeAll` removes the need for `[GrpcExport]`; `[GrpcIgnore]` still excludes an
action or controller. Existing unsupported-action restrictions still apply.

## Existing configured client

```yaml
ClientServiceClient:
  BaseAddress: https://example.org/client-service/api/v1
  Grpc:
    Enabled: true
    Routing:
      Mode: Rest
    Contract:
      Address: https://example.org/client-service/_grpcembed/schema.json
      Fetch: FirstCall
      SendHash: true
      OnMismatch: RefreshAndRetry
      MaxRetries: 1
```

No generated-client changes are required. `Grpc.Address` optionally overrides
the complete base address (host, port and base path). Global client routing
settings live at `GrpcEmbed:Clients:Routing`; per-client settings override them.
Configured registration defaults to Rest and first-call contract fetching.
Direct code-based registration retains Native defaults.

The adapter infers the application mount prefix by matching the leading route
segments against the base address. For `api/v{version}/clients/{id}` and a base
address ending in `/client-service/api/v1`, it derives mount `/client-service`
and version `1`. For unusual layouts, set `Grpc:Routing:PathBase` explicitly
and supply non-argument values through `Grpc:Routing:Values` (for example,
`version: "1"`). These settings never override actual method arguments.

Supply `Contract.Address` explicitly when the schema is published below an
application prefix; its existing default remains `/_grpcembed/schema.json` at
the origin. Query/body arguments remain in Protobuf, not in the URL query string.

## Addressing modes

| Mode | Application-relative path |
| --- | --- |
| Rest | The MVC attribute route with parameter substitution |
| Method | `/grpc/Method` |
| ControllerMethod | `/grpc/Controller/Method` |
| Native | `/GrpcEmbed.Service/Method` |

`Routing.Prefix` changes `grpc` in the two named modes. Client and server modes
and prefixes must agree. Method mode rejects duplicate method names at startup.
Non-Native modes require a downloaded contract; use Native for fetch-free clients.

REST routing currently requires attribute routes. The client renderer supports
ordinary scalar placeholders, inline constraints, defaults, trailing optional segments
and catch-all values. Complex escaped-brace templates and inline regular-expression
constraints containing braces are not supported by the client renderer. Prefer
Native for such routes until an explicit interoperability test covers them.
Custom MVC parameter transformers are not executed on the client.

## Safety and lifetime

The endpoint selector separates JSON REST from gRPC before verb selection. The
operation header distinguishes GET and DELETE actions sharing a URL. Missing or
incorrect operation metadata must not fall back to REST. ASP.NET Core performs
route matching and constraints. Before controller creation, transported route
arguments are compared with matched route values; disagreement returns
InvalidArgument. The contract gate still runs before Protobuf deserialization.

Routes, HTTP verbs, parameter aliases and routing mode are included in the
deterministic schema hash. A 2.1 hash differs from 2.0; regenerate pinned hashes.
Contract changes that no longer match a route may produce a routing error instead
of the explicit not-executed refresh marker. No generic error is replayed.

Channels and configured HTTP pipelines are reused. The adapter changes each
request URI immediately before the existing HTTP pipeline. It does not create
a channel or DI container per call and does not cache one gRPC method per URL.
The internal path-transfer header is removed before the HTTP pipeline/network.

## One external HTTPS port

Keep one ingress prefix per application on port 443. Forward both REST and gRPC
to an HTTP/2 backend listener. The application dispatches by content type; ingress
does not need two competing rules for the same path. An HTTP/1 client can still
use REST when the ingress supports conversion to its HTTP/2 backend.

Keep an HTTP/1 listener for direct legacy callers and health probes if necessary.
Cleartext Kestrel `Http1AndHttp2` does not provide TLS/ALPN negotiation: use an
HTTP/2-only backend or configure TLS correctly. HTTPS at ingress does not encrypt
the ingress-to-pod leg when that listener uses `http://`.

Verify this configuration with the actual ingress before rollout. Local
application tests do not prove load-balancer behavior or cluster deployment.

## Upgrade from 2.0

Deploy matching 2.1 server/client configurations together on a test environment.
To keep old addresses, explicitly set configured clients and servers to Native.
Do not switch a server to Rest while old Native callers still use it. There is no
automatic REST fallback and no dual-address guarantee during a rolling update.
