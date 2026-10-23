using System.Net;
using GrpcEmbed.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RouteHandlerTests
{
    [Fact]
    public async Task Fixed_channel_origin_preserves_schema_address_and_request_specific_paths()
    {
        using var leaf = new RecordingHandler();
        using var configured = new HttpClient(leaf, disposeHandler: false) { BaseAddress = new Uri("https://schema.test:8443/base/") };
        using var channel = GrpcEmbedRouteHandler.CreateClient(configured, leaf, "https://rpc.test:7443");
        using (await channel.GetAsync("https://schema.test:8443/schema?version=1"))
            Assert.Equal("https://schema.test:8443/schema?version=1", leaf.Uri!.AbsoluteUri);
        foreach (var path in new[] { "/application/api/users/42", "/application/api/users/73" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://rpc.test:7443/GrpcEmbed.Users/Get");
            request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, path);
            using var response = await channel.SendAsync(request);
            Assert.Equal("https://rpc.test:7443" + path, leaf.Uri!.AbsoluteUri);
            Assert.False(leaf.PrivateHeader);
        }
        Assert.Equal(3, leaf.Calls);
    }

    [Fact]
    public void Fixed_channel_origin_avoids_per_call_authority_string_allocation()
    {
        const string origin = "https://service.namespace.example.test:7443";
        var native = new Uri(origin + "/GrpcEmbed.Users/Get");
        long Measure(string? cachedOrigin)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, native);
                request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, "/api/users/42");
                Assert.True(GrpcEmbedRouteHandler.Rewrite(request, cachedOrigin));
            }
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        _ = Measure(null); _ = Measure(origin);
        var control = Measure(null);
        var candidate = Measure(origin);
        Assert.True(control - candidate >= 32_000, $"Expected at least 32 bytes saved per routed call: {control} vs {candidate}.");
    }

    [Fact]
    public async Task Private_client_preserves_parsed_default_authorization_and_per_request_override()
    {
        using var leaf = new AuthorizationRecordingHandler();
        using var configured = new HttpClient(leaf, disposeHandler: false);
        var authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new string('x', 2048));
        configured.DefaultRequestHeaders.Authorization = authorization;
        using var channel = GrpcEmbedRouteHandler.CreateClient(configured, leaf);
        // A string copy reparses and allocates a new value when this is read.
        Assert.Same(authorization, channel.DefaultRequestHeaders.Authorization);
        using (await channel.GetAsync("https://example.test/schema"))
            Assert.Equal(authorization.Parameter, leaf.Authorization!.Parameter);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/native");
        request.Headers.Add(GrpcEmbedChannelFactory.PathHeader, "/api/users/42");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "per-call");
        using (await channel.SendAsync(request))
            Assert.Equal("per-call", leaf.Authorization!.Parameter);
        Assert.Same(authorization, configured.DefaultRequestHeaders.Authorization);
        Assert.Same(authorization, channel.DefaultRequestHeaders.Authorization);
        using (await channel.GetAsync("https://example.test/schema"))
            Assert.Equal(authorization.Parameter, leaf.Authorization!.Parameter);
    }

    [Theory]
    [InlineData("@@@")]
    [InlineData("Bearer one", "Bearer two")]
    public void Private_client_keeps_invalid_or_multiple_authorization_values(params string[] values)
    {
        using var leaf = new RecordingHandler();
        using var configured = new HttpClient(leaf, disposeHandler: false);
        Assert.True(configured.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", values));
        using var channel = GrpcEmbedRouteHandler.CreateClient(configured, leaf);
        Assert.Equal(values, channel.DefaultRequestHeaders.NonValidated["Authorization"].ToArray());
    }

    [Fact]
    public void Private_client_does_not_retain_a_mutable_authorization_subclass()
    {
        using var leaf = new RecordingHandler();
        using var configured = new HttpClient(leaf, disposeHandler: false);
        var authorization = new MutableAuthorizationValue { Text = "Bearer original" };
        configured.DefaultRequestHeaders.Authorization = authorization;
        using var channel = GrpcEmbedRouteHandler.CreateClient(configured, leaf);
        authorization.Text = "Bearer changed";
        Assert.Equal("Bearer original", channel.DefaultRequestHeaders.NonValidated["Authorization"].ToString());
    }

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

    private sealed class MutableAuthorizationValue() : System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "unused")
    {
        public string Text { get; set; } = "";
        public override string ToString() => Text;
    }

    private sealed class AuthorizationRecordingHandler : HttpMessageHandler
    {
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
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
