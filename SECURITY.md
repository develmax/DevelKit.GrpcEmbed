# Security Policy

Do not disclose vulnerability details in public issues.

Use GitHub Security Advisories in this repository to privately report vulnerabilities to the project owner. Include the affected version, steps to reproduce, and potential impact.

Security fixes are released for the latest supported version.

## Transport security

Configured clients require HTTPS for non-loopback endpoints by default. Since
2.1.1, `GrpcEmbed:Clients:AllowUnencryptedHttp2` or a specific client's
`Grpc:AllowUnencryptedHttp2` can explicitly allow HTTP (h2c) for RPC and contract
discovery. A per-client value overrides the global default. This does not bypass
TLS certificate validation.

h2c carries tokens, headers and payloads without encryption. Enable it only on
trusted, access-controlled networks or behind independently verified transport
encryption. Keep HTTPS for external or untrusted connections. See
[runtime client configuration](docs/runtime-clients.md#explicit-unencrypted-http2-h2c-opt-in).

## Automated analysis

The [CodeQL workflow](.github/workflows/codeql.yml) analyzes C# on pull requests
to `main`, pushes to `main`, and weekly. Maintainers can also run it manually.
It uses a traced Release build of the solution and the `security-extended` query
suite, with results uploaded to GitHub Code Scanning. See GitHub's
[C# build guidance](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages).

CodeQL complements tests and review; a successful scan is not a guarantee that
the library is vulnerability-free. Runtime-generated proxy and Protobuf types
are not ordinary build-time source and require runtime validation as well.
