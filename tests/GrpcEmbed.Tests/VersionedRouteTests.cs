using GrpcEmbed.Client;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class VersionedRouteTests
{
    [Fact]
    public async Task Api_prefix_version_and_parameter_alias_are_resolved_from_base_address()
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(RouteProbeController).Assembly);
            services.PostConfigure<GrpcEmbedOptions>(options => options.Routing.Mode = GrpcEmbedRoutingMode.Rest);
        }));
        using var http = server.CreateClient();
        http.BaseAddress = new Uri("http://localhost/api/v1");
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IProbe>(options =>
        {
            options.Address = http.BaseAddress;
            options.HttpClient = http;
            options.ServiceName = "RouteProbe";
            options.Routing.Mode = GrpcEmbedRoutingMode.Rest;
            options.Contract.Fetch = GrpcEmbedContractFetch.FirstCall;
        });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IProbe>();
        Assert.Equal("get-17", await client.ReadAsync(17));
        Assert.Equal("delete-17", await client.DeleteAsync(17));
    }

    public interface IProbe
    {
        Task<string> ReadAsync(int id);
        Task<string> DeleteAsync(int id);
    }
}

[ApiController]
[Route("api/v{version:range(1,1)}/probe")]
public sealed class RouteProbeController : ControllerBase
{
    [HttpGet("{clientId:int}")]
    public Task<string> Read([FromRoute(Name = "clientId")] int id) => Task.FromResult("get-" + id);
    [HttpDelete("{clientId:int}")]
    public Task<string> Delete([FromRoute(Name = "clientId")] int id) => Task.FromResult("delete-" + id);
}
