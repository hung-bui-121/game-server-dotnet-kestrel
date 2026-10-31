using GameServer.Actors;
using GameServer.Commands;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Validation;

/// <summary>
/// Checks every command route (<see cref="CommandOperationMetadata"/>) against the registered commands before traffic: the
/// operation exists (ARC-07), the route's security profile is the command's (SEC-08) and a GM route requires the command's
/// permission (SEC-07). Registered commands without a route are not reported: GM-only hosts, service-only modules and game
/// code calling <c>ICommandRunner</c> from its own endpoint run commands that no command route maps.
/// </summary>
public static class CommandRouteValidator
{
    public static IReadOnlyList<string> Validate(IEnumerable<Endpoint> endpoints, IReadOnlyDictionary<string, CommandRegistration> commands)
    {
        var errors = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var operation = endpoint.Metadata.GetMetadata<CommandOperationMetadata>()?.Operation;
            if (operation is null)
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            var label = $"{string.Join(",", methods)} /{(endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/')}";
            if (!commands.TryGetValue(operation, out var command))
            {
                errors.Add(
                    $"[endpoint {label}] runs command '{operation}' which no registered module declares. " +
                    "Fix: add the command to its module's ModuleDescriptor.Commands and AddModule that module, " +
                    "or pass the registered operation name to MapPlayerCommand/MapDevCommand/MapGmCommand (ARC-07).");
                continue;
            }

            // A route with zero or several profiles is already reported by EndpointSecurityValidator.
            var profiles = endpoint.Metadata.GetOrderedMetadata<SecurityProfileMetadata>();
            if (profiles.Count != 1)
            {
                continue;
            }

            var descriptor = command.Descriptor;
            if (profiles[0].Profile != descriptor.Profile)
            {
                errors.Add(
                    $"[endpoint {label}] has profile {profiles[0].Profile} but runs command '{operation}' declared with profile {descriptor.Profile}. " +
                    "Fix: map Player commands with MapPlayerCommand, Dev commands with MapDevCommand and GM commands with MapGmCommand, " +
                    "or correct the profile in the CommandDescriptor (SEC-08).");
                continue;
            }

            var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
            if (descriptor.Profile == SecurityProfile.Gm && !string.Equals(permission, descriptor.Permission, StringComparison.Ordinal))
            {
                errors.Add(
                    $"[endpoint {label}] requires permission '{permission}' but runs GM command '{operation}' which requires '{descriptor.Permission}'. " +
                    "Fix: pass the command's permission (CommandDescriptor.Permission) to MapGmCommand (SEC-07).");
            }
        }

        return errors;
    }
}
