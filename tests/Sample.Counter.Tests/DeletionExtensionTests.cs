using System.Text.Json.Nodes;
using GameServer.Modules;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Jobs;
using GameServer.Platform.Persistence;
using GameServer.Platform.Privacy;
using GameServer.Privacy;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace Sample.Counter.Tests;

/// <summary>
/// Slice 010 US5: a game module keeps its own player data deletable without touching Core (FR-019, FR-020, EXT-01): a
/// <see cref="DeletionTreatment.ModuleStep"/> collection erased by the module's <see cref="IAccountDeletionStep"/> with both
/// ids, before identities and the profile go; idempotent; a failing step keeps the purge pending; the eraser stays inside
/// the module's own collections; undeclared treatments fail startup (SC-007).
/// </summary>
public sealed class DeletionExtensionTests
{
    private const string Increment = "/api/v1/counter/increment";

    private static Task<TestHost> StartAsync(NotesProbe probe, Action<Dictionary<string, string?>>? settings = null) => TestHost.StartAsync(
        b => b.AddModule<IdentityModule>().AddModule<CounterModule>().AddModule(new NotesModule(probe)),
        s =>
        {
            s["Identity:SessionSlidingDays"] = "60";
            settings?.Invoke(s);
        });

    private static Task<JobRunResult> RunPurgeAsync(TestHost host) =>
        host.Services.GetRequiredService<LeaseJobRunner>().RunOnceAsync(AccountPurgeJob.JobName, CancellationToken.None);

    private static async Task<string> LeaverWithNotesAsync(TestHost host)
    {
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var player = host.BearerClient(login.SessionToken);
        for (var i = 0; i < 2; i++)
        {
            (await (await player.PostProtobufAsync(Increment, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId()))
                .ReadProtobufAsync<GameServer.Protocol.Core.V1.CommandResponse>()).Code.ShouldBe("OK");
        }

        host.Store.Seed(new DocumentKey(NotesModule.Collection, "note-public-" + login.PlayerId[^6..]), NotesModule.Note(login.PlayerId, "gg", isPrivate: false));
        host.Store.Seed(new DocumentKey(NotesModule.Collection, "note-private-" + login.PlayerId[^6..]), NotesModule.Note(login.PlayerId, "my address", isPrivate: true));
        (await player.PostProtobufAsync(IdentityModule.DeleteAccountRoute, new RequestAccountDeletionRequest(), ProtobufHttp.NewRequestId()))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        host.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        return login.PlayerId;
    }

    private static string Snapshot(TestHost host) =>
        string.Join('\n', host.Services.GetRequiredService<GameServer.Platform.Registry.ModuleRegistry>().RequiredCollections()
            .Where(c => c != "leases")
            .SelectMany(c => host.Store.All(c).OrderBy(d => d.Key.Key, StringComparer.Ordinal).Select(d => $"{d.Key} {d.Content.ToJsonString()}")));

    [Fact]
    public async Task The_module_step_erases_its_collection_with_both_ids_before_identities_and_the_profile_go()
    {
        var probe = new NotesProbe();
        await using var host = await StartAsync(probe);
        var playerId = await LeaverWithNotesAsync(host);
        probe.During = eraser =>
        {
            // FR-020: the step still finds the player's profile and identity mapping; receipts are already anonymized.
            probe.ProfilePresent = host.Store.Peek(new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, eraser.PlayerId)) is not null;
            probe.MappingPresent = host.Store.All(IdentityCollections.Identities).Any(d => d.Content["playerId"]?.GetValue<string>() == eraser.PlayerId);
            probe.OldIdInReceipts = host.Store.All("receipts").Any(d => d.Content["playerId"]?.GetValue<string>() == eraser.PlayerId);
            return Task.CompletedTask;
        };

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var (calledFor, anonymousId) = probe.Calls.ShouldHaveSingleItem();
        calledFor.ShouldBe(playerId);
        anonymousId.ShouldStartWith(AnonymousIds.Prefix);
        probe.ProfilePresent.ShouldBeTrue();
        probe.MappingPresent.ShouldBeTrue();
        probe.OldIdInReceipts.ShouldBeFalse();

        var notes = host.Store.All(NotesModule.Collection).Select(d => d.Content).ToArray();
        notes.ShouldHaveSingleItem()["author"]!.GetValue<string>().ShouldBe(anonymousId);
        host.Store.All(CounterModuleDefinition.LogCollection).Select(d => d.Content["playerId"]!.GetValue<string>()).ShouldAllBe(id => id == anonymousId);
        host.Store.All(CounterModuleDefinition.LogCollection).Count.ShouldBe(2);
        AccountDeletionRecord.FromJson(host.Store.Peek(AccountDeletionRecord.KeyOf(playerId))!).State.ShouldBe(AccountDeletionState.Purged);
        host.Logs.ShouldNotContain(l => l.Contains(anonymousId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_step_keeps_the_purge_pending_and_a_rerun_after_completion_changes_nothing()
    {
        var probe = new NotesProbe { Fail = true };
        await using var host = await StartAsync(probe);
        var playerId = await LeaverWithNotesAsync(host);

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var record = AccountDeletionRecord.FromJson(host.Store.Peek(AccountDeletionRecord.KeyOf(playerId))!);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.LastFailure!.Step.ShouldBe(AccountPurgeService.ModuleStep(NotesModule.StepName));
        record.CompletedSteps.ShouldNotContain(AccountPurgeService.ModuleStep(NotesModule.StepName));
        host.Store.Peek(new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, playerId)).ShouldNotBeNull();
        host.Store.All(IdentityCollections.Identities).ShouldContain(d => d.Content["playerId"]!.GetValue<string>() == playerId);
        host.Logs.ShouldContain(l => l.Contains($"failed at step {AccountPurgeService.ModuleStep(NotesModule.StepName)}", StringComparison.Ordinal));

        probe.Fail = false;
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        AccountDeletionRecord.FromJson(host.Store.Peek(AccountDeletionRecord.KeyOf(playerId))!).State.ShouldBe(AccountDeletionState.Purged);
        probe.Calls.Count.ShouldBe(2);
        probe.Calls[1].ShouldBe(probe.Calls[0]); // same player, same anonymous id on the resumed attempt
        var after = Snapshot(host);

        // US5-3: a later run finds nothing due and changes nothing.
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        Snapshot(host).ShouldBe(after);
        probe.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_eraser_refuses_foreign_collections_player_aggregates_receipts_and_foreign_outbox_kinds()
    {
        var probe = new NotesProbe();
        await using var host = await StartAsync(probe);
        var playerId = await LeaverWithNotesAsync(host);
        var refused = new List<string>();
        probe.During = async eraser =>
        {
            async Task RefusedAsync(string what, Func<Task> call)
            {
                try
                {
                    await call();
                }
                catch (InvalidOperationException)
                {
                    refused.Add(what);
                }
            }

            await RefusedAsync("foreign find", () => eraser.FindAsync(CounterModuleDefinition.LogCollection, "playerId", eraser.PlayerId, 10, CancellationToken.None));
            await RefusedAsync("foreign rewrite", () => eraser.RewriteAsync(IdentityCollections.Identities, ["x"], d => d, CancellationToken.None));
            await RefusedAsync("pa_profile", () => eraser.RewriteAsync(IdentityAggregates.ProfileDescriptor.Collection, [eraser.PlayerId], _ => null, CancellationToken.None));
            await RefusedAsync("receipts", () => eraser.FindAsync("receipts", "playerId", eraser.AnonymousId, 10, CancellationToken.None));
            await RefusedAsync("gm_audit", () => eraser.FindAsync("gm_audit", "playerId", eraser.PlayerId, 10, CancellationToken.None));
            await RefusedAsync("foreign kind", () => eraser.ScrubOutboxAsync("counter_log", n => n, CancellationToken.None));
        };

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        refused.ShouldBe(["foreign find", "foreign rewrite", "pa_profile", "receipts", "gm_audit", "foreign kind"]);
        host.Store.Peek(new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, playerId)).ShouldBeNull();
    }

    [Fact]
    public async Task Find_returns_at_most_one_batch_whatever_limit_the_step_asks_for()
    {
        // Review minor 10: a step asking for more keys than a transaction holds used to fail RewriteAsync on every retry.
        var probe = new NotesProbe { FindLimit = 100 };
        await using var host = await StartAsync(probe, s => s["GameServer:Purge:BatchSize"] = "2");
        var playerId = await LeaverWithNotesAsync(host);
        for (var i = 0; i < 5; i++)
        {
            host.Store.Seed(new DocumentKey(NotesModule.Collection, $"note-extra-{i}-" + playerId[^6..]), NotesModule.Note(playerId, "gg " + i, isPrivate: false));
        }

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        probe.FoundBatches.ShouldNotBeEmpty();
        probe.FoundBatches.ShouldAllBe(n => n <= 2);
        AccountDeletionRecord.FromJson(host.Store.Peek(AccountDeletionRecord.KeyOf(playerId))!).State.ShouldBe(AccountDeletionState.Purged);
        host.Store.All(NotesModule.Collection).Count.ShouldBe(6); // 6 public notes anonymized, the private one removed
    }

    [Fact]
    public async Task A_step_that_runs_out_of_budget_leaves_the_purge_incomplete_and_the_next_run_finishes_it()
    {
        // Review minor 10: the eraser checks the run's document budget; the step is not recorded and reruns from the start.
        var probe = new NotesProbe();
        await using var host = await StartAsync(probe, s => s["GameServer:Purge:MaxDocumentsPerRun"] = "100");
        var playerId = await LeaverWithNotesAsync(host);
        for (var i = 0; i < 150; i++)
        {
            host.Store.Seed(new DocumentKey(NotesModule.Collection, $"note-bulk-{i:D3}-" + playerId[^6..]), NotesModule.Note(playerId, "gg", isPrivate: false));
        }

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var record = AccountDeletionRecord.FromJson(host.Store.Peek(AccountDeletionRecord.KeyOf(playerId))!);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.CompletedSteps.ShouldNotContain(AccountPurgeService.ModuleStep(NotesModule.StepName));
        record.LastFailure.ShouldBeNull();
        record.NextAttemptAtUtc!.Value.ShouldBeLessThanOrEqualTo(host.Clock.UtcNow);
        host.Store.All(NotesModule.Collection).Count(d => d.Content["author"]!.GetValue<string>() == playerId).ShouldBeGreaterThan(0);

        host.Clock.Advance(TimeSpan.FromSeconds(AccountPurgeJob.DefaultIntervalSeconds + 1));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        AccountDeletionRecord.FromJson(host.Store.Peek(AccountDeletionRecord.KeyOf(playerId))!).State.ShouldBe(AccountDeletionState.Purged);
        host.Store.All(NotesModule.Collection).ShouldAllBe(d => d.Content["author"]!.GetValue<string>() != playerId);
        probe.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_collection_without_treatment_or_a_module_step_without_a_step_fails_startup_naming_module_and_collection()
    {
        var missing = await Should.ThrowAsync<Exception>(() => TestHost.StartAsync(b => b
            .AddModule<IdentityModule>()
            .AddModule<CounterModule>()
            .AddModule(new DescriptorModule(new ModuleDescriptor { Name = "notes", Collections = [new CollectionSpec(NotesModule.Collection)] }))));
        missing.ToString().ShouldContain($"[module notes] collection '{NotesModule.Collection}' declares no deletion treatment");

        var stepless = await Should.ThrowAsync<Exception>(() => TestHost.StartAsync(b => b
            .AddModule<IdentityModule>()
            .AddModule<CounterModule>()
            .AddModule(new DescriptorModule(new ModuleDescriptor
            {
                Name = "notes",
                Collections = [new CollectionSpec(NotesModule.Collection, Deletion: DeletionTreatment.ModuleStep)],
            }))));
        stepless.ToString().ShouldContain($"[module notes] collection '{NotesModule.Collection}' uses deletion treatment ModuleStep but module 'notes' declares no deletion step");
    }

    private sealed class DescriptorModule(ModuleDescriptor descriptor) : IGameModule
    {
        public ModuleDescriptor Describe() => descriptor;

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}

/// <summary>What the test step saw; switches its failure on and off.</summary>
public sealed class NotesProbe
{
    public bool Fail { get; set; }

    public List<(string PlayerId, string AnonymousId)> Calls { get; } = [];

    public Func<IPlayerDataEraser, Task>? During { get; set; }

    public bool ProfilePresent { get; set; }

    public bool MappingPresent { get; set; }

    public bool OldIdInReceipts { get; set; }

    /// <summary>Limit the step passes to <c>FindAsync</c>; null = the eraser's batch size.</summary>
    public int? FindLimit { get; set; }

    /// <summary>Key counts returned by each <c>FindAsync</c> call that found something.</summary>
    public List<int> FoundBatches { get; } = [];
}

/// <summary>A game module whose notes are erased by its own deletion step (private notes removed, public ones anonymized).</summary>
public sealed class NotesModule(NotesProbe probe) : IGameModule
{
    public const string Collection = "probe_notes";
    public const string StepName = "notes.erase";

    public static JsonObject Note(string author, string text, bool isPrivate) => new() { ["author"] = author, ["text"] = text, ["private"] = isPrivate };

    public ModuleDescriptor Describe() => new()
    {
        Name = "notes",
        DependsOn = ["identity"],
        Collections = [new CollectionSpec(Collection, Deletion: DeletionTreatment.ModuleStep, PlayerField: "author")],
        DeletionSteps = [new DeletionStepRegistration(StepName, typeof(NotesDeletionStep))],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context) => services.AddSingleton(probe);
}

public sealed class NotesDeletionStep(NotesProbe probe) : IAccountDeletionStep
{
    public async ValueTask RunAsync(IPlayerDataEraser eraser, CancellationToken cancellationToken)
    {
        probe.Calls.Add((eraser.PlayerId, eraser.AnonymousId));
        if (probe.During is { } during)
        {
            await during(eraser);
        }

        if (probe.Fail)
        {
            throw new InvalidOperationException("notes backend unavailable");
        }

        while (true)
        {
            var keys = await eraser.FindAsync(NotesModule.Collection, "author", eraser.PlayerId, probe.FindLimit ?? eraser.BatchSize, cancellationToken);
            if (keys.Count == 0)
            {
                return;
            }

            probe.FoundBatches.Add(keys.Count);

            await eraser.RewriteAsync(
                NotesModule.Collection,
                keys,
                note =>
                {
                    if (note["private"]?.GetValue<bool>() == true)
                    {
                        return null;
                    }

                    note["author"] = eraser.AnonymousId;
                    return note;
                },
                cancellationToken);
        }
    }
}
