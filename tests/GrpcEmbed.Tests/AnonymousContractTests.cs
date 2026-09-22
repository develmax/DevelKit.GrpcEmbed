using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class AnonymousContractTests
{
    [Theory]
    [InlineData("/_grpcembed/schema.json")]
    [InlineData("/_grpcembed/schema.proto")]
    [InlineData("/_grpcembed/descriptor.pb")]
    [InlineData("/_grpcembed")]
    public async Task Anonymous_contract_overrides_fallback_but_not_business_authorization(string path)
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddAuthorization(options =>
                    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                services.PostConfigure<GrpcEmbedOptions>(options => options.Contract.AllowAnonymous = true);
            }));
        using var http = server.CreateClient();
        using var schema = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        using var business = await http.GetAsync("/api/users/123");
        Assert.Equal(HttpStatusCode.Unauthorized, business.StatusCode);
    }

    [Fact]
    public async Task Contract_remains_protected_unless_anonymous_access_is_explicit()
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddAuthorization(options =>
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())));
        using var http = server.CreateClient();
        using var response = await http.GetAsync("/_grpcembed/schema.json");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
