using System.Buffers;
using Grpc.Core;
using GrpcEmbed;
using GrpcEmbed.Client;
using Xunit;

namespace GrpcEmbed.Tests;

public class RequestSerializationTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(300000000, "")]
    [InlineData(int.MaxValue, "Привет")]
    public void KnownLengthPreservesPayload(int id, string? text)
    {
        var value = new ScalarRequest { Id = id, Text = text };
        GrpcEmbedRuntimeModel.Configure(typeof(ScalarRequest));
        var baseline = new Context();
        RequestSerialization.Write(value, baseline, false);
        var measured = new Context();
        RequestSerialization.Write(value, measured, true);
        Assert.Equal(baseline.Writer.WrittenSpan.ToArray(), measured.Writer.WrittenSpan.ToArray());
        Assert.Equal(measured.Writer.WrittenCount, measured.Length);
        Assert.True(measured.LengthSetBeforeWriter);
        Assert.True(measured.Completed);
        Assert.Null(baseline.Length);
    }

    [Fact]
    public void ComplexArgumentsAreNotMeasured()
    {
        Assert.False(RequestSerialization.IsScalar(typeof(ScalarRequest)));
        Assert.False(RequestSerialization.IsScalar(typeof(IEnumerable<int>)));
        Assert.False(RequestSerialization.IsScalar(typeof(byte[])));
        Assert.True(RequestSerialization.IsScalar(typeof(int?)));
        Assert.True(RequestSerialization.IsScalar(typeof(Guid)));
    }

    public class ScalarRequest { public int Id { get; set; } public string? Text { get; set; } }

    private sealed class Context : SerializationContext
    {
        internal readonly ArrayBufferWriter<byte> Writer = new();
        internal int? Length;
        internal bool LengthSetBeforeWriter, Completed;
        private bool writerRequested;
        public override void SetPayloadLength(int payloadLength)
        {
            LengthSetBeforeWriter = !writerRequested;
            Length = payloadLength;
        }
        public override IBufferWriter<byte> GetBufferWriter() { writerRequested = true; return Writer; }
        public override void Complete() => Completed = true;
        public override void Complete(byte[] payload) => throw new NotSupportedException();
    }
}
