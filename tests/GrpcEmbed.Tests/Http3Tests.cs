using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcEmbed.AspNetCore;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using GrpcEmbed.Tests.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GrpcEmbed.Tests;

public sealed class Http3Tests(ITestOutputHelper output)
{
    [Http3Fact]
    public Task Generated_and_contract_clients_use_real_http3() => VerifyTransport(HttpVersion.Version30);

    [Fact]
    public Task Generated_and_contract_clients_keep_http2_support() => VerifyTransport(HttpVersion.Version20);

    private async Task VerifyTransport(Version version)
    {
        // Real loopback Kestrel, not TestServer: verify the protocol negotiated on the wire.
        if (version.Major == 3)
            Assert.True(QuicListener.IsSupported && QuicConnection.IsSupported, "HTTP/3 requires platform QUIC support.");

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generatedCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generatedCertificate.Export(X509ContentType.Pfx), null);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        // Ignore sample appsettings copied to the test output; bind only the isolated test endpoint.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = version.Major == 3 ? HttpProtocols.Http2 | HttpProtocols.Http3 : HttpProtocols.Http2;
            listen.UseHttps(certificate);
        }));
        builder.Services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddGrpcEmbed();
        await using var app = builder.Build();
        var protocols = new ConcurrentQueue<string>();
        app.Use(async (context, next) =>
        {
            protocols.Enqueue(context.Request.Protocol);
            await next(context);
        });
        app.MapGrpcEmbed();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await app.StartAsync(timeout.Token);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            SocketsHttpHandler CreateTransport() => new()
            {
                UseProxy = false,
                // Pin this test's certificate without changing system trust or disabling validation globally.
                SslOptions = new() { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == certificate.GetCertHashString() }
            };

            using var generatedTransport = CreateTransport();
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = generatedTransport,
                HttpVersion = version,
                HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact
            });
            var generated = new Users.UsersClient(channel);
            var user = await generated.GetAsync(new Users_Get_Request { Id = 42 }, cancellationToken: timeout.Token);
            Assert.Equal(42, user.Id);
            Assert.Equal("Test", user.Name);
            var missing = await Assert.ThrowsAsync<RpcException>(async () =>
                await generated.MissingAsync(new Users_Missing_Request { Id = 99 }, cancellationToken: timeout.Token));
            Assert.Equal(StatusCode.NotFound, missing.StatusCode);

            // Exercise the existing 1.0 client API, as documented in docs/http3.md.
            using var handler = new ExactVersionHandler(version) { InnerHandler = CreateTransport() };
            var services = new ServiceCollection();
            services.AddGrpcEmbedClient<IUsersApi>(options =>
            {
                options.Address = new Uri(address);
                options.HttpHandler = handler;
                options.DefaultTimeout = TimeSpan.FromSeconds(15);
            });
            using var provider = services.BuildServiceProvider();
            var proxy = provider.GetRequiredService<IUsersApi>();
            var proxyUser = await proxy.Get(43, timeout.Token);
            Assert.Equal(43, proxyUser.Id);
            Assert.Equal("Test", proxyUser.Name);
            var proxyMissing = await Assert.ThrowsAsync<RpcException>(() => proxy.Missing(99, timeout.Token));
            Assert.Equal(StatusCode.NotFound, proxyMissing.StatusCode);

            Assert.Equal(4, protocols.Count);
            Assert.All(protocols, protocol => Assert.Equal($"HTTP/{version.Major}", protocol));
            output.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            output.WriteLine($"Generated client + contract proxy: DTOs and NotFound trailers passed; server protocols: {string.Join(", ", protocols)}");
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(stopTimeout.Token);
        }
    }

    [GrpcName("Users")]
    public interface IUsersApi
    {
        Task<Sample.Server.UserDto> Get(int id, CancellationToken cancellationToken = default);
        Task<Sample.Server.UserDto> Missing(int id, CancellationToken cancellationToken = default);
    }

    private sealed class ExactVersionHandler(Version version) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = version;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class Http3FactAttribute : FactAttribute
{
    public Http3FactAttribute()
    {
        // Required CI runs must fail rather than silently skip missing QUIC support.
        if (Environment.GetEnvironmentVariable("GRPCEMBED_REQUIRE_HTTP3") != "1" &&
            (!QuicListener.IsSupported || !QuicConnection.IsSupported))
            Skip = "The current OS/runtime has no QUIC support. Set GRPCEMBED_REQUIRE_HTTP3=1 to require it.";
    }
}
