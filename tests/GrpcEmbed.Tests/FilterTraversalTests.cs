using Grpc.Net.Client;
using System.Collections.Concurrent;
using GrpcEmbed.Tests.Generated;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public class FilterTraversalTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("authorization")]
    [InlineData("action")]
    [InlineData("result")]
    [InlineData("exception")]
    [InlineData("arguments")]
    public async Task Contexts_share_only_the_current_requests_filter_list(string mode)
    {
        var recording = new ContextLists(mode);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<MvcOptions>(options => options.Filters.Add(recording))));
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var client = new Users.UsersClient(channel);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            Assert.Equal(mode == "arguments" ? 43 : 42, (await client.GetAsync(new Users_Get_Request { Id = 42 })).Id)));
        Assert.Equal(12, recording.Lists.Count);
        Assert.Equal(12, recording.Lists.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(recording.Lists, list => Assert.Single(list.OfType<Marker>()));
    }

    private sealed class Marker : IFilterMetadata { }
    private sealed class ContextLists(string mode) : IAuthorizationFilter, IAsyncResourceFilter, IAsyncActionFilter, IAsyncResultFilter, IExceptionFilter
    {
        private readonly object _key = new();
        internal readonly ConcurrentBag<IList<IFilterMetadata>> Lists = new();
        private void Check(FilterContext context) => Assert.Same(context.HttpContext.Items[_key], context.Filters);
        private static IActionResult Response() => new OkObjectResult(new GrpcEmbed.Sample.Server.UserDto { Id = 42 });
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            context.HttpContext.Items.Add(_key, context.Filters);
            Lists.Add(context.Filters);
            context.Filters.Add(new Marker());
            if (mode == "authorization") context.Result = Response();
        }
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            Check(context); Check(await next());
        }
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            Check(context);
            if (mode == "exception") throw new InvalidOperationException("synthetic");
            if (mode == "action") { context.Result = Response(); return; }
            if (mode == "arguments") context.ActionArguments["id"] = 43;
            Check(await next());
        }
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            Check(context);
            if (mode == "result") { context.Cancel = true; return; }
            Check(await next());
        }
        public void OnException(ExceptionContext context)
        {
            Check(context);
            context.Result = Response(); context.ExceptionHandled = true;
        }
    }

    [Fact]
    public async Task Unrelated_categories_do_not_skip_or_reorder_applicable_filters()
    {
        var recording = new Recording();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<MvcOptions>(options =>
            {
                options.Filters.Add(recording);
                for (var i = 0; i < 64; i++) options.Filters.Add(new ExceptionOnly());
            })));
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var result = await new Users.UsersClient(channel).GetAsync(new Users_Get_Request { Id = 42 });
        Assert.Equal(42, result.Id);
        Assert.Equal(new[] { "authorize", "resource-before", "action-before", "action-after", "result-before", "result-after", "resource-after" }, recording.Events);
    }

    private sealed class ExceptionOnly : IExceptionFilter
    {
        public void OnException(ExceptionContext context) => throw new InvalidOperationException("No exception expected.");
    }

    private sealed class Recording : IAuthorizationFilter, IAsyncResourceFilter, IAsyncActionFilter, IAsyncResultFilter
    {
        internal readonly List<string> Events = new();
        public void OnAuthorization(AuthorizationFilterContext context) => Events.Add("authorize");
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            Events.Add("resource-before"); await next(); Events.Add("resource-after");
        }
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            Events.Add("action-before"); await next(); Events.Add("action-after");
        }
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            Events.Add("result-before"); await next(); Events.Add("result-after");
        }
    }
}
