using GrpcEmbed.AspNetCore;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class RequestFilterMaterializationTests
{
    [Fact]
    public void Factories_resolve_in_order_for_each_request_scope_without_caching_instances()
    {
        using var provider = new ServiceCollection().AddScoped<ScopedFilter>().BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var direct = new ScopedFilter();
        var factory = new ScopedFactory();
        IList<FilterDescriptor> descriptors = new List<FilterDescriptor> { new(direct, 0), new(factory, 0) };
        var a = GrpcEmbedService.CreateRequestFilters(descriptors, first.ServiceProvider);
        var b = GrpcEmbedService.CreateRequestFilters(descriptors, second.ServiceProvider);
        Assert.Equal(2, factory.Calls);
        Assert.Same(direct, a[0]);
        Assert.Same(direct, b[0]);
        Assert.Same(first.ServiceProvider.GetRequiredService<ScopedFilter>(), a[1]);
        Assert.Same(second.ServiceProvider.GetRequiredService<ScopedFilter>(), b[1]);
        Assert.NotSame(a[1], b[1]);
        Assert.NotSame(a, b);
        Assert.Same(factory, descriptors[1].Filter);
    }

    [Fact]
    public void Empty_descriptors_do_not_create_a_per_request_array()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Same(Array.Empty<IFilterMetadata>(), GrpcEmbedService.CreateRequestFilters(new List<FilterDescriptor>(), provider));
    }

    [Fact]
    public void Factory_exceptions_propagate_without_instantiating_later_filters()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var failure = new InvalidOperationException("factory failure");
        var later = new ScopedFactory();
        IList<FilterDescriptor> descriptors = new List<FilterDescriptor> { new(new FailingFactory(failure), 0), new(later, 0) };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => GrpcEmbedService.CreateRequestFilters(descriptors, provider)));
        Assert.Equal(0, later.Calls);
    }

    private sealed class ScopedFilter : IFilterMetadata { }

    private sealed class ScopedFactory : IFilterFactory
    {
        public int Calls { get; private set; }
        public bool IsReusable => false;
        public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        {
            Calls++;
            return serviceProvider.GetRequiredService<ScopedFilter>();
        }
    }

    private sealed class FailingFactory(Exception failure) : IFilterFactory
    {
        public bool IsReusable => false;
        public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => throw failure;
    }
}
