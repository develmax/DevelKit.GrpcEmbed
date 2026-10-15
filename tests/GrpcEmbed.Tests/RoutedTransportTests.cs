using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using GrpcEmbed.Client;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RoutedTransportTests
{
    [Theory]
    [InlineData("/api/v1/clients/42")]
    [InlineData("/api/a%20b")]
    [InlineData("/api/%D1%8F")]
    [InlineData("/api/one,two")]
    [InlineData("/api/")]
    public async Task Single_route_keeps_authority_escaping_auth_and_removes_private_header(string path)
    {
        using var terminal = new Terminal();
        using var invoker = Create(terminal);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://ignored.invalid/GrpcEmbed.Synthetic/Get");
        request.Headers.TryAddWithoutValidation(GrpcEmbedChannelFactory.PathHeader, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-only");
        request.Headers.TryAddWithoutValidation("grpcembed-contract-hash", new string('a', 64));
        using var response = await invoker.SendAsync(request, default);
        Assert.Equal(new Uri("http://target.invalid:7443" + path), request.RequestUri);
        Assert.Equal("synthetic-only", request.Headers.Authorization.Parameter);
        Assert.False(request.Headers.Contains(GrpcEmbedChannelFactory.PathHeader));
        Assert.Equal(new string('a', 64), Assert.Single(request.Headers.GetValues("grpcembed-contract-hash")));
        Assert.Equal(1, terminal.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("//foreign.invalid/path")]
    [InlineData("/api\\bad")]
    [InlineData("/api#bad")]
    [InlineData("/api?bad")]
    [InlineData("/api/\r\nbad")]
    [InlineData("/api/\rbad")]
    [InlineData("/api/\nbad")]
    public async Task Invalid_route_never_reaches_transport(string path)
    {
        using var terminal = new Terminal();
        using var invoker = Create(terminal);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://target.invalid");
        request.Headers.TryAddWithoutValidation(GrpcEmbedChannelFactory.PathHeader, path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, default));
        Assert.Equal(0, terminal.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_multiple_routes_never_reach_transport(bool multiple)
    {
        using var terminal = new Terminal();
        using var invoker = Create(terminal);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://target.invalid");
        if (multiple) request.Headers.TryAddWithoutValidation(GrpcEmbedChannelFactory.PathHeader, new[] { "/api/a", "/api/b" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, default));
        Assert.Equal(0, terminal.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_still_reaches_configured_client()
    {
        using var terminal = new Terminal();
        using var invoker = Create(terminal);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://target.invalid");
        request.Headers.TryAddWithoutValidation(GrpcEmbedChannelFactory.PathHeader, "/api/test");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, cancellation.Token));
        Assert.Equal(0, terminal.Calls);
    }

    private static HttpMessageInvoker Create(HttpMessageHandler terminal)
    {
        var type = typeof(GrpcEmbedChannelFactory).GetNestedType("RoutedTransport", BindingFlags.NonPublic)!;
        var client = new HttpClient(terminal);
        var handler = (HttpMessageHandler)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { client, true, new Uri("http://target.invalid:7443") }, null)!;
        return new HttpMessageInvoker(handler);
    }

    private sealed class Terminal : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
