using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GrpcEmbed.Client;

/// <summary>Decorates existing client registrations without modifying their registration library.</summary>
public static class GrpcEmbedRegisteredClientsExtensions
{
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("GrpcEmbed creates runtime interface proxies.")]
#endif
    [RequiresUnreferencedCode("GrpcEmbed inspects existing client contracts.")]
    public static IServiceCollection AddGrpcEmbedClients<TMarker>(this IServiceCollection services,
        Func<IServiceProvider, TMarker, GrpcEmbedClientOptions?> configure) where TMarker : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(x => x.ServiceType == typeof(RegistrationMarker<TMarker>)))
            throw new InvalidOperationException("GrpcEmbed client decoration must be registered only once.");
        var descriptors = services.Where(x => x.ServiceType.IsInterface &&
            typeof(TMarker).IsAssignableFrom(x.ServiceType)).ToArray();
        if (descriptors.GroupBy(x => x.ServiceType).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Multiple registrations of the same client interface require explicit disambiguation.");
        if (descriptors.Any(x => x.Lifetime != ServiceLifetime.Singleton))
            throw new NotSupportedException("Automatic GrpcEmbed decoration currently requires singleton client registrations.");
        if (descriptors.Any(x => x.ImplementationInstance is not null))
            throw new NotSupportedException("Automatic GrpcEmbed decoration requires factory or type registrations to preserve ownership.");
        services.AddSingleton(new RegistrationMarker<TMarker>());
        foreach (var descriptor in descriptors)
        {
            typeof(GrpcEmbedRegisteredClientsExtensions).GetMethod(nameof(Register), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TMarker), descriptor.ServiceType)
                .Invoke(null, new object[] { services, descriptor, configure });
        }
        return services;
    }

    private static void Register<TMarker, TContract>(IServiceCollection services, ServiceDescriptor original,
        Func<IServiceProvider, TMarker, GrpcEmbedClientOptions?> configure)
        where TMarker : class where TContract : class
    {
        services.AddSingleton<RegisteredClient<TContract>>(provider =>
        {
            var value = original.ImplementationInstance ??
                original.ImplementationFactory?.Invoke(provider) ??
                ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
            var ownsOriginal = original.ImplementationInstance is null;
            try
            {
                var options = configure(provider, (TMarker)value);
                return new RegisteredClient<TContract>((TContract)value, ownsOriginal, options);
            }
            catch
            {
                if (ownsOriginal && value is IDisposable disposable) disposable.Dispose();
                throw;
            }
        });
        var index = services.IndexOf(original);
        services[index] = ServiceDescriptor.Singleton<TContract>(provider =>
            provider.GetRequiredService<RegisteredClient<TContract>>().Client);
        services.AddSingleton<IGrpcEmbedClientInitializer>(new GrpcEmbedClientInitializer<TContract>());
    }

    private sealed class RegistrationMarker<TMarker> { }
}

internal sealed class RegisteredClient<TContract> : IDisposable where TContract : class
{
    private readonly GrpcChannel? _channel;
    public TContract Client { get; }

    public RegisteredClient(TContract original, bool ownsOriginal, GrpcEmbedClientOptions? options)
    {
        // In REST mode the returned original is tracked for disposal by the DI container.
        if (options is null) { Client = original; return; }
        // An externally supplied instance must remain externally owned, in either mode.
        _original = ownsOriginal ? original as IDisposable : null;
        if (options.Address is null) throw new InvalidOperationException("GrpcEmbed client Address is required.");
        if (options.HttpClient is not null && options.HttpHandler is not null)
            throw new InvalidOperationException("Specify HttpClient or HttpHandler, not both.");
        if (options.TransportMode != GrpcEmbedTransportMode.GrpcOnly)
            throw new InvalidOperationException("Only gRPC transport is supported.");
        options.LocalPropertyTarget = original;
        _channel = GrpcEmbedChannelFactory.Create(options);
        try { Client = GrpcEmbedProxy<TContract>.Create(_channel.CreateCallInvoker(), options); }
        catch { _channel.Dispose(); throw; }
    }

    private readonly IDisposable? _original;
    public void Dispose()
    {
        _channel?.Dispose();
        _original?.Dispose();
    }
}
