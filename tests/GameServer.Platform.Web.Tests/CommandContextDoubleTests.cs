using GameServer.Actors;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T030 (FR-020..FR-024, FR-026, US2-4, US2-5, US2-7): the command-context double — build errors, defaults, seeds,
/// recording, frozen configuration and the commit check with the runner's messages.
/// </summary>
public sealed class CommandContextDoubleTests
{
    private const string DefaultRequestId = "00000000-0000-4000-8000-000000000001";

    private static CommandContextDouble Save() => CommandContextDouble.Create(KitProbeModule.SaveOperation, new IdentityModule(), new KitProbeModule());

    private static async Task<ICommandOutcome> RunSaveAsync(CommandContextDouble context, KitSave request) =>
        await new KitSaveHandler().HandleAsync(request, context);

    [Fact]
    public void An_unknown_operation_names_it_and_the_registered_operations()
    {
        var ex = Should.Throw<ArgumentException>(() => CommandContextDouble.Create("kit.nope", new KitProbeModule()));

        ex.Message.ShouldStartWith(
            "Operation 'kit.nope' is not registered by the given modules. Registered operations: kit.gm_edit, kit.save, kit.violate. " +
            "Fix: pass the module that registers the command.");
    }

    [Fact]
    public void A_declared_aggregate_without_its_module_names_the_aggregate()
    {
        var ex = Should.Throw<ArgumentException>(() => CommandContextDouble.Create(KitExtensionModule.Operation, new KitExtensionModule()));

        ex.Message.ShouldStartWith(
            "Command 'kit_ext.touch' declares aggregate 'kit_notes' in Touches or Reads, but no given module registers it. " +
            "Fix: pass the module that registers it.");
        CommandContextDouble.Create(KitExtensionModule.Operation, new KitExtensionModule(), new KitProbeModule()).Descriptor.Operation
            .ShouldBe(KitExtensionModule.Operation);
    }

    [Fact]
    public void Defaults_are_the_test_player_fixed_time_empty_config_and_the_runner_receipt_id()
    {
        var context = Save();

        context.ReceiptId.ShouldBe(ReceiptIds.Compute("p_test", "p_test", KitProbeModule.SaveOperation, DefaultRequestId));
        context.Actor.ShouldBe(new ActorContext("p_test", ActorType.Player, "p_test", "test", "test", context.Actor.Permissions, DefaultRequestId));
        context.Actor.Permissions.ShouldBeEmpty();
        context.Now.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        context.Config.ShouldBeSameAs(TestGameConfig.Empty);
        context.ConfigVersion.ShouldBe(TestGameConfig.Empty.ConfigVersion);
        context.Descriptor.ShouldBeSameAs(KitProbeModule.SaveDescriptor);
    }

    [Fact]
    public void A_gm_command_defaults_to_the_test_staff_holding_its_permission_on_the_test_player()
    {
        var context = CommandContextDouble.Create(KitProbeModule.GmEditOperation, new KitProbeModule());

        context.Actor.ActorId.ShouldBe("staff_test");
        context.Actor.ActorType.ShouldBe(ActorType.Gm);
        context.Actor.PlayerId.ShouldBe("p_test");
        context.Actor.Permissions.ShouldBe([KitProbeModule.Permission]);
        context.ReceiptId.ShouldBe(ReceiptIds.Compute("staff_test", "p_test", KitProbeModule.GmEditOperation, DefaultRequestId));
    }

    [Fact]
    public void Configuration_sets_actor_time_config_and_ids()
    {
        var config = TestGameConfig.FromJson(new Dictionary<string, string> { ["rules.json"] = "{}" }, "rules");
        var now = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero);

        var player = Save().AsPlayer("p_9").At(now).WithConfig(config).WithRequestId("r-1");
        var gm = Save().AsGm("ops", "p_7", "a", "b");
        var actor = new ActorContext("p_5", ActorType.Player, "p_5", "game", "env", new HashSet<string>(), "corr");
        var custom = Save().WithActor(actor).WithReceiptId("fixed-receipt");

        player.ReceiptId.ShouldBe(ReceiptIds.Compute("p_9", "p_9", KitProbeModule.SaveOperation, "r-1"));
        player.Actor.PlayerId.ShouldBe("p_9");
        player.Now.ShouldBe(now);
        player.ConfigVersion.ShouldBe(config.ConfigVersion);
        gm.Actor.ShouldSatisfyAllConditions(
            a => a.ActorId.ShouldBe("ops"),
            a => a.ActorType.ShouldBe(ActorType.Gm),
            a => a.PlayerId.ShouldBe("p_7"),
            a => a.Permissions.ShouldBe(["a", "b"], ignoreOrder: true));
        custom.Actor.ShouldBeSameAs(actor);
        custom.ReceiptId.ShouldBe("fixed-receipt");
        custom.NewId("x").ShouldBe(Save().WithReceiptId("fixed-receipt").NewId("x"));
    }

    [Fact]
    public void Seeds_are_returned_and_missing_aggregates_have_the_descriptor_default()
    {
        var context = Save().Seed(KitProbeModule.Notes, new KitNotes { Text = "old", Count = 2 });

        context.Get<KitNotes>(KitProbeModule.Notes).ShouldBe(new KitNotes { Text = "old", Count = 2 });
        context.Get<KitLedger>(KitProbeModule.Ledger).ShouldBe(new KitLedger { Balance = 100 });
    }

    [Fact]
    public void A_seed_outside_touches_and_reads_or_of_the_wrong_type_throws_at_seed()
    {
        Should.Throw<ArgumentException>(() => Save().Seed(KitProbeModule.Other, new KitOther()))
            .Message.ShouldStartWith("Aggregate 'kit_other' is not declared in Touches or Reads of 'kit.save' (kit_notes, kit_ledger).");
        Should.Throw<ArgumentException>(() => Save().Seed(KitProbeModule.Notes, new KitLedger()))
            .Message.ShouldStartWith("Aggregate 'kit_notes' has state type KitNotes, not KitLedger.");
    }

    [Fact]
    public void Set_then_get_returns_the_new_state()
    {
        var context = Save().Seed(KitProbeModule.Notes, new KitNotes { Text = "old", Count = 1 });

        context.Set(KitProbeModule.Notes, new KitNotes { Text = "new", Count = 2 });

        context.Get<KitNotes>(KitProbeModule.Notes).ShouldBe(new KitNotes { Text = "new", Count = 2 });
    }

    [Fact]
    public async Task Writes_outbox_and_claims_are_recorded_in_call_order()
    {
        var context = Save().Seed(KitProbeModule.Notes, new KitNotes { Text = "old", Count = 1 });

        var outcome = await RunSaveAsync(context, new KitSave("new", ClaimKey: "c1", Audit: true, Analytics: true, Sets: 2));

        outcome.IsOk.ShouldBeTrue();
        context.Writes.ShouldBe([new RecordedWrite(KitProbeModule.Notes, new KitNotes { Text = "new", Count = 3 })]);
        context.Written<KitNotes>(KitProbeModule.Notes).ShouldBe(new KitNotes { Text = "new", Count = 3 });
        context.Written<KitLedger>(KitProbeModule.Ledger).ShouldBeNull();
        context.Outbox.Select(e => e.Kind).ShouldBe([KitProbeModule.SavedKind, AuditExtensions.OutboxKind, GameServer.Analytics.AnalyticsOutbox.Kind]);
        context.Outbox.Select(e => e.EntryId).ShouldBe([$"{context.ReceiptId}:0", $"{context.ReceiptId}:1", $"{context.ReceiptId}:2"]);
        context.Outbox[0].Payload!["text"]!.GetValue<string>().ShouldBe("new");
        context.Outbox[1].Payload!["actorId"]!.GetValue<string>().ShouldBe("p_test");
        context.Outbox[1].Payload!["permission"]!.GetValue<string>().ShouldBe(KitProbeModule.Permission);
        context.Outbox[2].Payload!["event"]!.GetValue<string>().ShouldBe("kit_saved");
        context.Claims.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Collection.ShouldBe(KitProbeModule.ClaimCollection),
            c => c.Key.ShouldBe("c1"),
            c => c.Attributes.ShouldBe(new Dictionary<string, string> { ["text"] = "new" }));
    }

    [Fact]
    public async Task A_seeded_claim_answers_false_and_is_not_recorded()
    {
        var context = Save().SeedClaim(KitProbeModule.ClaimCollection, "c1");

        var outcome = await RunSaveAsync(context, new KitSave("new", ClaimKey: "c1"));

        outcome.IsOk.ShouldBeFalse();
        outcome.Error.ShouldBe(KitProbeModule.Partial);
        context.Claims.ShouldBeEmpty();
        (await context.TryClaimAsync(KitProbeModule.ClaimCollection, "c2")).ShouldBeTrue();
        context.Claims.Select(c => c.Key).ShouldBe(["c2"]);
        context.Claims[0].Attributes.ShouldBeEmpty();
    }

    [Fact]
    public void Configuration_after_the_first_context_call_throws()
    {
        var context = Save();
        _ = context.Now;

        var ex = Should.Throw<InvalidOperationException>(() => context.AsPlayer("p_2"));

        ex.Message.ShouldBe("CommandContextDouble for 'kit.save' is already in use: configure the double before running the handler.");
        Should.Throw<InvalidOperationException>(() => context.Seed(KitProbeModule.Notes, new KitNotes()));
        Should.Throw<InvalidOperationException>(() => context.SeedClaim(KitProbeModule.ClaimCollection, "c"));
        Should.Throw<InvalidOperationException>(() => context.WithReceiptId("r"));
    }

    [Fact]
    public async Task Check_commit_refuses_an_uncatalogued_code_with_the_runner_message()
    {
        var notListed = new ErrorCode(24050, "KIT_NOT_LISTED", 200, false, "Not listed.");
        var context = Save();

        var violations = context.CheckCommit(await RunSaveAsync(context, new KitSave("x", CommitCode: notListed)));

        violations.ShouldBe([
            "Command 'kit.save' (module 'kit') committed with error code KIT_NOT_LISTED(24050), which is not in the error-code catalog; " +
            "the client would receive OK. Fix: list the code in the module's ErrorCodeSet (ModuleDescriptor.ErrorCodes) (API-11).",
        ]);
    }

    [Fact]
    public async Task Check_commit_refuses_a_catalogued_name_with_another_instance()
    {
        var context = Save();

        var violations = context.CheckCommit(await RunSaveAsync(context, new KitSave("x", CommitCode: KitProbeModule.Partial with { Number = 24009 })));

        violations.ShouldBe([
            "Command 'kit.save' (module 'kit') committed with error code KIT_PARTIAL(24009), which has the name of the catalogued KIT_PARTIAL(24001) " +
            "but differs from it (number, HTTP status, retryable flag or message). " +
            "Fix: commit with the catalogued ErrorCode instance from the module's ErrorCodeSet (API-11).",
        ]);
    }

    [Fact]
    public async Task Check_commit_refuses_a_non_200_commit_code()
    {
        var context = Save();

        var violations = context.CheckCommit(await RunSaveAsync(context, new KitSave("x", CommitCode: KitProbeModule.Conflict)));

        violations.ShouldBe([
            "Command 'kit.save' (module 'kit') committed with error code KIT_CONFLICT(24002), whose HTTP status is 409; " +
            "a commit always answers HTTP 200. Fix: use CommandOutcome.Reject for non-200 codes (nothing is committed) (API-11).",
        ]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Check_commit_refuses_a_gm_command_without_exactly_one_audit(int audits)
    {
        var context = CommandContextDouble.Create(KitProbeModule.GmEditOperation, new KitProbeModule());

        var violations = context.CheckCommit(await new KitGmEditHandler().HandleAsync(audits, context));

        violations.ShouldBe([
            $"Command 'kit.gm_edit' (Gm) wrote {audits} audit entries; expected 1 (SEC-14). " +
            "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.",
        ]);
    }

    [Fact]
    public async Task Check_commit_refuses_an_audit_in_a_non_gm_command()
    {
        var context = Save();

        var violations = context.CheckCommit(await RunSaveAsync(context, new KitSave("x", Audit: true)));

        violations.ShouldBe([
            "Command 'kit.save' (Player) wrote 1 audit entries; expected 0 (SEC-14). " +
            "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.",
        ]);
    }

    [Fact]
    public async Task Rejections_and_committable_outcomes_have_no_violation()
    {
        var rejected = Save();
        var rejection = await RunSaveAsync(rejected, new KitSave("x", RejectCode: KitProbeModule.Conflict, Audit: true));
        var committed = Save();
        var gm = CommandContextDouble.Create(KitProbeModule.GmEditOperation, new KitProbeModule());

        rejected.CheckCommit(rejection).ShouldBeEmpty();
        committed.CheckCommit(await RunSaveAsync(committed, new KitSave("x", CommitCode: KitProbeModule.Partial, Analytics: true))).ShouldBeEmpty();
        gm.CheckCommit(await new KitGmEditHandler().HandleAsync(1, gm)).ShouldBeEmpty();
    }

    /// <summary>
    /// FR-026: Touches and claim limits are enforced by the shared context while the handler runs (FR-023), so CheckCommit does
    /// not repeat them — the handler fails before any commit check could run.
    /// </summary>
    [Fact]
    public async Task A_limit_violation_throws_during_the_handler_before_any_commit_check()
    {
        var context = CommandContextDouble.Create(KitProbeModule.ViolateOperation, new KitProbeModule());

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new KitViolationHandler().HandleAsync(new KitViolation(KitViolationHandler.ClaimOverLimit), context));

        ex.Message.ShouldBe("Command 'kit.violate' exceeded MaxClaimMarkers = 1 (DAT-04).");
        context.Claims.Select(c => c.Key).ShouldBe(["a"]);
    }
}
