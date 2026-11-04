using System.Globalization;
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
/// returns the violation (a message, or a <see cref="CommitViolation"/> for the size rules of FG-016), or null when the outcome
/// may commit; the runner turns a code or audit message into INTERNAL, an aggregate above its budget into the AGGREGATE_TOO_LARGE
/// rejection and a result or receipt over its limit into the COMMAND_OUTPUT_TOO_LARGE rejection (nothing commits either way).
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

    /// <summary>
    /// CMD-03 (FG-016): the encoded result is stored in the receipt as resultCompact and must fit
    /// <see cref="CommandSettings.ResultCompactMaxBytes"/>; exactly at the limit commits.
    /// </summary>
    internal static CommitViolation? CheckResultSize(string operation, int resultBytes, CommandSettings settings) =>
        resultBytes > settings.ResultCompactMaxBytes
            ? CommitViolation.OutputTooLarge(
                OutputPartResult,
                resultBytes,
                settings.ResultCompactMaxBytes,
                $"Command '{operation}' result is {resultBytes} bytes; ResultCompactMaxBytes is {settings.ResultCompactMaxBytes}: the runner rejects the " +
                $"command with {CoreErrorCodes.CommandOutputTooLarge.Name} (part {OutputPartResult}) and nothing commits. " +
                "Fix: return less or raise GameServer:Commands:ResultCompactMaxBytes.")
            : null;

    /// <summary>
    /// CMD-03 (FG-016): the receipt document must fit <see cref="CommandSettings.ReceiptMaxBytes"/>; exactly at the limit commits.
    /// <paramref name="operation"/> is the command's operation, or <c>identity.create_account (initializer {name})</c> for an
    /// account initializer.
    /// </summary>
    internal static CommitViolation? CheckReceiptSize(string operation, int receiptBytes, CommandSettings settings) =>
        CheckReceiptSize(
            operation,
            receiptBytes,
            settings,
            $"the runner rejects the command with {CoreErrorCodes.CommandOutputTooLarge.Name} (part {OutputPartReceipt}) and nothing commits");

    /// <summary>
    /// <see cref="CheckReceiptSize(string, int, CommandSettings)"/> for the receipt of an account creation, which is no command
    /// rejection: the account writer refuses the creation and the login answers <c>INTERNAL</c> (D15, T082).
    /// </summary>
    internal static CommitViolation? CheckCreationReceiptSize(string operation, int receiptBytes, CommandSettings settings) =>
        CheckReceiptSize(operation, receiptBytes, settings, $"the account writer refuses the creation and the login answers {CoreErrorCodes.Internal.Name}");

    private static CommitViolation? CheckReceiptSize(string operation, int receiptBytes, CommandSettings settings, string consequence) =>
        receiptBytes > settings.ReceiptMaxBytes
            ? CommitViolation.OutputTooLarge(
                OutputPartReceipt,
                receiptBytes,
                settings.ReceiptMaxBytes,
                $"Command '{operation}' receipt would be {receiptBytes} bytes; ReceiptMaxBytes is {settings.ReceiptMaxBytes}: {consequence}. " +
                "Fix: fewer ledger/outbox entries or raise GameServer:Commands:ReceiptMaxBytes.")
            : null;

    /// <summary>Value of the <c>part</c> detail of <c>COMMAND_OUTPUT_TOO_LARGE</c> for the receipt.</summary>
    internal const string OutputPartReceipt = "receipt";

    /// <summary>Value of the <c>part</c> detail of <c>COMMAND_OUTPUT_TOO_LARGE</c> for the encoded result.</summary>
    internal const string OutputPartResult = "result";

    /// <summary>The <c>GameServer:Commands</c> setting that limits <paramref name="part"/> (for the log line's fix).</summary>
    internal static string OutputLimitSetting(string part) =>
        string.Equals(part, OutputPartResult, StringComparison.Ordinal) ? nameof(CommandSettings.ResultCompactMaxBytes) : nameof(CommandSettings.ReceiptMaxBytes);

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

/// <summary>
/// A commit rule the run broke, found before anything is staged: the code the runner rejects with, the details sent with it
/// and the line for the log, the startup report or the test kit (errors-and-report-lines §5).
/// </summary>
internal sealed record CommitViolation(ErrorCode Code, IReadOnlyDictionary<string, string> Details, string Line)
{
    internal const string PartDetail = "part";
    internal const string BytesDetail = "bytes";
    internal const string LimitDetail = "limit";

    /// <summary><c>COMMAND_OUTPUT_TOO_LARGE</c> with details <c>part</c>, <c>bytes</c> and <c>limit</c> (FR-030).</summary>
    internal static CommitViolation OutputTooLarge(string part, int bytes, int limit, string line) =>
        new(
            CoreErrorCodes.CommandOutputTooLarge,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PartDetail] = part,
                [BytesDetail] = bytes.ToString(CultureInfo.InvariantCulture),
                [LimitDetail] = limit.ToString(CultureInfo.InvariantCulture),
            },
            line);
}
