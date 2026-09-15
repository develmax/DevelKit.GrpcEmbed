# Changelog

All notable changes to this project are documented in this file.

## Unreleased

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
