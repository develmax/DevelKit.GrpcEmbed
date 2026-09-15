# HTTP/3 configuration

GrpcEmbed uses `Grpc.AspNetCore` on the server and `Grpc.Net.Client` on the client. Its protobuf contracts and MVC invocation pipeline do not depend on HTTP/2. You can configure HTTP/3 with the existing **1.0.0 API**; no controller, DTO, schema, or package changes are required.

The sample defaults remain cleartext HTTP/2. HTTP/3 is opt-in and requires HTTPS and QUIC on both ends. Building a library for a target framework does not establish HTTP/3 support on every OS that runs that framework.

## Prerequisites

- Use a supported .NET runtime, preferably .NET 10. HTTP/3 was experimental in .NET 6 and is fully supported by ASP.NET Core starting with .NET 7.
- Use an OS with .NET QUIC support: Windows 11 / Windows Server 2022 or later, or a supported Linux distribution with the appropriate `libmsquic` package. Windows 10 is not sufficient. Check the current [QUIC platform dependencies](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies) for installation details.
- Configure a valid HTTPS certificate trusted by the client. HTTP/3 uses TLS 1.3; cleartext `http://` endpoints cannot serve it.
- Allow UDP on the HTTPS endpoint port. Keep TCP available on the same port for HTTP/2 clients. Load balancers, container port mappings, firewalls, and proxies must be configured accordingly.

## Server

For an existing ASP.NET Core application, add this before `builder.Build()`:

```csharp
using GrpcEmbed.AspNetCore;
using Microsoft.AspNetCore.Server.Kestrel.Core;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5001, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
        listen.UseHttps(); // Uses the configured default certificate.
    });
});

builder.Services.AddControllers();
builder.Services.AddGrpcEmbed();

var app = builder.Build();
app.MapControllers();
app.MapGrpcEmbed();
app.Run();
```

Alternatively, in this repository's sample server, replace the existing `Kestrel:Endpoints:Grpc` entry in `appsettings.json` with:

```json
"Grpc": {
  "Url": "https://localhost:5001",
  "Protocols": "Http1AndHttp2AndHttp3"
}
```

Use one configuration approach; do not add a second listener on the same port. For local .NET client testing, provision a development certificate with `dotnet dev-certs https --trust` where supported. In production, configure the default Kestrel certificate through your deployment's secret/certificate provider; do not commit private keys or passwords.

See [Kestrel HTTP/3 configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/http3?view=aspnetcore-10.0).

## GrpcEmbed contract client (1.0.0)

`GrpcEmbedClientOptions` exposes `HttpHandler`, but does not expose `GrpcChannelOptions.HttpVersion`. Set the version on each outgoing request with a delegating handler:

```csharp
using System.Net;
using System.Net.Http;
using GrpcEmbed.Client;
using Microsoft.Extensions.DependencyInjection;

// Keep this handler alive for the lifetime of the client service provider.
using var http3Handler = new Http3Handler
{
    InnerHandler = new SocketsHttpHandler()
};

var services = new ServiceCollection();
services.AddGrpcEmbedClient<IUsersApi>(options =>
{
    options.Address = new Uri("https://localhost:5001");
    options.DefaultTimeout = TimeSpan.FromSeconds(10);
    options.HttpHandler = http3Handler;
});

using var provider = services.BuildServiceProvider();
var users = provider.GetRequiredService<IUsersApi>();
var user = await users.Get(42, CancellationToken.None);

// IUsersApi is your existing GrpcEmbed contract interface.
public sealed class Http3Handler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version30;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return base.SendAsync(request, cancellationToken);
    }
}
```

This requests **HTTP/3 only**, so a successful call cannot silently fall back to HTTP/2. GrpcEmbed 1.0.0 does not take ownership of a custom `HttpHandler`: dispose it after the client service provider, as above. In a long-running host, retain it until host shutdown. Do not create a new handler or channel per RPC.

Certificate validation remains enabled. Do not use an accept-all certificate callback in production. If an environment HTTP proxy prevents direct QUIC connections, use a network path that supports HTTP/3; set `SocketsHttpHandler.UseProxy = false` only when a direct connection is intended and permitted.

## Standard generated gRPC client

With a generated client, set the options directly:

```csharp
using System.Net;
using System.Net.Http;
using Grpc.Net.Client;

using var channel = GrpcChannel.ForAddress("https://localhost:5001",
    new GrpcChannelOptions
    {
        HttpVersion = HttpVersion.Version30,
        HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact
    });

// Use the generated client from GrpcEmbed's exported schema.
var client = new Users.UsersClient(channel);
```

## HTTP/2 compatibility and deployment

For an HTTP/2 baseline with opportunistic HTTP/3 upgrades, set `Version20` and `RequestVersionOrHigher` instead of `Version30` and `RequestVersionExact` in the handler (or the channel options). Kestrel advertises HTTP/3 through `Alt-Svc`; initial calls may still use HTTP/2. This is not a guarantee that every call uses QUIC. Do not use `Version30` plus `RequestVersionOrLower` as an HTTP/2-only fallback policy: it also permits HTTP/1.1, which is not the native gRPC transport used here.

A reverse proxy may accept HTTP/3 from the client but forward HTTP/2 to GrpcEmbed. That is valid, but it is not end-to-end HTTP/3. Confirm the protocol on each hop. HTTP/3 does not make native browser gRPC available and does not imply support in every third-party gRPC client.

## Verification

The [transport tests](../tests/GrpcEmbed.Tests/Http3Tests.cs) start a real loopback Kestrel HTTPS listener with a short-lived, test-pinned certificate. They exercise both a generated client and the GrpcEmbed contract proxy, verify DTO values and `NotFound` gRPC statuses, and assert that **all four requests arrive as `HTTP/3`**. A separate test checks HTTP/2. These are network integration tests, not TestServer simulations or performance benchmarks.

```powershell
$env:GRPCEMBED_REQUIRE_HTTP3 = '1'
dotnet test tests/GrpcEmbed.Tests/GrpcEmbed.Tests.csproj -c Release --filter FullyQualifiedName~Http3Tests --logger "console;verbosity=detailed"
Remove-Item Env:GRPCEMBED_REQUIRE_HTTP3
```

Without the environment variable, the HTTP/3 test is explicitly skipped on platforms without QUIC. With it, missing QUIC support fails the test. CI requires this test on its Windows runner; Linux runs it when QUIC is available. Local validation on Windows 10 / .NET 10.0.11 passed HTTP/2 and skipped HTTP/3, so that local result alone is not evidence of HTTP/3 interoperability. See the [CI results](https://github.com/develmax/DevelKit.GrpcEmbed/actions/workflows/ci.yml) for the required HTTP/3 run.

For a deployed server, inspect `HttpContext.Request.Protocol` or Kestrel request logs. A successful call, an `Alt-Svc` header, or an open UDP port alone does not prove HTTP/3 was used. The README performance results were measured with TestServer and do not compare HTTP/2 with HTTP/3.
