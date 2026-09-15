using Xunit;
using GrpcEmbed.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GrpcEmbed.Tests;

public sealed class PublicApiTests
{
    [Fact] public void Defaults_export_all_and_are_non_strict()
    {
        var options = new GrpcEmbedOptions();
        Assert.Equal(GrpcEmbedExportMode.All, options.ExportMode);
        Assert.False(options.ThrowOnUnsupportedAction);
    }

    [Fact] public void Grpc_name_rejects_empty_names() => Assert.Throws<ArgumentException>(() => new GrpcNameAttribute(" "));

    [Fact]
    public void Field_numbers_are_stable_when_reflection_order_changes()
    {
        var first = GrpcEmbedFieldNumbers.Assign(new[] { "Id", "Name" });
        var reordered = GrpcEmbedFieldNumbers.Assign(new[] { "Email", "Name", "Id" });
        Assert.Equal(first["Id"], reordered["Id"]);
        Assert.Equal(first["Name"], reordered["Name"]);
        Assert.Equal(3, reordered.Values.Distinct().Count());
    }

    [Fact]
    public void Explicit_field_numbers_support_safe_property_and_parameter_renames()
    {
        var property = typeof(ExplicitContract).GetProperties();
        var parameter = typeof(PublicApiTests).GetMethod(nameof(ExplicitParameter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetParameters();
        Assert.Equal(7, GrpcEmbedFieldNumbers.Assign(property)[nameof(ExplicitContract.Renamed)]);
        Assert.Equal(9, GrpcEmbedFieldNumbers.Assign(parameter)["renamed"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrpcFieldNumberAttribute(19_001));
        Assert.Equal(new[] { 7, 8 }, new GrpcReservedFieldAttribute(8, 7, 7).Numbers);
    }

    private sealed class ExplicitContract { [GrpcFieldNumber(7)] public int Renamed { get; set; } }
    private static void ExplicitParameter([GrpcFieldNumber(9)] int renamed) { }

    [Fact]
    public void Client_contract_validation_rejects_overloads_and_misplaced_cancellation()
    {
        var overloaded = new ServiceCollection();
        overloaded.AddGrpcEmbedClient<IOverloadedApi>(options => options.Address = new Uri("http://localhost"));
        using var overloadedProvider = overloaded.BuildServiceProvider();
        Assert.Throws<NotSupportedException>(() => overloadedProvider.GetRequiredService<IOverloadedApi>());

        var misplaced = new ServiceCollection();
        misplaced.AddGrpcEmbedClient<IMisplacedCancellationApi>(options => options.Address = new Uri("http://localhost"));
        using var misplacedProvider = misplaced.BuildServiceProvider();
        Assert.Throws<NotSupportedException>(() => misplacedProvider.GetRequiredService<IMisplacedCancellationApi>());
    }

    public interface IOverloadedApi { Task<string> Get(int id); Task<string> Get(string id); }
    public interface IMisplacedCancellationApi { Task<string> Get(CancellationToken cancellationToken, int id); }

    [Fact]
    public void Multiple_client_contracts_can_use_independent_channels()
    {
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IFirstApi>(options => options.Address = new Uri("http://first.invalid"));
        services.AddGrpcEmbedClient<ISecondApi>(options => options.Address = new Uri("http://second.invalid"));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IFirstApi>());
        Assert.NotNull(provider.GetRequiredService<ISecondApi>());
    }

    public interface IFirstApi { Task<string> Get(int id); }
    public interface ISecondApi { Task<string> Get(int id); }
}
