using GrpcEmbed.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ServerConfigurationTests
{
    [Theory]
    [InlineData(false, false, GrpcEmbedExportMode.ExplicitOnly)]
    [InlineData(true, false, GrpcEmbedExportMode.ExplicitOnly)]
    [InlineData(true, true, GrpcEmbedExportMode.All)]
    public void Server_flags_are_bound_independently(bool enabled, bool all, GrpcEmbedExportMode expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GrpcEmbed:Server:Enabled"] = enabled.ToString(),
            ["GrpcEmbed:Server:ExposeAll"] = all.ToString(),
            ["GrpcEmbed:Clients:Enabled"] = (!enabled).ToString(),
        }).Build();
        var services = new ServiceCollection();
        services.AddGrpcEmbed(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value;
        Assert.Equal(enabled, options.ServerEnabled);
        Assert.Equal(expected, options.ExportMode);
    }

    [Fact]
    public void Old_enabled_key_does_not_enable_server()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GrpcEmbed:Enabled"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddGrpcEmbed(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<IOptions<GrpcEmbedOptions>>().Value.ServerEnabled);
    }
}
