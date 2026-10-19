using System.Net;
using GrpcEmbed.Client;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RouteHandlerTests
{
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri;
            PrivateHeader = request.Headers.Contains(GrpcEmbedChannelFactory.PathHeader);
            DefaultHeader = request.Headers.Contains("X-Default");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
