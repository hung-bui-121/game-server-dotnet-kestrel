using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

/// <summary>Whether the startup checks run before serving or before provisioning (research R9).</summary>
internal enum StartupCheckMode
{
    Serve,
    Provision,
}

/// <summary>
/// The <see cref="IStartupCheck"/> registrations of this host (research R9, FG-023). The collection is captured in
/// <c>AddGameServer</c> and read after <c>Build()</c> (V4), so each check is created from its own descriptor inside its own
/// isolation: one throwing constructor or factory never hides the other checks.
/// </summary>
internal sealed class StartupCheckRegistrations(IServiceCollection services)
{
    /// <summary>The non-keyed <see cref="IStartupCheck"/> descriptors, in registration order.</summary>
    public IReadOnlyList<ServiceDescriptor> Descriptors() =>
        services.Where(d => d.ServiceType == typeof(IStartupCheck) && !d.IsKeyedService).ToArray();

    /// <summary>
    /// Creates the check of <paramref name="descriptor"/> the way the container would (V3): the instance, the type through
    /// <see cref="ActivatorUtilities"/>, or the factory. A constructor exception is rethrown unwrapped.
    /// </summary>
    public static IStartupCheck Create(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        try
        {
            var created = descriptor.ImplementationInstance
                ?? (descriptor.ImplementationType is { } type
                    ? ActivatorUtilities.CreateInstance(provider, type)
                    : descriptor.ImplementationFactory!(provider))
                ?? throw new InvalidOperationException("the registration's factory returned null");
            return created as IStartupCheck
                ?? throw new InvalidOperationException($"the registration created {created.GetType().Name}, which is not an {nameof(IStartupCheck)}");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// The name a line gives the check of <paramref name="descriptor"/> (startup-report §7): the implementation type; for a factory
    /// registration the class it returns (<c>AddSingleton&lt;IStartupCheck, X&gt;(sp =&gt; …)</c>, a method group returning X),
    /// otherwise its declaring type, skipping compiler-generated closure classes (V4).
    /// </summary>
    public static string NameOf(ServiceDescriptor descriptor, int index)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance.GetType().Name;
        }

        if (descriptor.ImplementationType is { } type)
        {
            return type.Name;
        }

        if (descriptor.ImplementationFactory?.Method.ReturnType is { IsClass: true, IsAbstract: false } returned
            && typeof(IStartupCheck).IsAssignableFrom(returned))
        {
            return returned.Name;
        }

        for (var declaring = descriptor.ImplementationFactory?.Method.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
        {
            if (!declaring.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) && !declaring.Name.StartsWith('<'))
            {
                return declaring.Name;
            }
        }

        return $"factory #{index} ({nameof(IStartupCheck)})";
    }
}
