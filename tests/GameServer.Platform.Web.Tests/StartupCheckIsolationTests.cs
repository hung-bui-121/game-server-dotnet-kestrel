using GameServer.Platform.Config;
using GameServer.Platform.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-030, FR-031, BC-10 (slice 015 T029): every check is created from its registration inside its own isolation.</summary>
public sealed class StartupCheckIsolationTests
{
    private const string CreatedFix = ". Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).";

    [Fact]
    public void A_throwing_constructor_is_named_by_its_type_and_does_not_hide_the_other_checks()
    {
        var result = Run(s =>
        {
            s.AddSingleton<IStartupCheck, ThrowingConstructorCheck>();
            s.AddSingleton<IStartupCheck>(new ReportingCheck("[test] plain ran"));
        });

        result.Errors.ShouldBe([
            $"[{nameof(ThrowingConstructorCheck)}] the startup check could not be created: InvalidOperationException: boom{CreatedFix}",
            "[test] plain ran",
        ]);
    }

    [Fact]
    public void A_missing_dependency_is_a_creation_line_naming_the_type()
    {
        var result = Run(s => s.AddSingleton<IStartupCheck, NeedsUnregisteredCheck>());

        var line = result.Errors.ShouldHaveSingleItem();
        line.ShouldStartWith($"[{nameof(NeedsUnregisteredCheck)}] the startup check could not be created: InvalidOperationException: ");
        line.ShouldContain(nameof(UnregisteredService));
        line.ShouldEndWith(CreatedFix);
    }

    [Fact]
    public void A_lambda_factory_is_named_by_its_declaring_type_not_the_closure_class()
    {
        var cause = "from a closure";
        var result = Run(s =>
        {
            s.AddSingleton<IStartupCheck>(_ => throw new InvalidOperationException("static lambda"));
            s.AddSingleton<IStartupCheck>(_ => throw new InvalidOperationException(cause));
        });

        result.Errors.ShouldBe([
            $"[{nameof(StartupCheckIsolationTests)}] the startup check could not be created: InvalidOperationException: static lambda{CreatedFix}",
            $"[{nameof(StartupCheckIsolationTests)}] the startup check could not be created: InvalidOperationException: from a closure{CreatedFix}",
        ]);
    }

    [Fact]
    public void A_method_group_and_a_static_extension_lambda_are_named_by_their_declaring_types()
    {
        var result = Run(s =>
        {
            s.AddSingleton<IStartupCheck>(CheckFactories.Throwing);
            s.AddFailingCheck();
        });

        result.Errors.ShouldBe([
            $"[{nameof(CheckFactories)}] the startup check could not be created: InvalidOperationException: method group{CreatedFix}",
            $"[{nameof(CheckRegistrationExtensions)}] the startup check could not be created: InvalidOperationException: extension{CreatedFix}",
        ]);
    }

    [Fact]
    public void A_throwing_check_of_an_instance_is_named_by_its_runtime_type()
    {
        var result = Run(s => s.AddSingleton<IStartupCheck>(new ThrowingRunCheck()));

        result.Errors.ShouldBe([$"[{nameof(ThrowingRunCheck)}] the startup check failed: InvalidOperationException: run"]);
    }

    [Fact]
    public void An_unmarked_startup_exception_from_check_gives_its_violations()
    {
        var result = Run(s => s.AddSingleton<IStartupCheck>(new RefusingCheck("[test] refused")));

        result.Errors.ShouldBe(["[test] refused"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_game_config_marker_gives_one_not_run_line(bool atCreation)
    {
        var result = Run(s => Marked(s, Unavailable.GameConfig, atCreation));

        result.Errors.ShouldBe([
            $"[{(atCreation ? nameof(StartupCheckIsolationTests) : nameof(MarkedRunCheck))}] the startup check did not run: the game config did not load " +
            "(see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).",
        ]);
        result.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_catalog_marker_gives_one_not_run_line_naming_the_catalog(bool atCreation)
    {
        var result = Run(s => Marked(s, Unavailable.Catalog(typeof(SampleCatalog)), atCreation));

        result.Errors.ShouldBe([
            $"[{(atCreation ? nameof(StartupCheckIsolationTests) : nameof(MarkedRunCheck))}] the startup check did not run: catalog SampleCatalog was not built " +
            "(see the lines above). Fix: take GameConfigStatus and read the catalog with TryGetCatalog (FG-023).",
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_provision_marker_is_a_warning_not_an_error(bool provision)
    {
        var result = Run(s => Marked(s, Unavailable.Provision, atCreation: true), provision ? StartupCheckMode.Provision : StartupCheckMode.Serve);

        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldBe([
            $"[{nameof(StartupCheckIsolationTests)}] the startup check did not run: provision mode does not load the game config. " +
            "Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).",
        ]);
    }

    [Fact]
    public void In_provision_mode_any_unavailable_config_is_a_warning()
    {
        var result = Run(s => Marked(s, Unavailable.GameConfig, atCreation: true), StartupCheckMode.Provision);

        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("provision mode does not load the game config");
    }

    [Fact]
    public void Each_check_is_created_once_per_run_and_not_from_the_container()
    {
        var counter = new CreationCounter();
        var services = Collection(s =>
        {
            s.AddSingleton(counter);
            s.AddSingleton<IStartupCheck, CountingCheck>();
            s.AddSingleton<IStartupCheck>(sp => new CountingCheck(sp.GetRequiredService<CreationCounter>()));
        });
        using var provider = services.BuildServiceProvider();

        GameServerApplicationExtensions.CheckAll(provider, StartupCheckMode.Serve).Errors.ShouldBe(["counted", "counted"]);
        counter.Created.ShouldBe(2);
        GameServerApplicationExtensions.CheckAll(provider, StartupCheckMode.Serve);
        counter.Created.ShouldBe(4);
    }

    // ---- slice 015 T071 m4, m5 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_constructor_that_throws_a_startup_exception_gives_its_violations_and_the_other_checks_run(bool provision)
    {
        var result = Run(
            s =>
            {
                s.AddSingleton<IStartupCheck, RefusingConstructorCheck>();
                s.AddSingleton<IStartupCheck>(new ReportingCheck("[test] plain ran"));
            },
            provision ? StartupCheckMode.Provision : StartupCheckMode.Serve);

        result.Errors.ShouldBe(["[mycheck] bad setting", "[test] plain ran"]);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Factories_returning_a_concrete_check_are_named_by_it_and_an_interface_factory_by_its_declaring_type()
    {
        var result = Run(s =>
        {
            s.AddSingleton<IStartupCheck, ThrowingRunCheck>(_ => new ThrowingRunCheck());
            s.AddSingleton<IStartupCheck>(CheckFactories.Concrete);
            s.AddSingleton<IStartupCheck>(_ => new ThrowingRunCheck());
            s.AddSingleton<IStartupCheck, ThrowingConstructorCheck>(_ => new ThrowingConstructorCheck());
        });

        result.Errors.ShouldBe([
            $"[{nameof(ThrowingRunCheck)}] the startup check failed: InvalidOperationException: run",
            $"[{nameof(ThrowingRunCheck)}] the startup check failed: InvalidOperationException: run",
            $"[{nameof(ThrowingRunCheck)}] the startup check failed: InvalidOperationException: run",
            $"[{nameof(ThrowingConstructorCheck)}] the startup check could not be created: InvalidOperationException: boom{CreatedFix}",
        ]);
        var descriptors = Collection(s =>
        {
            s.AddSingleton<IStartupCheck, ThrowingRunCheck>(_ => new ThrowingRunCheck());
            s.AddSingleton<IStartupCheck>(CheckFactories.Concrete);
            s.AddSingleton<IStartupCheck>(_ => new ThrowingRunCheck());
        }).Where(d => d.ServiceType == typeof(IStartupCheck)).ToArray();
        descriptors.Select((d, i) => StartupCheckRegistrations.NameOf(d, i))
            .ShouldBe([nameof(ThrowingRunCheck), nameof(ThrowingRunCheck), nameof(StartupCheckIsolationTests)]);
    }

    [Fact]
    public void A_factory_that_returns_null_is_a_creation_line()
    {
        var result = Run(s => s.AddSingleton<IStartupCheck>(_ => null!));

        result.Errors.ShouldBe([
            $"[{nameof(StartupCheckIsolationTests)}] the startup check could not be created: InvalidOperationException: the registration's factory returned null{CreatedFix}",
        ]);
    }

    [Fact]
    public void Keyed_checks_are_not_run()
    {
        var result = Run(s => s.AddKeyedSingleton<IStartupCheck>("k", new ReportingCheck("[test] keyed")));

        result.Errors.ShouldBeEmpty();
    }

    private static void Marked(IServiceCollection services, string marker, bool atCreation)
    {
        if (atCreation)
        {
            services.AddSingleton<IStartupCheck>(_ => throw Unavailable.Mark(new GameServerStartupException("[config] cause"), marker));
        }
        else
        {
            services.AddSingleton<IStartupCheck>(new MarkedRunCheck(marker));
        }
    }

    private static StartupCheckResult Run(Action<IServiceCollection> configure, StartupCheckMode mode = StartupCheckMode.Serve)
    {
        using var provider = Collection(configure).BuildServiceProvider();
        return GameServerApplicationExtensions.CheckAll(provider, mode);
    }

    private static ServiceCollection Collection(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StartupCheckRegistrations(services));
        configure(services);
        return services;
    }

    private sealed class SampleCatalog;

    private sealed class UnregisteredService;

    private sealed class CreationCounter
    {
        public int Created { get; set; }
    }

    private sealed class ThrowingConstructorCheck : IStartupCheck
    {
        public ThrowingConstructorCheck() => throw new InvalidOperationException("boom");

        public void Check(StartupCheckResult result)
        {
        }
    }

    private sealed class RefusingConstructorCheck : IStartupCheck
    {
        public RefusingConstructorCheck() => throw new GameServerStartupException("[mycheck] bad setting");

        public void Check(StartupCheckResult result)
        {
        }
    }

    private sealed class NeedsUnregisteredCheck(UnregisteredService service) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => result.Errors.Add(service.ToString()!);
    }

    private sealed class ReportingCheck(string error) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => result.Errors.Add(error);
    }

    internal sealed class ThrowingRunCheck : IStartupCheck
    {
        public void Check(StartupCheckResult result) => throw new InvalidOperationException("run");
    }

    private sealed class RefusingCheck(string violation) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => throw new GameServerStartupException(violation);
    }

    private sealed class MarkedRunCheck(string marker) : IStartupCheck
    {
        public void Check(StartupCheckResult result) =>
            throw new InvalidOperationException("wrapper", Unavailable.Mark(new GameServerStartupException("[config] cause"), marker));
    }

    private sealed class CountingCheck : IStartupCheck
    {
        public CountingCheck(CreationCounter counter) => counter.Created++;

        public void Check(StartupCheckResult result) => result.Errors.Add("counted");
    }
}

/// <summary>A method-group factory (V4: named by this type).</summary>
internal static class CheckFactories
{
    public static IStartupCheck Throwing(IServiceProvider services) => throw new InvalidOperationException("method group");

    /// <summary>A method group returning the concrete check (m5: named by its return type).</summary>
    public static StartupCheckIsolationTests.ThrowingRunCheck Concrete(IServiceProvider services) => new();
}

/// <summary>The <c>FileStoreServiceCollectionExtensions</c> shape: a static lambda in a static extension class.</summary>
internal static class CheckRegistrationExtensions
{
    public static IServiceCollection AddFailingCheck(this IServiceCollection services) =>
        services.AddSingleton<IStartupCheck>(_ => throw new InvalidOperationException("extension"));
}
