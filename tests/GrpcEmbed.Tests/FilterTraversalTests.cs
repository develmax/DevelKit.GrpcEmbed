using Grpc.Net.Client;
using GrpcEmbed.Tests.Generated;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public class FilterTraversalTests
{
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
