using System.Reflection;
using GameServer.Commands;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Observability;
using GameServer.Platform.Observability.Analytics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using GameServer.TestKit;
using NetArchTest.Rules;
using Sample.Counter;
using Sample.Counter.Adapters;
using SampleGame;
using SampleGame.Adapters;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Dependency rules of the requirements (ARC-01, ARC-02, ARC-05, CMD-05, SEC-08, PERF-01).
/// Banned APIs (.Result, .Wait(), DateTime.UtcNow) are enforced at compile time by BannedApiAnalyzers.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Core = typeof(ICommandHandler<,>).Assembly;
    private static readonly Assembly Platform = typeof(CommandRunner).Assembly;
    private static readonly Assembly PlatformWeb = typeof(GameServerBuilder).Assembly;
    private static readonly Assembly PlatformCouchbase = typeof(CouchbaseDocumentStore).Assembly;
    private static readonly Assembly PlatformTesting = typeof(InMemoryDocumentStore).Assembly;
    private static readonly Assembly PlatformObservability = typeof(ObservabilityModule).Assembly;
    private static readonly Assembly IdentityBusiness = typeof(ProfileState).Assembly;
    private static readonly Assembly IdentityAdapters = typeof(IdentityModule).Assembly;
    private static readonly Assembly CounterBusiness = typeof(IncrementHandler).Assembly;
    private static readonly Assembly CounterAdapters = typeof(CounterModule).Assembly;
    private static readonly Assembly EconomyBusiness = typeof(EconomyOperations).Assembly;
    private static readonly Assembly EconomyAdapters = typeof(EconomyModule).Assembly;
    private static readonly Assembly IapBusiness = typeof(IapPurchaseHandler).Assembly;
    private static readonly Assembly IapAdapters = typeof(IapModule).Assembly;
    private static readonly Assembly GameBusiness = typeof(EndBattleHandler).Assembly;
    private static readonly Assembly GameAdapters = typeof(SampleGameModule).Assembly;
    private static readonly Assembly GmBusiness = typeof(StaffRecord).Assembly;
    private static readonly Assembly GmAdapters = typeof(GmModule).Assembly;
    private static readonly Assembly TestKit = typeof(CommandContextDouble).Assembly;

    private static readonly Assembly[] BusinessAssemblies = [Core, IdentityBusiness, CounterBusiness, EconomyBusiness, IapBusiness, GameBusiness, GmBusiness];

    private static readonly Assembly[] ModuleAssemblies =
        [IdentityBusiness, IdentityAdapters, CounterBusiness, CounterAdapters, EconomyBusiness, EconomyAdapters, IapBusiness, IapAdapters, GameBusiness, GameAdapters, GmBusiness, GmAdapters];

    private static readonly Assembly[] AllFrameworkAndModules =
        [Core, Platform, PlatformWeb, PlatformCouchbase, PlatformTesting, PlatformObservability, TestKit, .. ModuleAssemblies];

    private static void ShouldPass(ConditionList rule, string because)
    {
        var result = rule.GetResult();
        result.IsSuccessful.ShouldBeTrue($"{because}. Violations: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Rules_engine_detects_real_dependencies()
    {
        // Guards against vacuous passes: these dependencies exist, so the "should not" rules must be able to see them.
        Types.InAssembly(PlatformCouchbase).Should().HaveDependencyOn("Couchbase").GetResult().IsSuccessful.ShouldBeFalse();
        Types.InAssembly(PlatformCouchbase).That().HaveName(nameof(CouchbaseDocumentStore))
            .ShouldNot().HaveDependencyOn("Couchbase").GetResult().IsSuccessful.ShouldBeFalse();
        Types.InAssembly(Platform).That().HaveName(nameof(CommandRunner))
            .ShouldNot().HaveDependencyOn(typeof(ITransactionContext).FullName!).GetResult().IsSuccessful.ShouldBeFalse();

        // Slice 016 (research R16): the account creation writer is a transactional write path too.
        Types.InAssembly(Platform).That().HaveName("AccountWriter")
            .ShouldNot().HaveDependencyOn(typeof(ITransactionContext).FullName!).GetResult().IsSuccessful.ShouldBeFalse();
    }

    [Fact]
    public void Login_writes_accounts_only_through_the_account_writer()
    {
        // Slice 016 T021 (FR-011, constitution I): every login writes the profile through IAccountWriter, which decides
        // create-or-update per attempt; the one-aggregate writer would bring back the pre-read decision (trap T3).
        LoginWriteDependencyViolations(typeof(LoginCompletion)).ShouldBeEmpty();
        ShouldPass(
            Types.InAssembly(IdentityAdapters).That().HaveName(nameof(LoginCompletion))
                .ShouldNot().HaveDependencyOn(typeof(GameServer.Platform.Aggregates.IPlayerAggregateWriter).FullName!),
            "LoginCompletion must not use IPlayerAggregateWriter (the login write is IAccountWriter's)");
    }

    /// <summary>What keeps <paramref name="login"/> from being the account writer's caller only: its constructor and fields.</summary>
    internal static IReadOnlyList<string> LoginWriteDependencyViolations(Type login)
    {
        var parameters = login.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToArray();
        var fields = login.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.FieldType).ToArray();
        var violations = new List<string>();
        if (!parameters.Contains(typeof(IAccountWriter)))
        {
            violations.Add($"{login.Name} does not take {nameof(IAccountWriter)}");
        }

        if (parameters.Concat(fields).Contains(typeof(GameServer.Platform.Aggregates.IPlayerAggregateWriter)))
        {
            violations.Add($"{login.Name} depends on {nameof(GameServer.Platform.Aggregates.IPlayerAggregateWriter)}");
        }

        return violations;
    }

    [Fact]
    public void Core_and_business_projects_are_independent_of_web_database_wire_and_logging()
    {
        foreach (var assembly in BusinessAssemblies)
        {
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(
                    "Microsoft.AspNetCore", "Couchbase", "Google.Protobuf", "Serilog", "GameServer.Platform"),
                $"{assembly.GetName().Name} must not depend on HTTP, Couchbase, Protobuf, Serilog or Platform (ARC-01)");
        }
    }

    [Fact]
    public void Only_the_observability_project_uses_serilog_and_opentelemetry()
    {
        foreach (var assembly in AllFrameworkAndModules.Where(a => a != PlatformObservability))
        {
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny("Serilog", "OpenTelemetry"),
                $"{assembly.GetName().Name} must log through Microsoft.Extensions.Logging and instrument with ActivitySource/Meter only (OBS-04)");
        }
    }

    [Fact]
    public void Only_the_couchbase_adapter_uses_the_couchbase_sdk()
    {
        foreach (var assembly in AllFrameworkAndModules.Where(a => a != PlatformCouchbase))
        {
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().HaveDependencyOn("Couchbase"),
                $"{assembly.GetName().Name} must not call the Couchbase SDK (CMD-05)");
        }
    }

    [Fact]
    public void Only_platform_writes_through_transactions()
    {
        foreach (var assembly in AllFrameworkAndModules.Where(a => a != Platform && a != PlatformCouchbase && a != PlatformTesting))
        {
            // Slice 013 T032: the kit's detached stub is the transaction behind the runner's own context in handler tests (no store).
            var types = assembly == TestKit
                ? Types.InAssembly(assembly).That().DoNotHaveName("DetachedTransaction").ShouldNot()
                : Types.InAssembly(assembly).ShouldNot();
            ShouldPass(
                types.HaveDependencyOn(typeof(ITransactionContext).FullName!),
                $"{assembly.GetName().Name} must not open transaction contexts: pa_* and receipts are written only by the runner (CMD-05, DAT-09)");
        }
    }

    [Fact]
    public void Inside_platform_only_the_transactional_write_paths_use_transaction_contexts()
    {
        // Runner and account creation writer (Commands), aggregate writer (Aggregates), stores (Persistence) and the purge
        // writer (Privacy) only: keeps jobs and future platform code off the transactional write path (DAT-09, slices 010, 016).
        ShouldPass(
            Types.InAssembly(Platform)
                .That().DoNotResideInNamespaceMatching(@"^GameServer\.Platform\.(Commands|Aggregates|Persistence|Privacy)(\..*)?$")
                .ShouldNot().HaveDependencyOn(typeof(ITransactionContext).FullName!),
            "GameServer.Platform types outside Commands, Aggregates, Persistence and Privacy must not depend on ITransactionContext");
    }

    [Fact]
    public void Only_the_purge_job_and_platform_privacy_code_use_the_purge_writer()
    {
        // The writer bypasses the runner and rewrites ledger/audit (CMD-08/SEC-14 exception): keep its callers to the purge
        // pipeline (Platform.Privacy), the identity purge job and the composition root that registers it (review minor 9).
        static bool Allowed(Type type)
        {
            var outer = type;
            while (outer.DeclaringType is { } declaring)
            {
                outer = declaring; // async state machines and closures are nested types
            }

            return outer.Namespace == "GameServer.Platform.Privacy" || outer == typeof(AccountPurgeJob) || outer == typeof(GameServerBuilderExtensions);
        }

        var writer = typeof(GameServer.Platform.Privacy.IAccountPurgeWriter).FullName!;
        var users = AllFrameworkAndModules.SelectMany(a => Types.InAssembly(a).That().HaveDependencyOn(writer).GetTypes()).ToArray();

        users.ShouldContain(t => t == typeof(AccountPurgeJob), "rule must see the purge job's dependency");
        users.Where(t => !Allowed(t)).Select(t => t.FullName).ShouldBeEmpty("only Platform.Privacy, AccountPurgeJob and the DI registration may depend on IAccountPurgeWriter");
    }

    [Fact]
    public void Only_the_testing_assembly_knows_the_file_store()
    {
        // The development-only file store (slice 011) is chosen by hosts; framework and module code never depends on it.
        Types.InAssembly(PlatformTesting).That().HaveDependencyOn(typeof(FileDocumentStore).FullName!).GetTypes()
            .ShouldContain(typeof(FileStoreServiceCollectionExtensions), "rule must see a real dependency");
        foreach (var assembly in AllFrameworkAndModules.Where(a => a != PlatformTesting))
        {
            // Slice 013 T032: the kit's TestHost builds the file store a test asks for (TestStoreKind.File) through kit-internal
            // TestStores code; its public surface never exposes it (FR-014).
            var types = assembly == TestKit
                ? Types.InAssembly(assembly).That().DoNotHaveName(nameof(TestHost)).And().DoNotHaveName(nameof(TestStores)).ShouldNot()
                : Types.InAssembly(assembly).ShouldNot();
            ShouldPass(
                types.HaveDependencyOn(typeof(FileDocumentStore).FullName!),
                $"{assembly.GetName().Name} must not depend on the development-only file store (FR-007)");
        }
    }

    [Fact]
    public void Only_observability_knows_the_analytics_file_sink()
    {
        // Slice 012 D19: hosts choose the file sink (AddAnalyticsFiles); framework and module code only see IAnalyticsSink.
        Types.InAssembly(PlatformObservability).That().HaveDependencyOn(typeof(AnalyticsFileSink).FullName!).GetTypes()
            .ShouldContain(typeof(AnalyticsFileExtensions), "rule must see a real dependency");
        foreach (var assembly in AllFrameworkAndModules.Where(a => a != PlatformObservability))
        {
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(typeof(AnalyticsFileSink).FullName!),
                $"{assembly.GetName().Name} must not depend on the analytics file sink (FR-005, D19)");
        }
    }

    [Fact]
    public void Modules_do_not_touch_the_document_store_directly()
    {
        foreach (var assembly in ModuleAssemblies)
        {
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(typeof(ITransactionalDocumentStore).FullName!),
                $"{assembly.GetName().Name} must go through the command runner or snapshot reader (CMD-05)");
        }
    }

    [Fact]
    public void Business_projects_do_not_depend_on_their_adapters_or_other_modules()
    {
        ShouldPass(
            Types.InAssembly(CounterBusiness).ShouldNot().HaveDependencyOnAny("Sample.Counter.Adapters", "GameServer.Modules.Identity"),
            "Module business code depends only on Core and its own contracts (ARC-05)");
    }

    [Fact]
    public void No_mvc_controllers()
    {
        foreach (var assembly in AllFrameworkAndModules)
        {
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().Inherit(typeof(Microsoft.AspNetCore.Mvc.ControllerBase)),
                "Minimal API with explicit mapping only; no controllers or application parts (ARC-07)");
        }
    }

    [Fact]
    public void Command_handlers_receive_domain_types_not_wire_messages()
    {
        var handlerInterface = typeof(ICommandHandler<,>);
        var offenders = AllFrameworkAndModules
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface)
            .SelectMany(i => i.GetGenericArguments())
            .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t))
            .ToArray();

        offenders.ShouldBeEmpty("adapters map wire messages to domain models before calling use cases (ARC-02)");
    }
}
