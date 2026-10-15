using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using ClrFixture = GrpcEmbed.Sample.Server.WireFixture;
using StandardFixture = GrpcEmbed.Tests.Generated.WireFixture;

namespace GrpcEmbed.Tests;

public sealed class WireCompatibilityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public WireCompatibilityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Shape_hash_distinguishes_the_legacy_wire_profile()
    {
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("grpcembed-shape-v1;System.Guid"))).ToLowerInvariant();
        Assert.NotEqual(legacy, GrpcEmbedContractShape.Hash(typeof(Guid)));
        var v3 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("grpcembed-shape-v2-level300;System.Guid"))).ToLowerInvariant();
        Assert.NotEqual(v3, GrpcEmbedContractShape.Hash(typeof(Guid)));
    }

    [Fact]
    public void Special_clr_types_match_standard_protobuf_in_both_directions()
    {
        var expected = new ClrFixture
        {
            Id = Guid.Parse("f607aa13-2adb-424f-b13a-82e62b70e64b"),
            OptionalId = Guid.Parse("19b94235-9b1b-46e2-8330-cb0be99de075"),
            Amount = 1234567890.123456789m,
            At = new DateTime(638915678901234567, DateTimeKind.Utc),
            Elapsed = TimeSpan.FromTicks(-987654321),
            OffsetAt = new DateTimeOffset(2026, 10, 12, 3, 4, 5, TimeSpan.FromHours(3)),
            States = new() { GrpcEmbed.Sample.Server.WireState.Unknown, GrpcEmbed.Sample.Server.WireState.Active },
            Values = new() { 0, -7, 42 },
            Ids = new() { Guid.Parse("e25161a0-8685-f111-8401-0050569758da") }
        };
        GrpcEmbedRuntimeModel.Configure(typeof(ClrFixture));
        using var stream = new MemoryStream();
        GrpcEmbedRuntimeModel.Model.Serialize(stream, expected);
        var standard = StandardFixture.Parser.ParseFrom(stream.ToArray());
        Assert.Equal(expected.Id.ToString("D"), standard.Id);
        Assert.Equal(expected.OptionalId.Value.ToString("D"), standard.OptionalId);
        Assert.Equal(expected.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), standard.Amount);
        Assert.Equal(expected.At, standard.At.ToDateTime());
        Assert.Equal(expected.Elapsed, standard.Elapsed.ToTimeSpan());
        Assert.Equal(expected.OffsetAt.ToString("O"), standard.OffsetAt);
        Assert.Equal(new[] { 0, 1 }, standard.States.Items.Select(x => (int)x.Value));
        Assert.Equal(new[] { 0, -7, 42 }, standard.Values.Items.Select(x => x.Value));
        Assert.Equal(expected.Ids[0]!.Value.ToString("D"), Assert.Single(standard.Ids.Items).Value);
        using var input = new MemoryStream(standard.ToByteArray());
        var actual = (ClrFixture)GrpcEmbedRuntimeModel.Model.Deserialize(input, null, typeof(ClrFixture))!;
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.OptionalId, actual.OptionalId);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.At, actual.At);
        Assert.Equal(expected.Elapsed, actual.Elapsed);
        Assert.Equal(expected.OffsetAt, actual.OffsetAt);
        Assert.Equal(expected.States, actual.States);
        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal(expected.Ids, actual.Ids);
    }

    [Fact]
    public async Task Standard_generated_grpc_client_can_echo_special_types()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var client = new Generated.Wire.WireClient(channel);
        var request = new StandardFixture
        {
            Id = "f607aa13-2adb-424f-b13a-82e62b70e64b",
            OptionalId = "19b94235-9b1b-46e2-8330-cb0be99de075",
            Amount = "1234567890.123456789",
            At = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Elapsed = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromTicks(123456789)),
            OffsetAt = "2026-10-12T03:04:05.0000000+03:00"
        };
        request.States = new Generated.WireStates(); request.States.Items.Add(new Generated.WireStateItem { Value = Generated.WireState.Active });
        request.Values = new Generated.WireValues(); request.Values.Items.Add(new Generated.WireValueItem { Value = 42 });
        request.Ids = new Generated.WireIds(); request.Ids.Items.Add(new Generated.WireIdItem { Value = request.Id });
        var result = await client.EchoAsync(new Generated.Wire_Echo_Request { Request = request });
        Assert.Equal(request, result);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Standard_client_preserves_collection_and_item_presence_on_request_and_response(int kind)
    {
        var request = new StandardFixture();
        if (kind != 0)
        {
            request.States = new Generated.WireStates(); request.Values = new Generated.WireValues(); request.Ids = new Generated.WireIds();
            if (kind == 2)
            {
                request.States.Items.Add(new Generated.WireStateItem());
                request.States.Items.Add(new Generated.WireStateItem { Value = Generated.WireState.Unknown });
                request.Values.Items.Add(new Generated.WireValueItem());
                request.Values.Items.Add(new Generated.WireValueItem { Value = 0 });
                request.Ids.Items.Add(new Generated.WireIdItem());
                // protobuf-net Level300 encodes Guid.Empty as an explicitly present
                // empty string; the item wrapper keeps it distinct from null.
                request.Ids.Items.Add(new Generated.WireIdItem { Value = "" });
            }
        }
        GrpcEmbedRuntimeModel.Configure(typeof(ClrFixture));
        using var input = new MemoryStream(request.ToByteArray());
        var dto = (ClrFixture)GrpcEmbedRuntimeModel.Model.Deserialize(input, null, typeof(ClrFixture))!;
        if (kind == 0) { Assert.Null(dto.States); Assert.Null(dto.Values); Assert.Null(dto.Ids); }
        else if (kind == 1) { Assert.Empty(dto.States); Assert.Empty(dto.Values); Assert.Empty(dto.Ids); }
        else
        {
            Assert.Equal(new GrpcEmbed.Sample.Server.WireState?[] { null, GrpcEmbed.Sample.Server.WireState.Unknown }, dto.States);
            Assert.Equal(new int?[] { null, 0 }, dto.Values);
            Assert.Equal(new Guid?[] { null, Guid.Empty }, dto.Ids);
        }
        using var output = new MemoryStream(); GrpcEmbedRuntimeModel.Model.Serialize(output, dto);
        var standard = StandardFixture.Parser.ParseFrom(output.ToArray());
        Assert.Equal(request.States, standard.States); Assert.Equal(request.Values, standard.Values); Assert.Equal(request.Ids, standard.Ids);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var result = await new Generated.Wire.WireClient(channel).EchoAsync(new Generated.Wire_Echo_Request { Request = request });
        Assert.Equal(request.States, result.States); Assert.Equal(request.Values, result.Values); Assert.Equal(request.Ids, result.Ids);
    }

    [Fact]
    public async Task Nullable_collection_schema_and_reflection_descriptors_resolve()
    {
        var http = _factory.CreateClient();
        var proto = await http.GetStringAsync("/_grpcembed/schema.proto");
        Assert.DoesNotContain("repeated Nullable", proto, StringComparison.Ordinal);
        Assert.Contains("GrpcEmbedCollection_WireFixture_7 states = 7;", proto, StringComparison.Ordinal);
        Assert.Contains("GrpcEmbedCollection_WireFixture_8 values = 8;", proto, StringComparison.Ordinal);
        Assert.Contains("GrpcEmbedCollection_WireFixture_9 ids = 9;", proto, StringComparison.Ordinal);
        var set = FileDescriptorSet.Parser.ParseFrom(await http.GetByteArrayAsync("/_grpcembed/descriptor.pb"));
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(file => file.ToByteString()));
        var descriptor = Assert.Single(files, file => file.Package == "GrpcEmbed");
        var fixture = Assert.Single(descriptor.MessageTypes, type => type.Name == "WireFixture");
        var state = fixture.FindFieldByNumber(7).MessageType.FindFieldByNumber(1).MessageType.FindFieldByNumber(1);
        Assert.Equal(FieldType.Enum, state.FieldType);
        Assert.Equal("WireState", state.EnumType.Name);
        Assert.True(state.HasPresence);
        Assert.Equal(FieldType.Int32, fixture.FindFieldByNumber(8).MessageType.FindFieldByNumber(1).MessageType.FindFieldByNumber(1).FieldType);
        Assert.Equal(FieldType.String, fixture.FindFieldByNumber(9).MessageType.FindFieldByNumber(1).MessageType.FindFieldByNumber(1).FieldType);
    }
}
