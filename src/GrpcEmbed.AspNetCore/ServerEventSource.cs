using System.Diagnostics.Tracing;

namespace GrpcEmbed;

// Numeric, opt-in events only. No request identifiers, DTOs, headers or exception text.
[EventSource(Name = "DevelKit-GrpcEmbed-Server")]
internal sealed class GrpcEmbedServerEventSource : EventSource
{
    internal static readonly GrpcEmbedServerEventSource Log = new();

    private GrpcEmbedServerEventSource() { }

    public static class Keywords
    {
        public const EventKeywords Serialization = (EventKeywords)1;
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Serialization)]
    public void ResponseSerialization(long startTimestamp, long elapsedTicks, long succeeded)
    {
        WriteEvent(1, startTimestamp, elapsedTicks, succeeded);
    }
}
