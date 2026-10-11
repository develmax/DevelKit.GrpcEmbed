# Contributing

Thank you for your interest in GrpcEmbed.

Please follow our [Code of Conduct](CODE_OF_CONDUCT.md). Use the repository's
issue forms for bug reports, feature requests, and accessibility barriers. See
[ACCESSIBILITY.md](ACCESSIBILITY.md) for our accessibility goals and limitations.
Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Local validation

```powershell
dotnet restore GrpcEmbed.sln
dotnet build GrpcEmbed.sln -c Release -m:1 /nodeReuse:false
dotnet test tests\GrpcEmbed.Tests\GrpcEmbed.Tests.csproj -c Release
```

Changes to public contracts must include schema compatibility tests. Changes to the transport hot path must preserve interoperability with standard gRPC clients and keep JSON out of the gRPC path.

Before opening a pull request, make sure the build completes without warnings or errors.

## Publishing releases

See [automated NuGet.org publishing](docs/nuget-publishing.md) for the one-time
Trusted Publishing policy, version tags, safe dry runs and partial-release retries.
