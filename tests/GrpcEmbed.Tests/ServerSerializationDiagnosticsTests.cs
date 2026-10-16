using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Grpc.Core;
using GrpcEmbed;
using GrpcEmbed.AspNetCore;
using ProtoBuf;
using Xunit;

[CollectionDefinition("Server serialization diagnostics", DisableParallelization = true)]
public sealed class ServerSerializationDiagnosticsCollection;

[Collection("Server serialization diagnostics")]
public sealed class ServerSerializationDiagnosticsTests
{
    [Fact]
    public void Disabled_diagnostics_add_no_serialization_allocations()
    {
        Assert.False(GrpcEmbedServerEventSource.Log.IsEnabled());
        var value = new TimingDto { Id = 42, Name = "Synthetic" };
        var baseline = GrpcEmbedMethodProvider.Marshaller<TimingDto>().ContextualSerializer;
        var observed = GrpcEmbedMethodProvider.Marshaller<TimingDto>(response: true).ContextualSerializer;
        var context = new BufferContext();
        for (var i = 0; i < 2000; i++)
        {
            context.Reset(); baseline(value, context);
            context.Reset(); observed(value, context);
        }
        long Allocate(Action<TimingDto, Grpc.Core.SerializationContext> serialize)
        {
            var started = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) { context.Reset(); serialize(value, context); }
            return GC.GetAllocatedBytesForCurrentThread() - started;
        }
        Assert.Equal(Allocate(baseline), Allocate(observed));
    }

    [Fact]
    public void Opt_in_timing_preserves_wire_bytes_and_reports_only_numbers()
    {
        var dto = new TimingDto { Id = 42, Name = "Synthetic-кириллица😀" };
        var baseline = new BufferContext();
        GrpcEmbedMethodProvider.Marshaller<TimingDto>().ContextualSerializer(dto, baseline);
        using var events = new SerializationListener();
        var response = new BufferContext();
        GrpcEmbedMethodProvider.Marshaller<TimingDto>(response: true).ContextualSerializer(dto, response);
        Assert.Equal(baseline.Bytes, response.Bytes);
        Assert.True(response.Completed);
        var payload = Assert.Single(events.Events);
        Assert.Equal(3, payload.Length);
        Assert.All(payload, value => Assert.IsType<long>(value));
        Assert.True((long)payload[0]! > 0);
        Assert.True((long)payload[1]! >= 0);
        Assert.Equal(1L, payload[2]);
    }

    [Fact]
    public void Request_marshaller_does_not_report_response_serialization()
    {
        using var events = new SerializationListener();
        GrpcEmbedMethodProvider.Marshaller<TimingDto>().ContextualSerializer(new TimingDto(), new BufferContext());
        Assert.Empty(events.Events);
    }

    [Fact]
    public void Serializer_failure_keeps_the_original_exception_and_reports_failure()
    {
        using var events = new SerializationListener();
        var failure = new InvalidOperationException("Synthetic writer failure");
        var context = new BufferContext(failure);
        var actual = Assert.Throws<InvalidOperationException>(() =>
            GrpcEmbedMethodProvider.Marshaller<TimingDto>(response: true).ContextualSerializer(new TimingDto(), context));
        Assert.Same(failure, actual);
        Assert.False(context.Completed);
        Assert.Equal(0L, Assert.Single(events.Events)[2]);
    }

    [Fact]
    public void Failing_diagnostic_observer_does_not_break_the_response()
    {
        using var events = new SerializationListener(throwOnEvent: true);
        var context = new BufferContext();
        GrpcEmbedMethodProvider.Marshaller<TimingDto>(response: true).ContextualSerializer(new TimingDto(), context);
        Assert.True(context.Completed);
    }

    [Fact]
    public void Disposing_the_listener_disables_the_provider()
    {
        using (var events = new SerializationListener())
        {
            Assert.True(GrpcEmbedServerEventSource.Log.IsEnabled(EventLevel.Verbose, GrpcEmbedServerEventSource.Keywords.Serialization));
        }
        Assert.False(GrpcEmbedServerEventSource.Log.IsEnabled(EventLevel.Verbose, GrpcEmbedServerEventSource.Keywords.Serialization));
    }

    [ProtoContract]
    public sealed class TimingDto
    {
        [ProtoMember(1)] public int Id { get; set; }
        [ProtoMember(2)] public string? Name { get; set; }
    }

    private sealed class BufferContext(Exception? failure = null) : Grpc.Core.SerializationContext
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public byte[] Bytes => _buffer.WrittenSpan.ToArray();
        public bool Completed { get; private set; }
        public void Reset() { _buffer.Clear(); Completed = false; }
        public override IBufferWriter<byte> GetBufferWriter() => failure is null ? _buffer : throw failure;
        public override void Complete() => Completed = true;
        public override void Complete(byte[] payload) => throw new NotSupportedException();
    }

    private sealed class SerializationListener(bool throwOnEvent = false) : EventListener
    {
        public ConcurrentQueue<object?[]> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "DevelKit-GrpcEmbed-Server")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventId != 1) return;
            if (throwOnEvent) throw new InvalidOperationException("Synthetic diagnostic observer failure");
            Events.Enqueue(data.Payload!.ToArray());
        }
    }
}
