using Grpc.Core;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class EmptyCollectionResponseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Empty_and_nonempty_list_match_rest_for_collection_interfaces(int count)
    {
        using var server = new WebApplicationFactory<Program>();
        using var http = server.CreateClient();
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<ICollectionClient>(options => { options.Address = http.BaseAddress!; options.HttpClient = http; options.ServiceName = "Users"; });
        services.AddGrpcEmbedClient<IArrayClient>(options => { options.Address = http.BaseAddress!; options.HttpClient = http; options.ServiceName = "Users"; });
        services.AddGrpcEmbedClient<IReadOnlyClient>(options => { options.Address = http.BaseAddress!; options.HttpClient = http; options.ServiceName = "Users"; });
        using var provider = services.BuildServiceProvider();
        var collection = await provider.GetRequiredService<ICollectionClient>().ListAsync(count);
        var array = await provider.GetRequiredService<IArrayClient>().ListAsync(count);
        var readOnly = await provider.GetRequiredService<IReadOnlyClient>().ListAsync(count);
        Assert.NotNull(collection); Assert.NotNull(array); Assert.NotNull(readOnly);
        Assert.Equal(count, collection.Count); Assert.Equal(count, array.Length); Assert.Equal(count, readOnly.Count);
        Assert.NotSame(collection, await provider.GetRequiredService<ICollectionClient>().ListAsync(count));
    }

    [Fact]
    public async Task True_null_collection_remains_an_rpc_error_and_is_not_replaced_by_empty()
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(EmptyResponsesController).Assembly)));
        using var http = server.CreateClient();
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IEmptyResponses>(options => { options.Address = http.BaseAddress!; options.HttpClient = http; options.ServiceName = "EmptyResponses"; });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IEmptyResponses>();
        Assert.Empty(await client.EmptyAsync());
        Assert.Equal(string.Empty, await client.TextAsync());
        Assert.Empty(await client.BytesAsync());
        var error = await Assert.ThrowsAsync<RpcException>(() => client.NullAsync());
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    public interface ICollectionClient { Task<ICollection<UserDto>> ListAsync(int count); }
    public interface IArrayClient { Task<UserDto[]> ListAsync(int count); }
    public interface IReadOnlyClient { Task<IReadOnlyList<UserDto>> ListAsync(int count); }
    public interface IEmptyResponses
    {
        Task<List<int>> EmptyAsync(); Task<List<int>?> NullAsync();
        Task<string> TextAsync(); Task<byte[]> BytesAsync();
    }
}

[ApiController]
[Route("api/empty-responses")]
public sealed class EmptyResponsesController : ControllerBase
{
    [HttpGet("empty")] public new Task<List<int>> Empty() => Task.FromResult(new List<int>());
    [HttpGet("null")] public Task<List<int>?> Null() => Task.FromResult<List<int>?>(null);
    [HttpGet("text")] public Task<string> Text() => Task.FromResult(string.Empty);
    [HttpGet("bytes")] public Task<byte[]> Bytes() => Task.FromResult(Array.Empty<byte>());
}
