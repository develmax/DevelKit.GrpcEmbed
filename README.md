# GrpcEmbed

<p align="center">
  <img src="assets/grpcembed-icon.png" width="160" alt="GrpcEmbed icon" />
</p>

Native gRPC for existing ASP.NET Core MVC APIs.

HTTP/2 by default, with opt-in HTTP/3 support. See [transport support](#transport-support-http2-and-http3).

NuGet package: `DevelKit.GrpcEmbed`.

[![CI](https://github.com/develmax/DevelKit.GrpcEmbed/actions/workflows/ci.yml/badge.svg)](https://github.com/develmax/DevelKit.GrpcEmbed/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DevelKit.GrpcEmbed.svg)](https://www.nuget.org/packages/DevelKit.GrpcEmbed)
[![License](https://img.shields.io/github/license/develmax/DevelKit.GrpcEmbed)](LICENSE)

**Your controllers. Your contracts. Now gRPC.**

GrpcEmbed adds a standard gRPC/Protocol Buffers transport to compatible MVC actions. It does not replace REST, invent an RPC protocol, use JSON in the gRPC path, or require generated protobuf DTOs.

```csharp
builder.Services.AddControllers();
builder.Services.AddGrpcEmbed();

app.MapControllers();
app.MapGrpcEmbed();
```

An ordinary `UsersController.Get(int id)` remains available as `GET /api/users/42` and is additionally exposed as `/GrpcEmbed.Users/Get`.

## Choosing which actions to expose

By default, `AddGrpcEmbed()` uses `GrpcEmbedExportMode.All`: it discovers MVC actions and exports compatible ones. This does not replace their REST routes.

To export all compatible actions and fail startup instead of silently skipping unsupported actions:

```csharp
using GrpcEmbed;

builder.Services.AddGrpcEmbed(options =>
{
    options.ExportMode = GrpcEmbedExportMode.All;
    options.ThrowOnUnsupportedAction = true;
});
```

Use `[GrpcIgnore]` on a controller or action to keep it REST-only:

```csharp
[HttpPost("upload")]
[GrpcIgnore]
public IActionResult Upload(IFormFile file) => Ok();
```

Alternatively, opt in explicitly:

```csharp
builder.Services.AddGrpcEmbed(options =>
{
    options.ExportMode = GrpcEmbedExportMode.ExplicitOnly;
    options.ThrowOnUnsupportedAction = true;
});

// Add to an existing MVC action, alongside its usual routing attributes:
[GrpcExport]
[HttpGet("{id:int}")]
public Task<UserDto> Get(int id, CancellationToken cancellationToken) => LoadUser(id, cancellationToken);
```

`[GrpcExport]` on a controller opts in all its compatible MVC actions. `[GrpcIgnore]` always wins, including when the controller has `[GrpcExport]`; attributes are inherited. These attributes do not disable REST or override authorization. A configured `ShouldExport` predicate is an additional filter: attributes cannot re-enable an action it excludes. Remove a method-name allowlist if you want all actions of the selected controllers.

`ThrowOnUnsupportedAction` controls diagnostics, not compatibility: it does not make file uploads, raw streams, or other unsupported contracts serializable. With `false` (the default), detected unsupported actions are logged and skipped; with `true`, startup fails. Explicitly ignored actions are not exported or validated for gRPC. Keep strict mode in tests to detect unexpected omissions. Successful route/schema construction is not a substitute for testing each action's business behavior.

## Client

The current source version is **2.0.0**. Before upgrading from
1.0.0, read the [migration guide](docs/migration-2.0.md) and
[changelog](CHANGELOG.md). Source version does not imply NuGet publication.
See [runtime client integration](docs/runtime-clients.md) and
[configurable contract safety](docs/contract-safety.md) for the new opt-in APIs.

```csharp
services.AddGrpcEmbedClient<IUsersApi>(options =>
{
    options.Address = new Uri("https://users-service");
    options.DefaultTimeout = TimeSpan.FromSeconds(10);
    options.TransportMode = GrpcEmbedTransportMode.GrpcOnly;
});

var user = await users.Get(42, cancellationToken);
```

Schema and reflection are disabled by default for production safety. Enable them explicitly during development:

```csharp
builder.Services.AddGrpcEmbed(options =>
{
    options.EnableSchemaEndpoint = true;
    options.EnableReflection = true;
    options.SchemaAuthorizationPolicy = "SchemaReaders"; // optional named authorization policy
});
```

This exposes `/_grpcembed/schema.proto`, `/_grpcembed/descriptor.pb`, `/_grpcembed/schema.json`, a deterministic schema hash at `/_grpcembed`, and standard `grpc.reflection.v1alpha.ServerReflection`. Set `SchemaManifestPath` to a checked-in manifest to detect removed, renumbered, renamed, or type-changed fields during startup. Clients may set `ExpectedSchemaHash` for strict contract negotiation.

For deliberate schema evolution, use `[GrpcFieldNumber(n)]` to preserve a field or parameter wire ID across a rename, and `[GrpcReservedField(n)]` on DTO types when removing fields. Reserved numbers are emitted into both `.proto` and the binary descriptor set.

## Transport support: HTTP/2 and HTTP/3

- **HTTP/2 (default):** the standard transport used by the GrpcEmbed client and sample server. Both generated clients and the contract proxy are covered by real-network HTTP/2 tests. The sample uses cleartext HTTP/2 locally; use HTTPS in production.
- **HTTP/3 (opt-in):** available through the same ASP.NET Core and .NET gRPC transports, without changing controllers or protobuf contracts. Requires HTTPS, platform QUIC support, and explicit client configuration. Both client variants have passed real-network HTTP/3 tests on Windows Server 2025 / .NET 10.0.12.

HTTP/3 is not enabled automatically. Existing HTTP/2 applications can keep their current configuration.

See the [HTTP/3 setup and verification guide](https://github.com/develmax/DevelKit.GrpcEmbed/blob/main/docs/http3.md) for server settings, the existing 1.0.0 contract-client API, generated clients, HTTP/2 compatibility, and real-network tests.

## Performance comparison

Measured with BenchmarkDotNet 0.15.2 ShortRun on .NET 10.0.11, Windows 10, and an Intel Core i5-12500H. All four paths use ASP.NET Core TestServer. Payload size is the length of the ASCII string in the DTO, before serialization.

**Mean latency per request (lower is better):**

| Payload | MVC + System.Text.Json | Native gRPC service | GrpcEmbed + generated client | GrpcEmbed + contract proxy |
|---:|---:|---:|---:|---:|
| 32 B | 119.57 us | 35.81 us | 85.19 us | 83.87 us |
| 200 B | 98.22 us | 28.11 us | 88.45 us | 102.90 us |
| 2 KiB | 153.78 us | 35.35 us | 96.09 us | 85.75 us |
| 20 KiB | 224.11 us | 88.64 us | 121.68 us | 116.09 us |

**Managed memory allocated per request (lower is better):**

| Payload | MVC + System.Text.Json | Native gRPC service | GrpcEmbed + generated client | GrpcEmbed + contract proxy |
|---:|---:|---:|---:|---:|
| 32 B | 17.37 KiB | 14.64 KiB | 22.14 KiB | 23.42 KiB |
| 200 B | 18.19 KiB | 15.29 KiB | 22.80 KiB | 24.05 KiB |
| 2 KiB | 27.45 KiB | 22.51 KiB | 36.02 KiB | 43.29 KiB |
| 20 KiB | 138.32 KiB | 116.29 KiB | 165.85 KiB | 229.19 KiB |

In this run, the contract proxy had approximately 44% lower mean latency than MVC + JSON at 2 KiB and 48% lower at 20 KiB. At 200 B, it was approximately 5% slower. GrpcEmbed allocated more memory than MVC + JSON and remained slower than the native gRPC service.

These are preliminary in-memory measurements: one launch, three warmup iterations, and three measurement iterations, with wide confidence intervals. They do not establish production network throughput or a guaranteed speedup. The native baseline is a separate generated gRPC service; both GrpcEmbed client variants invoke the MVC action through GrpcEmbed.

Reproduce the comparison using the [benchmark source](benchmarks/GrpcEmbed.Benchmarks/Program.cs):

```powershell
dotnet run --project benchmarks/GrpcEmbed.Benchmarks -c Release -- --filter *PayloadTransportBenchmarks*
```

## Current milestone

The repository contains runtime MVC discovery, cached dynamic contracts and accessors, deterministic field numbers, protobuf-net marshalling, standard native unary gRPC registration, controller DI activation, cancellation, authorization metadata, authorization/resource/action/result/exception filters, parameter and DTO validation, status mapping, schema/descriptor export, reflection, and an interface proxy. MVC binding-source metadata is retained for discovery and schema tooling; values on the gRPC path come from the protobuf request rather than MVC route/query/body value providers.

Version 1 intentionally implements unary RPC only. Custom MVC model binders, formatter-specific results, streaming, trimming and NativeAOT are not supported. Reflection.Emit and DispatchProxy are explicitly annotated with trimming/dynamic-code warnings so unsupported deployments fail visibly during analysis.

## Build

```powershell
dotnet restore GrpcEmbed.sln
dotnet build GrpcEmbed.sln -c Release
dotnet test GrpcEmbed.sln -c Release
dotnet run --project samples/GrpcEmbed.Sample.Server
dotnet run --project samples/GrpcEmbed.Sample.Client -- http://localhost:5001
dotnet run --project benchmarks/GrpcEmbed.Benchmarks -c Release
dotnet run --project benchmarks/GrpcEmbed.Benchmarks -c Release -- --filter *PayloadTransportBenchmarks*
dotnet pack src/GrpcEmbed -c Release -o artifacts/packages
```
## Support development

If this library helps you, you can support its maintenance, tests, and documentation.
Donations are optional; the library remains freely available under the MIT license.

[Patreon](https://www.patreon.com/develmax) · [Boosty](https://boosty.to/develmax/donate) · [YooMoney](https://yoomoney.ru/to/4100119529133322) · [PayPal](https://paypal.me/develmax)

## License

MIT. See [LICENSE](LICENSE).
