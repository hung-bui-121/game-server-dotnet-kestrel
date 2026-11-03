using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Catalog;

/// <summary>A named hosting condition (FR-001); declarations reference it by name.</summary>
public sealed record RouteConditionRegistration(string Name, Func<IServiceProvider, bool> Holds);

public static class RouteConditionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hosting condition <paramref name="name"/> used by route declarations (<c>RouteDeclaration.Condition</c>): a
    /// declared route whose condition holds must be mapped, one whose condition does not hold is not reported (FR-011).
    /// </summary>
    public static IServiceCollection AddRouteCondition(this IServiceCollection services, string name, Func<IServiceProvider, bool> holds)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(holds);
        return services.AddSingleton(new RouteConditionRegistration(name, holds));
    }
}
