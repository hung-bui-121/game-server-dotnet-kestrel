using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Outbox;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Commands;

/// <summary>
/// Commit rules shared by <see cref="CommandRunner"/> and the consumer test kit (API-11, SEC-14, CMD-03, DAT-01). Each check
/// returns the violation message, or null when the outcome may commit; the runner turns a code, audit or size message into
/// INTERNAL and an aggregate above its budget into the AGGREGATE_TOO_LARGE rejection (nothing commits either way).
/// </summary>
internal static class CommitRules
{
    /// <summary>
    /// API-11: a committed receipt stores the code by name and a response resolves it through the catalog, so the code of a
    /// commit must be the catalogued instance (else the client gets OK or another code's number) and a business code (HTTP 200:
    /// a commit always answers 200).
    /// </summary>
    internal static string? CheckCode(CommandRegistration registration, ModuleRegistry registry, ErrorCode? code)
    {
        if (code is null)
        {
            return null;
        }

        var catalogued = registry.Errors.FindByName(code.Name);
        string problem;
        string fix;
        if (catalogued is null)
        {
            problem = "which is not in the error-code catalog; the client would receive OK";
            fix = "list the code in the module's ErrorCodeSet (ModuleDescriptor.ErrorCodes)";
        }
        else if (catalogued != code)
        {
            problem = $"which has the name of the catalogued {catalogued} but differs from it (number, HTTP status, retryable flag or message)";
            fix = "commit with the catalogued ErrorCode instance from the module's ErrorCodeSet";
        }
        else if (code.HttpStatus != 200)
        {
            problem = $"whose HTTP status is {code.HttpStatus}; a commit always answers HTTP 200";
            fix = "use CommandOutcome.Reject for non-200 codes (nothing is committed)";
        }
        else
        {
            return null;
        }

        return $"Command '{registration.Descriptor.Operation}' ({DescribeOwner(registration, registry)}) committed with error code {code}, {problem}. Fix: {fix} (API-11).";
    }

    /// <summary>
    /// SEC-14: every committed GM command carries exactly one audit entry in its receipt; other commands carry none, so
    /// the audit trail cannot be forged by Player or Dev commands.
    /// </summary>
    internal static string? CheckAudit(CommandDescriptor descriptor, IReadOnlyList<OutboxEntry> outbox)
    {
        var audits = outbox.Count(e => e.Kind == AuditExtensions.OutboxKind);
        var expected = descriptor.Profile == SecurityProfile.Gm ? 1 : 0;
        return audits == expected
            ? null
            : $"Command '{descriptor.Operation}' ({descriptor.Profile}) wrote {audits} audit entries; expected {expected} (SEC-14). " +
              "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.";
    }

    /// <summary>
    /// A set aggregate is written only when its canonical state JSON differs from the loaded one: setting the loaded state again
    /// (or changing it back) writes nothing and bumps no revision.
    /// </summary>
    internal static bool ChangesState(LoadedAggregate before, ReadOnlySpan<byte> newStateJson) => !newStateJson.SequenceEqual(before.StateJson);

    /// <summary>The stored document of a written aggregate must fit its <see cref="PlayerAggregateDescriptor.HardBytes"/> budget.</summary>
    internal static bool ExceedsHardBytes(PlayerAggregateDescriptor aggregate, int documentBytes) => documentBytes > aggregate.HardBytes;

    /// <summary>
    /// <see cref="ExceedsHardBytes"/> for the document the runner would store for <paramref name="newStateJson"/> (same envelope,
    /// next revision, measured with <paramref name="gameId"/> and <paramref name="environmentId"/>).
    /// </summary>
    internal static string? CheckAggregateSize(
        string operation,
        string gameId,
        string environmentId,
        LoadedAggregate before,
        byte[] newStateJson,
        DateTimeOffset now)
    {
        var (_, size) = AggregateDocumentSerializer.BuildDocument(gameId, environmentId, before, newStateJson, before.Rev + 1, now);
        return ExceedsHardBytes(before.Descriptor, size)
            ? $"Command '{operation}' would store aggregate '{before.Descriptor.AggregateId}' as {size} bytes; its HardBytes is " +
              $"{before.Descriptor.HardBytes}: the runner rejects the command with {CoreErrorCodes.AggregateTooLarge.Name} and nothing commits. " +
              "Fix: keep the state smaller or split it into another aggregate."
            : null;
    }

    /// <summary>CMD-03: the encoded result is stored in the receipt as resultCompact and must fit its limit.</summary>
    internal static string? CheckResultSize(string operation, int resultBytes, CommandSettings settings) =>
        resultBytes > settings.ResultCompactMaxBytes
            ? $"Result of '{operation}' is {resultBytes} bytes; resultCompact max is {settings.ResultCompactMaxBytes} (CMD-03)."
            : null;

    /// <summary>Owning module of a command, and the replacing handler when <c>ReplaceHandler</c> swapped it (EXT-02).</summary>
    private static string DescribeOwner(CommandRegistration registration, ModuleRegistry registry)
    {
        var operation = registration.Descriptor.Operation;
        var (module, declared) = registry.Modules
            .SelectMany(m => m.Descriptor.Commands.Select(c => (Module: m.Descriptor.Name, Registration: c)))
            .FirstOrDefault(x => x.Registration.Descriptor.Operation == operation);
        return declared is not null && declared.HandlerType != registration.HandlerType
            ? $"module '{module}', handler replaced by {registration.HandlerType.Name} through ReplaceHandler"
            : $"module '{module}'";
    }
}
