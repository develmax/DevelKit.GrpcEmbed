# GrpcEmbed implementation report

English | [Русский](IMPLEMENTATION.ru.md)

## Implemented

### 2.1 URL routing

- Configurable Rest, Method, ControllerMethod and Native addressing.
- REST-compatible gRPC paths selected by content type and operation metadata.
- Route/Protobuf consistency checks before controller creation; contract validation
  remains before deserialization.
- Stable channels/method descriptors with per-request URI rewriting, without an
  unbounded per-URL method cache.
- Optional anonymous schema downloads, independently of business authorization.
- Routing information included in deterministic hashes. See [migration and
  limitations](docs/url-routing.md).

Local 2.1 validation: 77 non-HTTP/3 tests passed. This is not a cluster deployment
or a measured performance comparison.

- MVC action discovery at startup through `IActionDescriptorCollectionProvider`.
- Export modes for all compatible actions or explicitly marked actions only.
- Support for `GrpcExport`, `GrpcIgnore`, `GrpcName`, and custom export predicates.
- Native unary gRPC methods registered through `IServiceMethodProvider<TService>` and hosted by `Grpc.AspNetCore`.
- One-time generation of CLR request types using Reflection.Emit and direct protobuf serialization without JSON.
- Stable protobuf field numbers independent of reflection order.
- Explicit `[GrpcFieldNumber]` assignments, `[GrpcReservedField]` reservations, and safe field renaming.
- Deterministic `.proto`, descriptor set, and SHA-256 schema hash.
- Standard gRPC Server Reflection v1alpha.
- MVC controller activation and release through `IControllerFactory`.
- Scoped DI, `HttpContext`, `HttpRequest`, `HttpResponse`, `ClaimsPrincipal`, service parameters, and `CancellationToken`.
- MVC authorization, resource, action, result, and exception filters.
- DataAnnotations validation for parameters and DTOs.
- Mapping of `ActionResult<T>`, `ObjectResult`, and HTTP status codes to gRPC responses and statuses.
- Contract clients based on ordinary C# interfaces using `DispatchProxy` and `Grpc.Net.Client`.
- Support for `Task<T>` and `ValueTask<T>`, DTOs, scalars, strings, byte arrays, and collections.
- `GrpcEmbedTransportMode.GrpcOnly`, deadlines, metadata, cancellation, and expected schema hash validation.
- A separate reusable channel for each client contract, with disposal through DI.
- Caching of schema hashes, request readers, argument plans, validation attributes, response wrappers, client request factories, and response readers during initialization.
- Binding sources and field numbers preserved in the discovery context and schema manifest.
- Diagnostic and schema endpoints disabled by default, with optional authorization policies.
- Interoperability with standard externally generated gRPC clients.
- An integration test verifying that the native gRPC path does not call System.Text.Json.
- Manifest compatibility validation for fields, services, methods, and request/response types.
- Enum declarations in `.proto` and descriptor sets.
- Runtime round-trip coverage for primitives, nullable values, collections, enums, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `decimal`, `byte[]`, and nested DTOs.
- Realistic Users, Orders, Payments, Files, and Admin sample controllers.
- `DevelKit.GrpcEmbed*` NuGet packages with MIT licensing, README, repository metadata, symbols, and deterministic builds.
- An independent net8.0 consumer that builds using only the local NuGet packages.

## Architecture

### Server

1. MVC provides a collection of `ControllerActionDescriptor` instances at startup.
2. The export policy excludes incompatible actions and actions marked with `GrpcIgnore`.
3. Protobuf request types are generated once for action parameters.
4. DTOs are registered in the shared protobuf-net `RuntimeTypeModel`.
5. Delegates for reading parameters, invoking actions, and wrapping responses are compiled in advance.
6. `Grpc.AspNetCore` registers standard gRPC routes such as `/GrpcEmbed.Users/Get`.
7. Each protobuf request is deserialized directly into a CLR object, then passed to the existing MVC action.
8. The MVC result is converted into a protobuf response without intermediate JSON serialization.

### Client

1. DI inspects the ordinary contract interface when it is first resolved.
2. Request types, marshallers, and request factory delegates are built once.
3. A dedicated `GrpcChannel` is created and reused for the contract.
4. An interface call sends a standard unary gRPC request through `CallInvoker`.
5. The protobuf response is mapped directly to the expected CLR type.

### Schema and compatibility

- The `.proto`, descriptor set, manifest, and SHA-256 schema hash are generated deterministically.
- The manifest contains services, methods, types, fields, binding sources, and field numbers.
- Clients can validate the `grpcembed-schema-hash` response header.
- Strict startup validation detects removed or renamed contracts, type changes, and unsafe field number reassignment.

## Version 1 limitations

- Custom MVC model binders, value providers, and formatter-specific results do not run on the protobuf path. Binding metadata is preserved, but arguments arrive as typed values from the protobuf request.
- `GrpcEmbedMethodType` anticipates streaming, but version 1 implements unary RPC only, as defined by the MVP scope in the specification.
- Reflection.Emit and DispatchProxy prevent NativeAOT and safe trimming support. Public entry points carry `RequiresDynamicCode` and `RequiresUnreferencedCode` annotations where supported by the target framework.
- File, stream, multipart, and other transport-specific MVC results are not exported.
- Ambiguous gRPC names, CLR overloads, unsupported asynchronous return types, and incorrectly placed `CancellationToken` parameters are rejected during client creation.

## Performance

BenchmarkDotNet ShortRun was executed on .NET 10.0.11 and an Intel Core i5-12500H through ASP.NET Core TestServer.

Each cell shows mean latency / managed memory allocated per operation.

| Payload | MVC + JSON | Manual native gRPC | GrpcEmbed + generated client | GrpcEmbed + contract proxy |
|---:|---:|---:|---:|---:|
| 32 B | 119.57 us / 17.37 KiB | 35.81 us / 14.64 KiB | 85.19 us / 22.14 KiB | 83.87 us / 23.42 KiB |
| 200 B | 98.22 us / 18.19 KiB | 28.11 us / 15.29 KiB | 88.45 us / 22.80 KiB | 102.90 us / 24.05 KiB |
| 2 KiB | 153.78 us / 27.45 KiB | 35.35 us / 22.51 KiB | 96.09 us / 36.02 KiB | 85.75 us / 43.29 KiB |
| 20 KiB | 224.11 us / 138.32 KiB | 88.64 us / 116.29 KiB | 121.68 us / 165.85 KiB | 116.09 us / 229.19 KiB |

This is a short in-memory run with wide confidence intervals, rather than a measurement of production network throughput. The original report estimated GrpcEmbed performance at approximately 42–76% of manual native gRPC, depending on payload and client. The aspirational 85–95% target has not been reached and remains a direction for further optimization.

## Framework compatibility

| Framework | Library build | Kestrel runtime validation |
|---|---|---|
| .NET 6 | Passed | REST and HTTP/2 gRPC passed |
| .NET 7 | Passed | ASP.NET runtime unavailable on the validation machine |
| .NET 8 | Passed | REST and HTTP/2 gRPC passed |
| .NET 9 | Passed | ASP.NET runtime unavailable on the validation machine |
| .NET 10 | Passed | REST and HTTP/2 gRPC passed |

The release build completed with no errors or warnings. Tests: 20 of 20 passed.

## NuGet packages

- `DevelKit.GrpcEmbed`
- `DevelKit.GrpcEmbed.AspNetCore`
- `DevelKit.GrpcEmbed.Client`
- `DevelKit.GrpcEmbed.Core`
- `DevelKit.GrpcEmbed.Abstractions`

Both `.nupkg` and `.snupkg` files were produced for each package. Old artifacts using the `GrpcEmbed.*` package IDs were removed.

Package metadata points to the repository at `https://github.com/develmax/DevelKit.GrpcEmbed`.

## Commands

Restore dependencies:

```powershell
dotnet restore GrpcEmbed.sln
```

Release build:

```powershell
dotnet build GrpcEmbed.sln -c Release -m:1 /nodeReuse:false
```

Run tests:

```powershell
dotnet test tests\GrpcEmbed.Tests\GrpcEmbed.Tests.csproj -c Release
```

Run the sample server:

```powershell
dotnet run --project samples\GrpcEmbed.Sample.Server -c Release -f net10.0
```

REST listens on `http://localhost:5000`; gRPC over HTTP/2 listens on `http://localhost:5001`.

Run the sample client:

```powershell
dotnet run --project samples\GrpcEmbed.Sample.Client -c Release -- http://localhost:5001
```

Run the transport benchmark:

```powershell
dotnet run --project benchmarks\GrpcEmbed.Benchmarks -c Release -- --filter *PayloadTransportBenchmarks*
```

Build the top-level NuGet package:

```powershell
dotnet pack src\GrpcEmbed\GrpcEmbed.csproj -c Release -o artifacts\packages
```

Export the schema after starting the sample server:

```powershell
curl.exe http://localhost:5000/_grpcembed/schema.proto
curl.exe http://localhost:5000/_grpcembed/descriptor.pb --output grpcembed.protoset
curl.exe http://localhost:5000/_grpcembed/schema.json
```

## Summary

GrpcEmbed provides a working, interoperable unary gRPC transport for existing ASP.NET Core MVC APIs. Controllers and DTOs can remain unchanged, GrpcEmbed clients use ordinary C# interfaces, and external standard gRPC clients can use the exported schema.
