using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RuntimeModelTests
{
    [Fact]
    public void Supported_clr_model_round_trips_losslessly_without_attributes()
    {
        var expected = new ModelFixture
        {
            Flag = true, Signed = -17, Unsigned = 42, Ratio = 1.25, Amount = 1234567890.123456789m,
            Id = Guid.Parse("f607aa13-2adb-424f-b13a-82e62b70e64b"),
            At = new DateTime(638915678901234567, DateTimeKind.Utc),
            OffsetAt = new DateTimeOffset(2026, 8, 22, 3, 4, 5, 678, TimeSpan.FromHours(3)),
            Elapsed = TimeSpan.FromTicks(987654321), State = FixtureState.Active,
            Optional = 7, Bytes = new byte[] { 1, 2, 3 }, Values = new List<int> { 4, 5 },
            Nested = new NestedFixture { Name = "nested" }
        };
        GrpcEmbedRuntimeModel.Configure(typeof(ModelFixture));
        using var stream = new MemoryStream();
        GrpcEmbedRuntimeModel.Model.Serialize(stream, expected);
        stream.Position = 0;
        var actual = (ModelFixture)GrpcEmbedRuntimeModel.Model.Deserialize(stream, null, typeof(ModelFixture))!;
        Assert.Equal(expected.Flag, actual.Flag); Assert.Equal(expected.Signed, actual.Signed); Assert.Equal(expected.Unsigned, actual.Unsigned);
        Assert.Equal(expected.Ratio, actual.Ratio); Assert.Equal(expected.Amount, actual.Amount); Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.At.Ticks, actual.At.Ticks); Assert.Equal(expected.OffsetAt, actual.OffsetAt);
        Assert.Equal(expected.Elapsed, actual.Elapsed); Assert.Equal(expected.State, actual.State); Assert.Equal(expected.Optional, actual.Optional);
        Assert.Equal(expected.Bytes, actual.Bytes); Assert.Equal(expected.Values, actual.Values); Assert.Equal(expected.Nested.Name, actual.Nested.Name);
    }

    public sealed class ModelFixture
    {
        public bool Flag { get; set; } public long Signed { get; set; } public ulong Unsigned { get; set; }
        public double Ratio { get; set; } public decimal Amount { get; set; } public Guid Id { get; set; }
        public DateTime At { get; set; } public DateTimeOffset OffsetAt { get; set; } public TimeSpan Elapsed { get; set; }
        public FixtureState State { get; set; } public int? Optional { get; set; } public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public List<int> Values { get; set; } = new(); public NestedFixture Nested { get; set; } = new();
    }
    public sealed class NestedFixture { public string Name { get; set; } = ""; }
    public enum FixtureState { Unknown, Active }
}
