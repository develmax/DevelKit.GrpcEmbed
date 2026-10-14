using Xunit;

namespace GrpcEmbed.Tests;

public sealed class NestedCollectionPresenceTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Nested_null_empty_and_populated_collections_are_distinct(int kind)
    {
        var source = new Holder { Child = new Child {
            Items = kind == 0 ? null : kind == 1 ? new() : new() { new Leaf { Id = 42 }, null },
            Array = kind == 0 ? null : kind == 1 ? Array.Empty<int?>() : new int?[] { null, 0, 42 },
            Values = kind == 0 ? null : kind == 1 ? new() : new() { null, 0, 42 },
            Names = kind == 0 ? null : kind == 1 ? new() : new() { null, "", "synthetic" },
            Ids = kind == 0 ? null : kind == 1 ? new() : new() { null, Guid.Empty },
        }};
        var actual = RoundTrip(source);
        Assert.NotNull(actual.Child);
        Assert.Equal(source.Child.Values, actual.Child.Values);
        Assert.Equal(source.Child.Array, actual.Child.Array);
        Assert.Equal(source.Child.Names, actual.Child.Names);
        Assert.Equal(source.Child.Ids, actual.Child.Ids);
        if (kind == 0) Assert.Null(actual.Child.Items);
        else if (kind == 1) Assert.Empty(actual.Child.Items!);
        else { Assert.Equal(42, actual.Child.Items![0]!.Id); Assert.Null(actual.Child.Items[1]); }
        Assert.Null(RoundTrip(new Holder { Child = null }).Child);
        Assert.Null(RoundTrip(new Holder { Child = new Child { Items = null } }).Child!.Items);
    }

    [Fact]
    public void Collection_presence_changes_bytes_and_does_not_share_mutable_empty_instances()
    {
        var empty = new Holder { Child = new Child { Items = new() } };
        var missing = new Holder { Child = new Child { Items = null } };
        Assert.NotEqual(Convert.ToHexString(Serialize(empty)), Convert.ToHexString(Serialize(missing)));
        var first = RoundTrip(empty); var second = RoundTrip(empty);
        Assert.NotSame(first.Child!.Items, second.Child!.Items);
        first.Child.Items!.Add(new Leaf { Id = 1 });
        Assert.Empty(second.Child.Items!);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Collection_interfaces_preserve_presence(int kind)
    {
        List<int?>? values = kind == 0 ? null : kind == 1 ? new() : new() { null, 0, 42 };
        var source = new Interfaces { ReadOnly = values, Collection = values, Enumerable = values, Set = values is null ? null : new HashSet<int?>(values) };
        GrpcEmbedRuntimeModel.Configure(typeof(Interfaces));
        using var stream = new MemoryStream(); GrpcEmbedRuntimeModel.Model.Serialize(stream, source); stream.Position = 0;
        var actual = (Interfaces)GrpcEmbedRuntimeModel.Model.Deserialize(stream, null, typeof(Interfaces))!;
        Assert.Equal(source.ReadOnly, actual.ReadOnly); Assert.Equal(source.Collection, actual.Collection);
        Assert.Equal(source.Enumerable, actual.Enumerable); Assert.Equal(source.Set, actual.Set);
    }

    public sealed class Interfaces
    {
        public IReadOnlyList<int?>? ReadOnly { get; set; }
        public ICollection<int?>? Collection { get; set; }
        public IEnumerable<int?>? Enumerable { get; set; }
        public ISet<int?>? Set { get; set; }
    }

    [Theory]
    [InlineData(typeof(Dictionary<string, int?>))]
    [InlineData(typeof(IDictionary<string, string?>))]
    [InlineData(typeof(IReadOnlyDictionary<string, Leaf?>))]
    [InlineData(typeof(MapHolder))]
    public void Maps_fail_before_model_registration_instead_of_losing_value_presence(Type type)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<NotSupportedException>(() => GrpcEmbedRuntimeModel.Configure(type));
            Assert.Contains("dictionary/map", error.Message, StringComparison.Ordinal);
        }
    }

    public sealed class MapHolder { public Dictionary<string, int?>? Values { get; set; } }

    private static byte[] Serialize(Holder value)
    {
        GrpcEmbedRuntimeModel.Configure(typeof(Holder));
        using var stream = new MemoryStream();
        GrpcEmbedRuntimeModel.Model.Serialize(stream, value); return stream.ToArray();
    }
    private static Holder RoundTrip(Holder value)
    {
        using var stream = new MemoryStream(Serialize(value));
        return (Holder)GrpcEmbedRuntimeModel.Model.Deserialize(stream, null, typeof(Holder))!;
    }
    public sealed class Holder { public Child? Child { get; set; } = new(); }
    public sealed class Child
    {
        public List<Leaf?>? Items { get; set; } = new();
        public int?[]? Array { get; set; } = System.Array.Empty<int?>();
        public List<int?>? Values { get; set; } = new();
        public List<string?>? Names { get; set; } = new();
        public List<Guid?>? Ids { get; set; } = new();
    }
    public sealed class Leaf { public int Id { get; set; } }
}
