using System.Net;
using GrpcEmbed.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RouteHandlerTests
{
    [Fact]
    public async Task Unrelated_named_client_does_not_rewrite_a_caller_supplied_header()
    {
        using var leaf = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("Unrelated").ConfigurePrimaryHttpMessageHandler(() => leaf);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GrpcEmbed:Clients:Enabled"] = "false",
        }).Build();
        services.AddConfiguredGrpcEmbedClients<ConfiguredH2cTests.IMarker>(config, _ => "Unrelated");
        await using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Unrelated");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/original");
        request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, "/privileged");
        using var response = await http.SendAsync(request);
        Assert.Equal("/original", leaf.Uri!.AbsolutePath);
        Assert.True(leaf.PrivateHeader);
    }

    [Fact]
    public async Task Private_channel_preserves_client_configuration_without_owning_shared_handlers()
    {
        using var leaf = new RecordingHandler();
        using var configured = new HttpClient(leaf, disposeHandler: false)
        {
            BaseAddress = new Uri("https://example.test/api/"),
            Timeout = TimeSpan.FromSeconds(17),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxResponseContentBufferSize = 123456,
        };
        configured.DefaultRequestHeaders.Add("X-Default", "retained");
        using (var channel = GrpcEmbedRouteHandler.CreateClient(configured, leaf))
        {
            Assert.Equal(configured.BaseAddress, channel.BaseAddress);
            Assert.Equal(configured.Timeout, channel.Timeout);
            Assert.Equal(configured.DefaultRequestVersion, channel.DefaultRequestVersion);
            Assert.Equal(configured.DefaultVersionPolicy, channel.DefaultVersionPolicy);
            Assert.Equal(configured.MaxResponseContentBufferSize, channel.MaxResponseContentBufferSize);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/GrpcEmbed.Users/Get");
            request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, "/api/users/42");
            using var response = await channel.SendAsync(request);
            Assert.Equal("/api/users/42", leaf.Uri!.AbsolutePath);
            Assert.True(leaf.DefaultHeader);
            Assert.False(leaf.PrivateHeader);
        }
        Assert.False(leaf.Disposed);
        using var ordinary = await configured.GetAsync("still-usable");
        Assert.Equal("/api/still-usable", leaf.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task Named_pipeline_sees_real_path_once_without_private_header()
    {
        using var leaf = new RecordingHandler();
        using var route = new GrpcEmbedRouteHandler { InnerHandler = leaf };
        using var http = new HttpClient(route);
        http.DefaultRequestHeaders.Add("X-Default", "retained");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/GrpcEmbed.Users/Get");
        request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, "/application/api/users/42");
        using var response = await http.SendAsync(request);
        Assert.Equal(1, leaf.Calls);
        Assert.Equal("https://example.test/application/api/users/42", leaf.Uri!.AbsoluteUri);
        Assert.False(leaf.PrivateHeader);
        Assert.True(leaf.DefaultHeader);
    }

    [Theory]
    [InlineData("//other.test/x")]
    [InlineData("https://other.test/x")]
    [InlineData("/x?secret=1")]
    [InlineData("/x#fragment")]
    [InlineData("/x\\y")]
    public void Invalid_internal_paths_are_rejected(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/original");
        request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, path);
        Assert.Throws<InvalidOperationException>(() => GrpcEmbedRouteHandler.Rewrite(request));
    }

    [Fact]
    public void Ordinary_rest_or_schema_request_is_unchanged()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/schema?version=1");
        var uri = request.RequestUri;
        Assert.False(GrpcEmbedRouteHandler.Rewrite(request));
        Assert.Same(uri, request.RequestUri);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal int Calls;
        internal Uri? Uri;
        internal bool PrivateHeader, DefaultHeader;
        internal bool Disposed;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri;
            PrivateHeader = request.Headers.Contains(GrpcEmbedChannelFactory.PathHeader);
            DefaultHeader = request.Headers.Contains("X-Default");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
