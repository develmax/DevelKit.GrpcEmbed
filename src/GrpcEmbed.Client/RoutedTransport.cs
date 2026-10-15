using Grpc.Net.Client;

namespace GrpcEmbed.Client;

internal static class GrpcEmbedChannelFactory
{
    public const string PathHeader = "grpcembed-internal-path";
    public static GrpcChannel Create(GrpcEmbedClientOptions options)
    {
        if (options.Routing.Mode == GrpcEmbedRoutingMode.Native)
            return GrpcChannel.ForAddress(options.Address, new GrpcChannelOptions
            {
                HttpClient = options.HttpClient, HttpHandler = options.HttpHandler,
                DisposeHttpClient = options.DisposeHttpClient,
            });
        if (options.HttpClient is null || options.HttpHandler is not null)
            throw new InvalidOperationException("URL routing requires a configured HttpClient and no separate HttpHandler.");
        var origin = new Uri(options.Address.GetLeftPart(UriPartial.Authority));
        return GrpcChannel.ForAddress(origin, new GrpcChannelOptions
        {
            HttpHandler = new RoutedTransport(options.HttpClient, options.DisposeHttpClient, origin),
            DisposeHttpClient = true,
        });
    }

    private sealed class RoutedTransport : HttpMessageHandler
    {
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly string _origin;
        public RoutedTransport(HttpClient client, bool ownsClient, Uri origin) { _client = client; _ownsClient = ownsClient; _origin = origin.GetLeftPart(UriPartial.Authority); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!request.Headers.NonValidated.TryGetValues(PathHeader, out var values))
                throw new InvalidOperationException("Missing internal route address.");
            if (values.Count != 1)
                throw new InvalidOperationException("Expected one internal route address.");
            var path = values.ToString();
            request.Headers.Remove(PathHeader);
            if (path.AsSpan().IndexOfAny('\r', '\n') >= 0 || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('#') || path.Contains('?'))
                throw new InvalidOperationException("Invalid internal route address.");
            request.RequestUri = new Uri(_origin + path);
            return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsClient) _client.Dispose();
            base.Dispose(disposing);
        }
    }
}
