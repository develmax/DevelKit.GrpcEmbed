# GrpcEmbed

<p align="center">
  <img src="assets/grpcembed-icon.png" width="160" alt="GrpcEmbed icon" />
</p>

Native gRPC for existing ASP.NET Core MVC APIs.

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

## Client

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
