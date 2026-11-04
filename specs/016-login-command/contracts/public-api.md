# Public API — 016 login-command

Signatures only (no bodies). Everything here is additive except the `CommandRunner` constructor (§2.2, BC-12). The behaviour
changes are BC-1..BC-11 in the plan; BC-12 was added by the review (T081 m8). Internal types are named where a reviewer needs
them; they are not consumer API.

## 1. Core (`GameServer.Core`, business layer, ARC-01)

### 1.1 Account initializers (`GameServer.Accounts`, new namespace)

```csharp
namespace GameServer.Accounts;

/// <summary>
/// Runs once per account, inside the transaction that creates the account (FG-015). Pure over the context like a command
/// handler (CMD-04): the transaction may run it several times. Returns null for OK, or a code that fails the creation
/// (logged with the initializer's name; the login answers INTERNAL). Created per run from DI (constructor injection).
/// </summary>
public interface IAccountInitializer
{
    ValueTask<GameServer.Errors.ErrorCode?> InitializeAsync(GameServer.Commands.ICommandContext context);
}

/// <summary>An initializer with the aggregates it may write and the claim markers it may reserve.</summary>
public sealed class AccountInitializerRegistration
{
    public static AccountInitializerRegistration Of<TInitializer>(string name, IReadOnlyList<string> touches, int maxClaimMarkers = 0)
        where TInitializer : IAccountInitializer;

    /// <summary><c>&lt;module&gt;.&lt;name&gt;</c>, lowercase, unique per deployment.</summary>
    public string Name { get; }

    public Type InitializerType { get; }

    /// <summary>Aggregates the initializer may read and write; never <c>profile</c>.</summary>
    public IReadOnlyList<string> Touches { get; }

    public int MaxClaimMarkers { get; }
}
```

### 1.2 Descriptor member

```csharp
namespace GameServer.Modules;

public sealed class ModuleDescriptor
{
    // existing members unchanged

    /// <summary>
    /// Run in registration order inside the transaction that creates an account (first login), never for existing accounts.
    /// profile + the union of their Touches is at most CommandDescriptor.MaxTouches (checked at startup).
    /// </summary>
    public IReadOnlyList<GameServer.Accounts.AccountInitializerRegistration> AccountInitializers { get; init; } = [];
}
```

### 1.3 Error codes (`CoreErrorCodes`, listed in `All`)

```csharp
public static readonly ErrorCode CommandOutputTooLarge = new(23, "COMMAND_OUTPUT_TOO_LARGE", 200, false, "The command result is too large.");
public static readonly ErrorCode AggregateVersionAhead = new(24, "AGGREGATE_VERSION_AHEAD", 503, true, "Player data was written by a newer server version. Retry later.");
public static readonly ErrorCode AggregateUnreadable = new(25, "AGGREGATE_UNREADABLE", 500, false, "Player data could not be read.");
```

### 1.4 Changed XML doc (no signature change)

`ICommandOutcome.Details`: "Details of a rejection. A code that is not HTTP 200 sends them in the error envelope. A
business code (HTTP 200) sends them in `CommandResponse.details` (since 0.7.0)." (BC-11)

## 2. Platform (`GameServer.Platform`)

### 2.1 Account writer (`GameServer.Platform.Commands`)

```csharp
namespace GameServer.Platform.Commands;

/// <summary>
/// The login write of the account root (FG-015, FG-013): updates the profile of an existing player, or creates the account
/// (anchor rewrite + profile + account initializers + receipt) when the profile does not exist, in one transaction. The
/// create-or-update decision is taken in each transaction attempt (research R1).
/// </summary>
public interface IAccountWriter
{
    Task<AccountWriteResult<TProfile>> WriteLoginAsync<TProfile>(AccountLoginWrite<TProfile> write, CancellationToken cancellationToken)
        where TProfile : class;
}

/// <param name="PlayerId">Resolved player.</param>
/// <param name="Mutate">Applied to the stored profile, or to the default profile on creation; may throw GameServerException (late access checks).</param>
/// <param name="CreationAnchor">The identity mapping that must exist and name the player for a creation (010 R9).</param>
/// <param name="CorrelationId">Request correlation id, stored in the creation receipt.</param>
public sealed record AccountLoginWrite<TProfile>(
    string PlayerId,
    Func<TProfile, TProfile> Mutate,
    GameServer.Platform.Persistence.DocumentKey CreationAnchor,
    string CorrelationId)
    where TProfile : class;

/// <param name="Created">True only when this call committed the creation of the account.</param>
/// <param name="CreationReceiptId">Receipt of the creation; null when not created or when the initializers had no effect.</param>
public sealed record AccountWriteResult<TProfile>(TProfile Profile, long Rev, bool Created, string? CreationReceiptId)
    where TProfile : class;

public static class AccountCreation
{
    /// <summary>Reserved operation of the creation receipt; no module command may use it.</summary>
    public const string Operation = "identity.create_account";
}

// internal sealed class AccountWriter : IAccountWriter                       (registered by AddGameServer)
// internal sealed class InitializerContext : ICommandContext                  (per-initializer view, research R3)
// internal sealed class AccountInitializerFailedException : Exception         (name, code or exception type)
// internal static class ReceiptBuilder { static ReceiptDocument Build(in ReceiptInputs inputs); }   (research R14)
// internal readonly record struct ReceiptInputs(…)
// internal sealed record CommitViolation(ErrorCode Code, IReadOnlyDictionary<string, string> Details, string Line)
// CommitRules: CheckResultSize(...) → CommitViolation?;  CheckReceiptSize(string operation, int receiptBytes, CommandSettings) → CommitViolation?
```

### 2.2 Command execution (additive members)

```csharp
public sealed record CommandExecution(/* existing positional members unchanged */)
{
    /// <summary>Details of a business rejection (HTTP 200), sent in CommandResponse.details; empty otherwise.</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = EmptyDetails;

    /// <summary>Client-visible aggregates this response cannot carry (replay of a broken aggregate).</summary>
    public IReadOnlyList<GameServer.Platform.Aggregates.AggregateReadFailure> Unavailable { get; init; } = [];
}

// Changed (BC-12): the last constructor parameter was ILogger<CommandRunner> logger. The runner logs in two categories
// (its own and GameServer.Commands for the output-limit line), so it takes the factory. DI-created runners are unaffected.
public sealed class CommandRunner(
    ModuleRegistry registry,
    ITransactionalDocumentStore store,
    AggregateDocumentSerializer serializer,
    IGameConfig config,
    IClock clock,
    GameServerOptions options,
    PerPlayerCommandLimiter limiter,
    IOutboxRelay relay,
    PlatformMetrics metrics,
    IEnumerable<ICommandGuard> guards,
    IServiceProvider services,
    ILoggerFactory loggers) : ICommandRunner;
```

### 2.3 Read failures (`GameServer.Platform.Aggregates`)

```csharp
namespace GameServer.Platform.Aggregates;

public enum AggregateReadFailureReason
{
    VersionAhead,
    UpgradeFailed,
    Unreadable,
    MapperFailed,
}

public sealed record AggregateReadFailure(
    string AggregateId,
    string PlayerId,
    AggregateReadFailureReason Reason,
    string ExceptionType,
    int StoredSchemaVersion,
    int CodeSchemaVersion)
{
    /// <summary>AGGREGATE_VERSION_AHEAD for VersionAhead, else AGGREGATE_UNREADABLE.</summary>
    public GameServer.Errors.ErrorCode Code { get; }

    /// <summary>version_ahead | upgrade_failed | unreadable | mapper_failed.</summary>
    public string ReasonName { get; }

    /// <summary>
    /// GameServerException(Code) with details {aggregate}, retry delay for VersionAhead, no inner exception, marked as a
    /// reported read failure (the error middleware does not log it again).
    /// </summary>
    public GameServer.Errors.GameServerException ToException();

    /// <summary>The failure carried by an exception from <see cref="ToException"/>; false for any other exception.</summary>
    public static bool TryGet(Exception exception, [NotNullWhen(true)] out AggregateReadFailure? failure);
}

/// <summary>Logs (once) and counts each read failure (FR-026).</summary>
public sealed class AggregateReadFailures
{
    /// <summary>Logs in category GameServer.Aggregates (LogCategory), so it takes the factory (corrected in T082 m8).</summary>
    public AggregateReadFailures(GameServer.Platform.Diagnostics.PlatformMetrics metrics, ILoggerFactory loggers);

    public const string LogCategory = "GameServer.Aggregates";

    public void Report(AggregateReadFailure failure);
}

public sealed class AggregateDocumentSerializer
{
    public AggregateDocumentSerializer(GameServerOptions options, GameServer.Platform.Diagnostics.PlatformMetrics metrics);   // existing: counts, does not log
    public AggregateDocumentSerializer(GameServerOptions options, GameServer.Platform.Diagnostics.PlatformMetrics metrics, AggregateReadFailures failures);

    /// <summary>Throws failure.ToException() for a read failure (was INTERNAL / raw exceptions; BC-5).</summary>
    public LoadedAggregate Load(GameServer.Aggregates.PlayerAggregateDescriptor descriptor, string playerId, GameServer.Platform.Persistence.StoredDocument? document);

    /// <summary>Never throws for a read failure; reports it once.</summary>
    public bool TryLoad(
        GameServer.Aggregates.PlayerAggregateDescriptor descriptor,
        string playerId,
        GameServer.Platform.Persistence.StoredDocument? document,
        [NotNullWhen(true)] out LoadedAggregate? loaded,
        [NotNullWhen(false)] out AggregateReadFailure? failure);
}

/// <summary>Readable aggregates and the failures of one read.</summary>
public sealed record PlayerSnapshot(IReadOnlyList<LoadedAggregate> Sections, IReadOnlyList<AggregateReadFailure> Unavailable);

public sealed class PlayerSnapshotReader
{
    // existing ReadAsync / ReadAllAsync / ReadOneAsync unchanged in signature; they throw the typed exception (BC-5)

    /// <summary>Like ReadAsync, but a broken aggregate is listed instead of failing the read. Unknown/hidden ids are still BAD_REQUEST.</summary>
    public Task<PlayerSnapshot> ReadAvailableAsync(string playerId, IReadOnlyList<string>? aggregateIds, CancellationToken cancellationToken);

    /// <summary>Like ReadAllAsync (GM), with failures listed.</summary>
    public Task<PlayerSnapshot> ReadAllAvailableAsync(string playerId, CancellationToken cancellationToken);
}
```

### 2.4 Command settings

```csharp
public sealed class CommandSettings
{
    // existing properties and defaults unchanged

    /// <summary>One line per value outside its documented range (FR-032); empty when valid.</summary>
    public IEnumerable<string> Validate();
}
```

### 2.5 Registry (internal, listed for reviewers)

`ModuleRegistry.ValidateAccountInitializers` and the reserved-operation check join `ModuleRegistry.Validate`.
`ModuleRegistry.AccountInitializers` (internal) lists registrations in run order.

## 3. Platform.Web (`GameServer.Platform.Web`)

```csharp
namespace GameServer.Platform.Web.Aggregates;

/// <summary>
/// The one place a section payload is built (snapshot, login, command response): a wire mapper that throws makes the section
/// unavailable (reported once, research R7) instead of failing the response.
/// </summary>
public sealed class SectionWriter
{
    public SectionWriter(AggregateWireMapperRegistry mappers, GameServer.Platform.Aggregates.AggregateReadFailures failures);

    public void WriteSections(
        string playerId,
        IEnumerable<GameServer.Platform.Aggregates.LoadedAggregate> loaded,
        IEnumerable<GameServer.Platform.Aggregates.AggregateReadFailure> failures,
        Google.Protobuf.Collections.RepeatedField<GameServer.Protocol.Core.V1.Section> sections,
        Google.Protobuf.Collections.RepeatedField<GameServer.Protocol.Core.V1.UnavailableSection> unavailable);

    public void WriteUpdates(
        string playerId,
        IEnumerable<GameServer.Platform.Commands.AggregateChange> changes,
        IEnumerable<GameServer.Platform.Aggregates.AggregateReadFailure> failures,
        Google.Protobuf.Collections.RepeatedField<GameServer.Protocol.Core.V1.SectionUpdate> updates,
        Google.Protobuf.Collections.RepeatedField<GameServer.Protocol.Core.V1.UnavailableSection> unavailable);
}
```

`GmCommandResponse` (`GameServer.Platform.Web.Commands`) gains init properties `Details`
(`IReadOnlyDictionary<string,string>`) and `Unavailable` (`IReadOnlyList<GmUnavailableAggregate>`). See wire-changes §4.

## 4. Identity module (`GameServer.Modules.Identity.Adapters`)

```csharp
public sealed record LoginResult(string Token, string PlayerId, DateTimeOffset ExpiresAt, IReadOnlyList<LoadedAggregate> Sections)
{
    /// <summary>True when this login created the player (FG-013); equals the analytics isNewUser.</summary>
    public bool IsNewPlayer { get; init; }

    /// <summary>Client-visible aggregates the login could not read (FG-029).</summary>
    public IReadOnlyList<GameServer.Platform.Aggregates.AggregateReadFailure> Unavailable { get; init; } = [];
}
```

`LoginCompletion` takes `IAccountWriter` instead of `IPlayerAggregateWriter` (constructor of a DI-created class; not
consumer API).

## 5. Gm module (`GameServer.Modules.Gm.Adapters`)

`GmPlayerView` gains the init property `Unreadable` (`IReadOnlyList<GmUnreadableAggregate>`), with
`GmUnreadableAggregate(string Id, string Code, string Reason, int StoredSchemaVersion, int CodeSchemaVersion)`.

## 6. TestKit (`GameServer.TestKit`)

```csharp
public sealed class CommandContextDouble : ICommandContext
{
    // existing members unchanged

    /// <summary>
    /// The double for an account initializer: the initializer's touches at their default state (rev 0) unless seeded, the
    /// creation's system actor for the default player, operation identity.create_account (FR-017). Throws ArgumentException
    /// when no given module registers the initializer or one of its aggregates.
    /// </summary>
    public static CommandContextDouble ForAccountInitializer(string initializerName, params IGameModule[] modules);

    /// <summary>Existing state at the given revision (the receipt's newRev is rev + 1; research R14).</summary>
    public CommandContextDouble Seed<TState>(string aggregateId, TState state, long rev)
        where TState : class;

    /// <summary>
    /// What the account writer would refuse for the initializers' run so far (the runner's lines): undeclared writes were already
    /// thrown; aggregate sizes, audit entries (0 expected), receipt size (empty result). Empty when the creation would commit.
    /// </summary>
    public IReadOnlyList<string> CheckAccountCreation();

    // CheckCommit(outcome, encodeResult) now also reports the receipt-size line (ReceiptMaxBytes) measured with the runner's
    // builder; XML doc lists the inputs that are placeholders (body hash) and the ones taken from the configuration.
}
```

## 7. Unity SDK (`com.gameserver.client` 0.7.0, `Runtime/Core`, C# 9 / netstandard2.1)

### 7.1 Client

```csharp
namespace GameServer.Client
{
    public sealed class GameServerClient
    {
        // existing members unchanged

        /// <summary>A fresh request id (lowercase UUID, format D).</summary>
        public static string NewRequestId();

        /// <summary>
        /// SendCommandAsync with a caller-owned request id: every attempt sends it with the same bytes; the same id and bytes in a
        /// later call (also after an app restart) replay the committed command (CMD-02, CMD-07). Null = the SDK generates one.
        /// Throws ArgumentException for an id that is not a UUID in format D, before any network call or disk write.
        /// </summary>
        public Task<CommandResult<TResult>> SendCommandAsync<TResult>(
            string route,
            IMessage request,
            MessageParser<TResult> resultParser,
            string? requestId,
            CancellationToken cancellationToken = default)
            where TResult : class, IMessage<TResult>;

        /// <summary>Entries of the pending-command store (empty without a store).</summary>
        public IReadOnlyList<PendingCommand> PendingCommands { get; }

        public PendingCommandStatus StatusOf(PendingCommand command);

        /// <summary>
        /// Resends the stored bytes with the stored request id to the stored route. Throws InvalidOperationException when the
        /// entry is not Resendable (another player, expired, or unknown).
        /// </summary>
        public Task<CommandResult<TResult>> ResendPendingAsync<TResult>(PendingCommand command, MessageParser<TResult> resultParser, CancellationToken cancellationToken = default)
            where TResult : class, IMessage<TResult>;

        /// <summary>Removes an entry (the game decided from state). Unknown ids are ignored.</summary>
        public void DiscardPendingCommand(string requestId);
    }

    public sealed class CommandResult<TResult>
    {
        // existing constructor and members unchanged (the old constructor sets RequestId = string.Empty)
        public CommandResult(CommandResponse response, TResult? result, bool sectionsStale, bool resultUnreadable, string requestId);

        /// <summary>The request id this command used.</summary>
        public string RequestId { get; }
    }

    public sealed class GameServerError
    {
        // existing members unchanged; RequestId is now also set on SDK-made errors of a call
        public static GameServerError Client(string code, string message, bool retryable, string? requestId);
    }

    public static class ErrorCodes
    {
        public const string CommandOutputTooLarge = "COMMAND_OUTPUT_TOO_LARGE";
        public const string AggregateVersionAhead = "AGGREGATE_VERSION_AHEAD";
        public const string AggregateUnreadable = "AGGREGATE_UNREADABLE";
    }

    public sealed class GameServerClientOptions
    {
        // existing members unchanged

        /// <summary>Opt-in store of commands whose outcome is unknown (FG-022). One client per store.</summary>
        public IPendingCommandStore? PendingCommands { get; set; }

        /// <summary>Entries older than this (server clock) are Expired; default 23 h, below the 24 h replay window. Validate: (0, 168 h].</summary>
        public TimeSpan PendingCommandMaxAge { get; set; }
    }
}
```

### 7.2 Pending-command store

```csharp
namespace GameServer.Client
{
    public sealed class PendingCommand
    {
        public PendingCommand(string requestId, string playerId, string route, byte[] body, DateTimeOffset recordedAtServerUtc);

        public string RequestId { get; }
        public string PlayerId { get; }
        public string Route { get; }

        /// <summary>The exact bytes sent; never re-serialized.</summary>
        public byte[] Body { get; }

        public DateTimeOffset RecordedAtServerUtc { get; }
    }

    public enum PendingCommandStatus
    {
        Resendable,
        Expired,
        OtherPlayer,
    }

    /// <summary>Not thread-safe: use it from the client's thread. Never throws for disk problems.</summary>
    public interface IPendingCommandStore
    {
        /// <summary>A snapshot: Record and Remove do not change a list already returned (T082 B1).</summary>
        IReadOnlyList<PendingCommand> Entries { get; }

        void Record(PendingCommand command);

        void Remove(string requestId);

        /// <summary>Entries skipped at load (damaged, unknown format); null when everything loaded.</summary>
        string? LoadProblem { get; }

        /// <summary>The last write failure; null after a successful write.</summary>
        string? WriteProblem { get; }
    }

    public sealed class InMemoryPendingCommandStore : IPendingCommandStore { /* members of the interface */ }

    /// <summary>One file per entry in Folder (contracts/sdk-pending-store.md); atomic per entry; damaged entries are skipped.</summary>
    public sealed class PersistentPendingCommandStore : IPendingCommandStore
    {
        public PersistentPendingCommandStore(string folder);

        public string Folder { get; }

        // members of the interface
    }
}
```

### 7.3 Section store

```csharp
public sealed class UnavailableSectionState
{
    public string Id { get; }
    public string Code { get; }
    public int CodeNumber { get; }
}

public sealed class SectionStore
{
    // existing members unchanged; Set/Apply clear the section's mark, Clear() clears all marks

    public IReadOnlyDictionary<string, UnavailableSectionState> Unavailable { get; }

    public bool IsUnavailable(string id);

    /// <summary>Raised when a section becomes unavailable or available again.</summary>
    public event Action<string>? AvailabilityChanged;
}
```

### 7.4 Unity helper (`Runtime/Unity`, `#if UNITY_5_3_OR_NEWER`)

```csharp
public static class UnityPendingCommandStore
{
    /// <summary>Folder persistentDataPath/gameserver-pending/{environmentName}; name rule of UnityConfigCache; iOS no-backup flag.</summary>
    public static PersistentPendingCommandStore Create(string environmentName);
}
```

Internal: `PendingCommandFormat` (container v1), `CacheFileOps.EnumerateFiles(string folder, string pattern)` (new
virtual member). Every new file has a committed `.meta`.
