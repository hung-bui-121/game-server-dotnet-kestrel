using GameServer.Aggregates;
using GameServer.Analytics;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Probe module of the command-context double tests (slice 013 T030, T031): one player command that saves a note with
/// optional claim, audit, analytics entry and commit code; one GM command with a chosen number of audits; one command that
/// performs the access-rule violation its request names.
/// </summary>
public sealed class KitProbeModule : IGameModule
{
    public const string Name = "kit";
    public const string Notes = "kit_notes";
    public const string Ledger = "kit_ledger";
    public const string Other = "kit_other";
    public const string ClaimCollection = "kit_claims";
    public const string Permission = "kit.edit";
    public const string SaveOperation = "kit.save";
    public const string GmEditOperation = "kit.gm_edit";
    public const string ViolateOperation = "kit.violate";
    public const string SavedKind = "kit.saved";

    public static readonly ErrorCode Partial = new(24001, "KIT_PARTIAL", 200, false, "Partially saved.");
    public static readonly ErrorCode Conflict = new(24002, "KIT_CONFLICT", 409, false, "Conflict.");
    public static readonly AnalyticsEventType Saved = new("kit_saved", 1, "savedTime");

    public static CommandDescriptor SaveDescriptor { get; } = new(SaveOperation, touches: [Notes], reads: [Ledger], maxClaimMarkers: 2);

    public static CommandDescriptor GmEditDescriptor { get; } =
        new(GmEditOperation, touches: [Notes], profile: GameServer.Actors.SecurityProfile.Gm, permission: Permission);

    public static CommandDescriptor ViolateDescriptor { get; } = new(ViolateOperation, touches: [Notes], reads: [Ledger], maxClaimMarkers: 1);

    public ModuleDescriptor Describe() => new()
    {
        Name = Name,
        Permissions = [Permission],
        Collections = [new CollectionSpec(ClaimCollection, Deletion: DeletionTreatment.NoPlayerData)],
        Aggregates =
        [
            new PlayerAggregateDescriptor<KitNotes>(Notes, 1, clientVisible: false, () => new KitNotes()),
            new PlayerAggregateDescriptor<KitLedger>(Ledger, 1, clientVisible: false, () => new KitLedger { Balance = 100 }),
            new PlayerAggregateDescriptor<KitOther>(Other, 1, clientVisible: false, () => new KitOther()),
        ],
        Commands =
        [
            Command.Define<KitSave, string>(SaveDescriptor).HandledBy<KitSaveHandler>(),
            Command.Define<int, string>(GmEditDescriptor).HandledBy<KitGmEditHandler>(),
            Command.Define<KitViolation, string>(ViolateDescriptor).HandledBy<KitViolationHandler>(),
        ],
        ErrorCodes = new ErrorCodeSet(24000, 24099, Partial, Conflict),
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }
}

/// <summary>Touches an aggregate only <see cref="KitProbeModule"/> registers: building a double without that module fails.</summary>
public sealed class KitExtensionModule : IGameModule
{
    public const string Operation = "kit_ext.touch";

    public ModuleDescriptor Describe() => new()
    {
        Name = "kit_ext",
        Commands = [Command.Define<KitSave, string>(new CommandDescriptor(Operation, touches: [KitProbeModule.Notes])).HandledBy<KitSaveHandler>()],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }
}

public sealed record KitNotes
{
    public string Text { get; init; } = string.Empty;

    public int Count { get; init; }
}

public sealed record KitLedger
{
    public long Balance { get; init; }
}

public sealed record KitOther
{
    public int Value { get; init; }
}

/// <summary>What <see cref="KitSaveHandler"/> does besides setting the note.</summary>
public sealed record KitSave(
    string Text,
    string? ClaimKey = null,
    bool Audit = false,
    bool Analytics = false,
    ErrorCode? CommitCode = null,
    ErrorCode? RejectCode = null,
    int Sets = 1);

public sealed class KitSaveHandler : ICommandHandler<KitSave, string>
{
    public async ValueTask<CommandOutcome<string>> HandleAsync(KitSave request, ICommandContext context)
    {
        if (request.RejectCode is { } reject)
        {
            return CommandOutcome<string>.Reject(reject);
        }

        var notes = context.Get<KitNotes>(KitProbeModule.Notes);
        for (var i = 1; i <= request.Sets; i++)
        {
            notes = notes with { Text = request.Text, Count = notes.Count + 1 };
            context.Set(KitProbeModule.Notes, notes);
        }

        if (request.ClaimKey is { } claimKey
            && !await context.TryClaimAsync(KitProbeModule.ClaimCollection, claimKey, new Dictionary<string, string> { ["text"] = request.Text }))
        {
            return CommandOutcome<string>.Reject(KitProbeModule.Partial);
        }

        context.AddOutbox(KitProbeModule.SavedKind, new { text = request.Text });
        if (request.Audit)
        {
            context.AddAudit(KitProbeModule.Permission, "kit.save", "probe");
        }

        if (request.Analytics)
        {
            context.AddAnalyticsEvent(KitProbeModule.Saved, new { text = request.Text });
        }

        return request.CommitCode is { } code ? CommandOutcome<string>.Commit(notes.Text, code) : CommandOutcome<string>.Ok(notes.Text);
    }
}

public sealed class KitGmEditHandler : ICommandHandler<int, string>
{
    public ValueTask<CommandOutcome<string>> HandleAsync(int request, ICommandContext context)
    {
        context.Set(KitProbeModule.Notes, new KitNotes { Text = "gm", Count = 1 });
        for (var i = 0; i < request; i++)
        {
            context.AddAudit(KitProbeModule.Permission, "kit.gm_edit", "probe");
        }

        return ValueTask.FromResult(CommandOutcome<string>.Ok("gm"));
    }
}

/// <summary>Request of <see cref="KitViolationHandler"/>: the rule case to perform and the comma-separated purposes of derived ids.</summary>
public sealed record KitViolation(string Case, string Purposes = "battle");

/// <summary>Performs one access-rule case of the runner's context (FR-023) and returns what the handler observed.</summary>
public sealed class KitViolationHandler : ICommandHandler<KitViolation, string>
{
    public const string WriteOutsideTouches = "write-outside-touches";
    public const string ReadOutsideDeclared = "read-outside-declared";
    public const string WrongStateType = "wrong-state-type";
    public const string ClaimOverLimit = "claim-over-limit";
    public const string ClaimInReceipts = "claim-in-receipts";
    public const string ClaimInAggregate = "claim-in-pa";
    public const string DuplicateClaim = "duplicate-claim";
    public const string PreexistingClaim = "preexisting-claim";
    public const string SetThenGet = "set-then-get";
    public const string Ids = "ids";
    public const string PreexistingKey = "taken";

    public async ValueTask<CommandOutcome<string>> HandleAsync(KitViolation request, ICommandContext context)
    {
        switch (request.Case)
        {
            case WriteOutsideTouches:
                context.Set(KitProbeModule.Ledger, new KitLedger { Balance = 1 });
                break;
            case ReadOutsideDeclared:
                context.Get<KitOther>(KitProbeModule.Other);
                break;
            case WrongStateType:
                context.Get<KitLedger>(KitProbeModule.Notes);
                break;
            case ClaimOverLimit:
                await context.TryClaimAsync(KitProbeModule.ClaimCollection, "a");
                await context.TryClaimAsync(KitProbeModule.ClaimCollection, "b");
                break;
            case ClaimInReceipts:
                await context.TryClaimAsync("receipts", "a");
                break;
            case ClaimInAggregate:
                await context.TryClaimAsync("pa_x", "a");
                break;
            case DuplicateClaim:
                var first = await context.TryClaimAsync(KitProbeModule.ClaimCollection, "dup");
                var second = await context.TryClaimAsync(KitProbeModule.ClaimCollection, "dup");
                return Ok($"{first}/{second}");
            case PreexistingClaim:
                return Ok((await context.TryClaimAsync(KitProbeModule.ClaimCollection, PreexistingKey)).ToString());
            case SetThenGet:
                var before = context.Get<KitNotes>(KitProbeModule.Notes);
                context.Set(KitProbeModule.Notes, before with { Text = "after", Count = before.Count + 1 });
                var after = context.Get<KitNotes>(KitProbeModule.Notes);
                var ledger = context.Get<KitLedger>(KitProbeModule.Ledger);
                return Ok($"{before.Text}|{before.Count}|{after.Text}|{after.Count}|{ledger.Balance}");
            case Ids:
                return Ok(string.Join(';', request.Purposes.Split(',').Select(p => $"{p}={context.NewId(p)}|{context.NewSeed(p)}")));
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Case, "Unknown case.");
        }

        return Ok("no violation");
    }

    private static CommandOutcome<string> Ok(string result) => CommandOutcome<string>.Ok(result);
}
