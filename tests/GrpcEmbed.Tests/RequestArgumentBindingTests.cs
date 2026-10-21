using GrpcEmbed.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RequestArgumentBindingTests
{
    [Fact]
    public void Transport_and_scoped_arguments_bind_in_declared_order_without_sharing_values()
    {
        using var provider = new ServiceCollection().AddScoped<ScopedValue>().BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = first.ServiceProvider };
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "synthetic") }));
        http.User = user;
        using var cancellation = new CancellationTokenSource();
        var method = CreateMethod();
        var a = GrpcEmbedService.BindArguments(method, new Envelope(42, "first"), http, cancellation.Token);
        Assert.Equal(42L, a[0]);
        Assert.Same(http, a[1]);
        Assert.Same(first.ServiceProvider.GetRequiredService<ScopedValue>(), a[2]);
        Assert.Equal("first", a[3]);
        Assert.Same(user, a[4]);
        Assert.Same(http.Request, a[5]);
        Assert.Same(http.Response, a[6]);
        Assert.Equal(cancellation.Token, a[7]);
        http.RequestServices = second.ServiceProvider;
        var b = GrpcEmbedService.BindArguments(method, new Envelope(77, null), http, default);
        Assert.NotSame(a, b);
        Assert.Equal(77L, b[0]);
        Assert.Null(b[3]);
        Assert.NotSame(a[2], b[2]);
        Assert.Equal(42L, a[0]);
        Assert.Equal("first", a[3]);
    }

    [Fact]
    public void Removing_transport_buffer_reduces_allocations_without_bypassing_required_services()
    {
        using var provider = new ServiceCollection().AddScoped<ScopedValue>().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var method = CreateMethod();
        var request = new Envelope(42, "synthetic");
        static object?[] Legacy(RuntimeMethod method, object request, HttpContext http)
        {
            var transport = new object?[method.RequestReaders.Length];
            for (var i = 0; i < transport.Length; i++) transport[i] = method.RequestReaders[i](request);
            var args = GrpcEmbedService.BindArguments(method, request, http, default);
            // Keep the old additional array live; reuse its values as the old binder did.
            foreach (var pair in method.Arguments.Select((plan, index) => (plan, index)))
                if (pair.plan.Source == RuntimeArgumentSource.Transport) args[pair.index] = transport[pair.plan.TransportIndex];
            return args;
        }
        // The measured baseline below isolates only the old extra array, not the
        // LINQ/eager reads in Legacy above, so it cannot inflate claimed savings.
        for (var i = 0; i < 1000; i++) GrpcEmbedService.BindArguments(method, request, http, default);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var transport = new object?[method.RequestReaders.Length];
            var args = GrpcEmbedService.BindArguments(method, request, http, default);
            GC.KeepAlive(transport);
            GC.KeepAlive(args);
        }
        var baseline = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) GC.KeepAlive(GrpcEmbedService.BindArguments(method, request, http, default));
        var candidate = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(baseline - candidate >= 40000);
        Assert.Equal(Legacy(method, request, http), GrpcEmbedService.BindArguments(method, request, http, default));
        http.RequestServices = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => GrpcEmbedService.BindArguments(method, request, http, default));
    }

    [Fact]
    public void Route_operation_is_immutable_metadata_not_rebuilt_on_each_match()
    {
        var method = CreateMethod();
        var route = new GrpcEmbedRouteMetadata(method, RoutePatternFactory.Parse("api/users"));
        Assert.Equal("GrpcEmbed.Synthetic/Get", route.Operation);
        Assert.Same(route.Operation, route.Operation);
    }

    private static RuntimeMethod CreateMethod()
    {
        var method = typeof(RequestArgumentBindingTests).GetMethod(nameof(Action), BindingFlags.NonPublic | BindingFlags.Static)!;
        var parameters = method.GetParameters();
        var sources = new[] { RuntimeArgumentSource.Transport, RuntimeArgumentSource.HttpContext, RuntimeArgumentSource.Services,
            RuntimeArgumentSource.Transport, RuntimeArgumentSource.ClaimsPrincipal, RuntimeArgumentSource.HttpRequest,
            RuntimeArgumentSource.HttpResponse, RuntimeArgumentSource.CancellationToken };
        var transport = 0;
        var plans = parameters.Select((p, i) => new RuntimeArgument(p, sources[i],
            sources[i] == RuntimeArgumentSource.Transport ? transport++ : -1, Array.Empty<ValidationAttribute>())).ToArray();
        return new RuntimeMethod(new ControllerActionDescriptor { MethodInfo = method }, "Synthetic", "Get",
            typeof(Envelope), typeof(object), typeof(object), new[] { parameters[0], parameters[3] },
            (_, _) => null, new Func<object, object?>[] { value => ((Envelope)value).Id, value => ((Envelope)value).Name }, plans, null);
    }

    private static void Action(long id, HttpContext http, [FromServices] ScopedValue service, string? name,
        ClaimsPrincipal user, HttpRequest request, HttpResponse response, CancellationToken token) { }
    private sealed record Envelope(long Id, string? Name);
    private sealed class ScopedValue { }
}
