# Changelog

All notable changes to this project are documented in this file.

## Unreleased

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
