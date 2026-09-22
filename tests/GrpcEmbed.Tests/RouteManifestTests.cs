using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RouteManifestTests
{
    [Fact]
    public async Task Manifest_describes_route_and_original_verb_without_changing_rpc_identity()
    {
        using var server = new WebApplicationFactory<Program>();
        using var http = server.CreateClient();
        using var json = JsonDocument.Parse(await http.GetStringAsync("/_grpcembed/schema.json"));
        var methods = json.RootElement.GetProperty("services").GetProperty("Users").GetProperty("methods");
        var get = methods.GetProperty("Get").GetProperty("route");
        Assert.Equal("GrpcEmbed.Users/Get", get.GetProperty("operationId").GetString());
        Assert.Equal("api/users/{id:int}", get.GetProperty("template").GetString());
        Assert.Equal("GET", Assert.Single(get.GetProperty("httpMethods").EnumerateArray()).GetString());
        var create = methods.GetProperty("Create").GetProperty("route");
        Assert.Equal("api/users", create.GetProperty("template").GetString());
        Assert.Equal("POST", Assert.Single(create.GetProperty("httpMethods").EnumerateArray()).GetString());
    }
}
