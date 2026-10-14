using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RecursiveCollectionTests
{
    [Fact]
    public void Recursive_collection_serializer_builds_and_preserves_presence_at_each_level()
    {
        var source = new Node { Name = "root", Children = new() {
            null, new Node { Name = "null", Children = null },
            new Node { Name = "empty", Children = new() },
            new Node { Name = "nested", Children = new() { new Node { Name = "leaf", Children = new() } } }
        }};
        var result = RoundTrip(source);
        Assert.Equal("root", result.Name); Assert.Equal(4, result.Children!.Count);
        Assert.Null(result.Children[0]); Assert.Null(result.Children[1]!.Children);
        Assert.Empty(result.Children[2]!.Children!);
        Assert.Equal("leaf", Assert.Single(result.Children[3]!.Children!)!.Name);
        Assert.Empty(result.Children[3]!.Children![0]!.Children!);
    }

    [Fact]
    public void Mutually_recursive_array_and_interface_shapes_are_supported()
    {
        var source = new Parent { Children = new[] { new Child { Parents = new List<Parent> { new Parent { Children = Array.Empty<Child>() } } } } };
        var actual = RoundTrip(source);
        Assert.Empty(Assert.Single(Assert.Single(actual.Children!).Parents!).Children!);
        Assert.Null(RoundTrip(new Parent { Children = null }).Children);
    }

    [Fact]
    public void Actual_object_cycles_fail_with_a_protocol_error_not_a_stack_overflow()
    {
        var source = new Node { Name = "cycle", Children = new() }; source.Children.Add(source);
        GrpcEmbedRuntimeModel.Configure(typeof(Node));
        using var stream = new MemoryStream();
        Assert.Throws<ProtoBuf.ProtoException>(() => GrpcEmbedRuntimeModel.Model.Serialize(stream, source));
    }

    private static T RoundTrip<T>(T value)
    {
        GrpcEmbedRuntimeModel.Configure(typeof(T));
        using var stream = new MemoryStream(); GrpcEmbedRuntimeModel.Model.Serialize(stream, value);
        stream.Position = 0; return GrpcEmbedRuntimeModel.Model.Deserialize<T>(stream);
    }

    public sealed class Node { public string? Name { get; set; } public List<Node?>? Children { get; set; } }
    public sealed class Parent { public Child[]? Children { get; set; } }
    public sealed class Child { public IReadOnlyList<Parent>? Parents { get; set; } }
}
