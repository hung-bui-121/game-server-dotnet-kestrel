using System.Reflection;
using GameServer.Commands;
using GameServer.Modules.Economy;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
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

    private static readonly Assembly[] BusinessAssemblies = [Core, IdentityBusiness, CounterBusiness, EconomyBusiness, IapBusiness, GameBusiness];

    private static readonly Assembly[] ModuleAssemblies =
        [IdentityBusiness, IdentityAdapters, CounterBusiness, CounterAdapters, EconomyBusiness, EconomyAdapters, IapBusiness, IapAdapters, GameBusiness, GameAdapters];

    private static readonly Assembly[] AllFrameworkAndModules =
        [Core, Platform, PlatformWeb, PlatformCouchbase, PlatformTesting, .. ModuleAssemblies];

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
            ShouldPass(
                Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(typeof(ITransactionContext).FullName!),
                $"{assembly.GetName().Name} must not open transaction contexts: pa_* and receipts are written only by the runner (CMD-05, DAT-09)");
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
