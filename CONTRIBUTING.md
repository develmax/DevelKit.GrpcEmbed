# Contributing

Thank you for your interest in GrpcEmbed.

## Local validation

```powershell
dotnet restore GrpcEmbed.sln
dotnet build GrpcEmbed.sln -c Release -m:1 /nodeReuse:false
dotnet test tests\GrpcEmbed.Tests\GrpcEmbed.Tests.csproj -c Release
```

Changes to public contracts must include schema compatibility tests. Changes to the transport hot path must preserve interoperability with standard gRPC clients and keep JSON out of the gRPC path.

Before opening a pull request, make sure the build completes without warnings or errors.
