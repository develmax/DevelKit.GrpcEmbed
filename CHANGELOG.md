# Changelog

All notable changes to this project are documented in this file.

## 4.1.1 — 2026-10-21

- Route configured named-client calls inside their existing HTTP handler pipeline instead of forwarding them through a second `HttpClient.SendAsync`. Preserve configured headers, audit/authentication handlers, cancellation, ownership and route validation; manually supplied HTTP clients and custom factories retain the existing fallback. No protobuf wire or contract changes.
- Preserve the 4.x schema hashes and Reflection/native-alias behavior; existing contracts do not require regeneration. This patch removes a redundant client wrapper, not a guaranteed end-to-end GET latency improvement. Controlled pilot measurements did not establish a reproducible latency gain.

## 4.1.0 — 2026-10-19

- Expose both `grpc.reflection.v1.ServerReflection` and `grpc.reflection.v1alpha.ServerReflection` when reflection is enabled. Resolve exact symbols, filenames and transitive dependencies, including reflection's own descriptors, from one cached schema snapshot.
- Bind `GrpcEmbed:Server:EnableReflection` from configuration. `Contract.AllowAnonymous` now applies to discovery through Reflection as well as HTTP schema endpoints; otherwise the existing schema policy and host fallback authorization apply. Business authorization is unchanged.
- Add opt-in `GrpcEmbed:Server:Routing:EnableNativeAliases` (default `false`). Standard `/GrpcEmbed.Service/Method` endpoints can coexist with Rest, Method or ControllerMethod routes, retaining controller/filter metadata, payload audit hooks and pre-execution contract validation. Endpoint conventions returned by `MapGrpcEmbed()` apply to both sets of business routes.
- Keep the 4.0.x protobuf wire format, business descriptors and schema hashes unchanged. Reflection clients can fetch descriptors without a local `.proto`; required contract hashes and business credentials must still be supplied as metadata. See [routing and Reflection](docs/url-routing.md).

## 4.0.7 — 2026-10-16

- Add an opt-in `DevelKit-GrpcEmbed-Server` EventSource event around the actual server response marshaller, including protobuf serialization and `SerializationContext.Complete()`, but not HTTP response flushing. The payload contains only three numeric fields: start timestamp, elapsed Stopwatch ticks and success (0/1); it never contains DTOs, headers, identifiers or exception text.
- With no listener, response serialization does not read a clock or emit events; a warm-path regression verifies no additional serialization allocations. Diagnostic listener failures do not replace business serialization results or exceptions.
- Preserve the 4.0.x wire format, schema fingerprints, controller execution, authorization, audit, masking and cancellation. Diagnostics are for separate instrumented experiments, not a claim of improved business latency. See [server diagnostics](docs/server-diagnostics.md).

## 4.0.6 — 2026-10-16

- Materialize MVC filters without a per-request captured LINQ selector/iterator. Preserve descriptor order, request-scoped factory resolution, exception propagation and separate execution/context collections. An isolated .NET 6 probe saves 160 bytes per materialization; this is not a whole-request latency claim.
- Bind the captured client operation resolver delegate once per proxy method instead of allocating it on every RPC. Preserve contract refresh, parallel argument isolation and cancellation. An isolated .NET 6 probe saves 64 bytes per call.
- Add scoped factory, exception, repeated-call and parallel-call regressions. The 4.0.x wire format, schema fingerprints, authorization, full body-audit and masking hooks are unchanged; existing 4.x contracts do not require regeneration.

- Read the private per-call route header without materializing its values array or LINQ enumerator. Preserve exact single-value checks, URL restrictions, configured authority, cancellation and private-header removal. No wire, authorization or audit behavior changes; this reduces a small routing allocation, not a claimed business-latency speedup.
- Pre-bind fixed URL parts for REST-compatible gRPC routes with one varying scalar. Avoid the per-call path builder and intermediate path string; preserve escaping, dot-segment rejection, default/empty/optional handling and general-route fallback. Cache only route plans, never argument values, DTOs, tokens or responses. No wire, schema, authorization, audit or masking changes; no universal end-to-end latency gain is claimed.

## 4.0.5 — 2026-10-15

- Reuse one request-local MVC filter-context list instead of copying it for each authorization/resource/action/result context. Keep the execution order stable and preserve parallel-request isolation, short circuits and exception handling.
- Bind successful client operation/shape resolution to the loaded contract snapshot. Avoid repeated operation scans and key construction on warm calls; resolve and validate again after contract refresh. No request DTOs, responses or authorization metadata are cached.
- Add concurrent filter-context and contract-refresh/allocation regressions. The 4.0.x wire format, schema fingerprints, authorization, body-audit and masking hooks are unchanged; no universal business-latency speedup is claimed.

## 4.0.4 — 2026-10-15

- Prepare and cache typed `ActionResult<T>` result/value readers, avoiding per-response property reflection. Preserve direct values, nested MVC results, nulls and HTTP-to-gRPC error mapping.
- Avoid creating parameter validation contexts when there are no parameter validation attributes. Object/property validation and `IValidatableObject` still run, including when no parameter attributes exist.
- Add validation, result-shape and allocation regressions. No wire, descriptor, hash, authorization, filter or audit changes; no universal end-to-end latency improvement is claimed.

## 4.0.3 — 2026-10-15

- Prepare typed MVC `Task<T>` and `ValueTask<T>` result readers during discovery instead of looking up `Result`/`AsTask` with reflection on every call. Return results with `ValueTask<object?>`, avoiding an extra result task for synchronously completed reference results.
- Preserve original exceptions, cancellation and single-consumption `IValueTaskSource<T>` behavior. Add completed/pending/faulted/cancelled result and allocation regressions.
- Preserve the 4.0.x wire format, descriptors, contract hashes, authorization, filters and audit hooks. This reduces adapter overhead; it does not guarantee lower end-to-end latency for every workload.

## 4.0.2 — 2026-10-14

- Measure stable scalar request envelopes before writing, allowing Grpc.Net.Client to use its pooled frame buffer without an intermediate growing buffer and copy. Complex DTO and collection arguments retain the existing serialization path.
- Skip unrelated MVC filter categories in-place rather than creating recursive async frames. Preserve applicable authorization, resource, action and result filters and their order.
- Add payload-byte/length and mixed-category filter-order regressions. The 4.0.x wire format, descriptors and contract fingerprints are unchanged; no Protobuf regeneration is needed.

## 4.0.1 — 2026-10-14

- Fix serializer-construction stack overflow for direct and mutually recursive collection DTOs. Use a public serializer provider with deferred wire serializer resolution instead of eagerly rebuilding a surrogate chain. Preserve the 4.0.0 wire profile, descriptors and contract fingerprints.
- Add recursive collection, mutual array/interface recursion and cyclic-object rejection regressions.

## 4.0.0 — 2026-10-14

- Breaking wire change: presence-aware collection and item wrappers preserve null, empty and populated collections, including nested properties, arrays and supported collection interfaces. Nullable scalar and reference items retain presence, including zero and empty values.
- Export matching Protobuf messages and reflection descriptors with optional item values. Keep existing DTO field numbers and level-300 special type encoding.
- Change schema and CLR shape fingerprints so configured contract discovery/validation rejects 3.x peers before business execution. Fetch-free or validation-disabled clients still require operational version coordination.
- Compile attributed wire surrogates once for collection-bearing DTOs using public protobuf-net APIs. DTOs without collections keep direct serialization. Deserialization restores transmitted values without running DTO initializers.
- Add nested collection, interface, standard generated-client request/response and reflection regression coverage. REST endpoints, authorization, audit hooks and masking integrations remain enabled and unchanged.
- Reject dictionary/map shapes before registration/export rather than advertising an unsupported key/value contract or losing nullable values. Server discovery skips them, or fails in strict mode; REST remains available.
- See [MIGRATING-4.0.md](MIGRATING-4.0.md). Do not mix 3.x and 4.x serializers behind one gRPC address.

- Reuse a bound client's resolved RPC method and operation identity for a prepared contract snapshot. Rebuild them after contract refresh; preserve routing, audit payloads, metadata guards and cancellation.

## 3.1.1 — 2026-10-12

- Resolve operations without per-call LINQ candidate arrays when the client discovers the service automatically. Preserve model-shape validation, ambiguous-operation errors and refreshed contract snapshots.
- Reuse the configured server authority when constructing routed RPC URLs. A named HTTP client's base address does not override that authority; route validation remains unchanged.
- Add regression coverage for warmed resolution allocations, contract refresh, ambiguous methods and configured routing authority.
- Preserve 3.0.x wire format, contract hashes, authorization, audit hooks and cancellation. These changes reduce client overhead; they do not guarantee lower latency for every workload.

## 3.1.0 — 2026-10-12

- Add opt-in `CaptureAuditPayloads` integration for runtime clients. Application audit adapters can observe the logical request body and actual decoded response DTO, with final status, failure and resource-release callbacks.
- Add MVC endpoint audit hooks for decoded request bodies after contract validation and unwrapped response DTOs after MVC filters. Integrations do not consume or replace Protobuf streams or trailers.
- Use published body-binding metadata for REST-compatible request projections. Applications retain responsibility for JSON settings, masking, body limits and audit sinks.
- Preserve 3.0.x wire format and contract hashes. Capture is disabled by default and performs no automatic logging. A callback failure after a completed RPC cannot trigger a contract-mismatch retry.
- Add regression coverage for typed payloads, concurrent-call isolation, callback cleanup and unchanged RPC status.

## 3.0.2 — 2026-10-12

- Allocate a contract timeout timer only for actual contract loading/refresh and
  semaphore waits, not every warmed RPC. Preserve the original end-to-end deadline
  across contract I/O, calls and guarded retries; cancel underlying fetch I/O on timeout.
- Reuse cached MVC parameter metadata instead of reflecting argument arrays on
  each invocation. Avoid creating resource-filter contexts for unrelated filters
  or when no resource filters apply. Authorization and action/result filters remain active.
- Add regression tests for cold-fetch cancellation and expired refresh deadlines.
- Preserve 3.0.x wire format and contract fingerprints. These are overhead reductions,
  not a guarantee of lower database/network latency or universally better p99.

## 3.0.1 — 2026-10-12

- Reduce client routed-call allocations by binding route prefixes and arguments
  once per method/contract snapshot, reusing prepared contracts, consolidating
  metadata copying, and compiling request factories during proxy initialization.
- Preserve wire compatibility, contract guards, cancellation, refresh/retry and
  URI escaping; see [client performance](docs/client-performance.md).

## 3.0.0 — 2026-10-12

- Fix standard Protobuf interoperability for GUID, decimal, DateTime and TimeSpan
  by explicitly selecting compatibility level 300 for manually registered types.
- Unwrap nullable collection element types in exported .proto and reflection
  descriptors, removing undefined `Nullable` references.
- Omit `type_name` for scalar descriptor fields instead of emitting an explicitly
  empty symbol name, so standard descriptor resolvers accept the full schema.
- Add generated Google.Protobuf/gRPC client tests for special CLR values and
  nullable collections, rather than relying only on symmetric .NET round trips.
- **Breaking wire correction:** change contract and CLR-shape fingerprint
  revisions. Upgrade clients and servers together; old/new model shapes fail
  contract preparation even after a hash refresh. See
  [wire-format migration](docs/wire-format-migration.md). This is not a compatible
  replacement for 2.1.2. Upgrade clients and servers together.

## 2.1.2 — 2026-10-11

- Restore empty collections, arrays, strings and byte arrays in successful
  wrapped client responses when Protobuf omits their default/empty field.
- Keep genuine null MVC results as RPC errors; do not normalize nullable DTO
  properties or bypass completion/status validation.
- Compile empty-value construction once per response type, retaining reusable
  runtime proxies and channels without per-call reflection.
- Client-only correction: no protobuf field-number, schema-hash or server
  configuration changes. Existing 2.1.1 servers remain compatible.
- Add regression coverage for empty/nonempty collection interfaces and arrays,
  empty strings/bytes, and genuine null-result errors.

## 2.1.1

- Use HTTP/2 exactly for opted-in HTTP contract discovery without changing named HTTP client defaults or unrelated REST requests.

- Add explicit `GrpcEmbed:Clients:AllowUnencryptedHttp2` and per-client
  `Grpc:AllowUnencryptedHttp2` settings for trusted-network h2c integrations.
  The per-client flag overrides the global default, including explicit false.
- Keep HTTPS required by default for non-loopback addresses. The opt-in applies
  to both RPC and contract discovery addresses and never permits non-HTTP schemes
  or disables TLS certificate validation.

## 2.1.0

- Validate resolved contract download URLs as well as gRPC endpoints for HTTPS.
- Preserve asynchronous disposal of decorated clients, including initialization failures.
- Added REST-compatible gRPC URLs, configurable Method/ControllerMethod/Native
  addressing, and operation metadata for actions sharing a REST route.
- Added route/Protobuf argument consistency checks before controller execution.
- Preserved reusable channels without per-URL gRPC method-cache growth.
- Added explicit anonymous schema downloads without weakening business authorization.
- Included routing information in deterministic contract hashes. Regenerate pinned hashes.
- Configured clients now default to Rest routing and first-call contract fetching;
  set Routing.Mode to Native to preserve 2.0 addressing. Direct registration keeps
  Native defaults. See [URL routing and upgrade notes](docs/url-routing.md).

## 2.0.0 — 2026-09-22

### Added

- Reusable runtime proxies for existing singleton interface clients and named HTTP clients, without modifying generated clients.
- Global and per-client transport selection; REST endpoints remain available.
- Optional startup/first-call metadata fetching, conditional refresh, deterministic contract hashes and exact model fingerprints.
- Pre-deserialization server validation and bounded refresh/retry only after an explicit operation-not-executed rejection.
- Completion-only Task/ValueTask responses and inherited client interfaces.

### Breaking changes

- Schema hashes now use a canonical schema representation instead of descriptor bytes. Previously pinned ExpectedSchemaHash values must be regenerated.
- Client method names strip the Async suffix by default. Use StripAsyncSuffix=false or an explicit GrpcName to preserve legacy wire names.
- The new configuration registration uses GrpcEmbed:Server:Enabled, not the former application-level GrpcEmbed:Enabled switch. Direct callback-based registration remains supported.
- Required contract validation rejects legacy clients. Use IfPresent or Disabled during migration; guarded metadata fetching requires upgraded servers.
- This is not a verified drop-in replacement for 1.0.0. See [the migration guide](docs/migration-2.0.md).

### Fixed

- Keep response headers writable while MVC actions and filters execute. The schema hash is now queued without flushing the response, allowing headers such as `X-Total-Count` to reach gRPC clients. Schema-hash validation and error responses remain covered by transport tests.

## 1.0.0 — 2026-09-15

First stable release.

### Added

- Runtime discovery of existing ASP.NET Core MVC controllers.
- Standard native unary gRPC endpoints and Protocol Buffers without JSON.
- Contract-based .NET clients using ordinary C# interfaces.
- Export of `.proto`, descriptor sets, schema manifests, and SHA-256 schema hashes.
- gRPC Server Reflection, authorization, MVC filters, DI, validation, and cancellation.
- Stable field numbers, reserved fields, and diagnostics for incompatible changes.
- Support for .NET 6, 7, 8, 9, and 10.
- Sample applications, integration tests, and a BenchmarkDotNet project.
