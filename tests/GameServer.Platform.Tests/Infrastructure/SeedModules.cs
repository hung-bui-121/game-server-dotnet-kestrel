using System.Collections.Concurrent;
using GameServer.Accounts;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Modules.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Tests.Infrastructure;

/// <summary>State of the seed test aggregates.</summary>
public sealed record SeedState
{
    public long Value { get; init; }

    public string Blob { get; init; } = string.Empty;
}

public static class SeedAggregates
{
    public const string A = "seed_a";
    public const string B = "seed_b";
    public const int AHardBytes = 2048;

    public static PlayerAggregateDescriptor<SeedState> ADescriptor { get; } =
        new(A, 1, clientVisible: true, () => new SeedState(), warnBytes: 1024, hardBytes: AHardBytes);

    public static PlayerAggregateDescriptor<SeedState> BDescriptor { get; } = new(B, 1, clientVisible: false, () => new SeedState());
}

public static class SeedErrors
{
    public static readonly ErrorCode Rejected = new(20201, "SEED_REJECTED", 200, false, "Seed refused.");

    public static ErrorCodeSet All { get; } = new(20200, 20299, Rejected);
}

/// <summary>What an initializer of the seed module does in one run; null = OK.</summary>
public delegate ValueTask<ErrorCode?> SeedStep(ICommandContext context);

/// <summary>The steps of the seed initializers (by initializer name) and what they saw, shared through DI.</summary>
public sealed class SeedScript
{
    private readonly ConcurrentDictionary<string, int> _invocations = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, SeedStep> Steps { get; } = new(StringComparer.Ordinal);

    /// <summary>Lines recorded by steps (e.g. ids drawn), in run order.</summary>
    public ConcurrentQueue<string> Trace { get; } = new();

    /// <summary>Constructor dependencies the initializers received.</summary>
    public ConcurrentQueue<object> Dependencies { get; } = new();

    public int Invocations(string initializer) => _invocations.GetValueOrDefault(initializer);

    internal ValueTask<ErrorCode?> RunAsync(string initializer, ICommandContext context)
    {
        _invocations.AddOrUpdate(initializer, 1, (_, n) => n + 1);
        return Steps.TryGetValue(initializer, out var step) ? step(context) : ValueTask.FromResult<ErrorCode?>(null);
    }
}

/// <summary>A DI service an initializer takes by constructor (research R3).</summary>
public sealed class SeedDependency;

/// <summary>Initializer <see cref="SeedModules.First"/>.</summary>
public sealed class FirstSeedInitializer(SeedScript script, SeedDependency dependency) : IAccountInitializer
{
    public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
    {
        script.Dependencies.Enqueue(dependency);
        return script.RunAsync(SeedModules.First, context);
    }
}

/// <summary>Initializer <see cref="SeedModules.Second"/>.</summary>
public sealed class SecondSeedInitializer(SeedScript script) : IAccountInitializer
{
    public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context) => script.RunAsync(SeedModules.Second, context);
}

/// <summary>Slice 016: a pure-Platform module with account initializers for <c>AccountWriter</c> tests (no Economy, no web).</summary>
public static class SeedModules
{
    public const string ModuleName = "seed";
    public const string First = "seed.first";
    public const string Second = "seed.second";
    public const string ClaimCollection = "seed_claims";
    public const string OutboxKind = "seed_event";

    /// <summary>The identity mapping collection, the creation anchor of a login.</summary>
    public static DescriptorModule Anchors() =>
        new(new ModuleDescriptor
        {
            Name = "anchors",
            Collections = [new CollectionSpec(IdentityCollections.Identities, Deletion: DeletionTreatment.NoPlayerData)],
        });

    public static AccountInitializerRegistration FirstOf(IReadOnlyList<string> touches, int maxClaimMarkers = 0) =>
        AccountInitializerRegistration.Of<FirstSeedInitializer>(First, touches, maxClaimMarkers);

    public static AccountInitializerRegistration SecondOf(IReadOnlyList<string> touches, int maxClaimMarkers = 0) =>
        AccountInitializerRegistration.Of<SecondSeedInitializer>(Second, touches, maxClaimMarkers);

    public static DescriptorModule Module(SeedScript script, params AccountInitializerRegistration[] initializers) =>
        new(
            new ModuleDescriptor
            {
                Name = ModuleName,
                Aggregates = [SeedAggregates.ADescriptor, SeedAggregates.BDescriptor],
                ErrorCodes = SeedErrors.All,
                Collections = [new CollectionSpec(ClaimCollection, Deletion: DeletionTreatment.Anonymize)],
                AccountInitializers = initializers,
            },
            services =>
            {
                services.AddSingleton(script);
                services.AddSingleton<SeedDependency>();
            });

    /// <summary>Adds <paramref name="value"/> to <paramref name="aggregateId"/>.</summary>
    public static void Add(ICommandContext context, string aggregateId, long value, string blob = "")
    {
        var state = context.Get<SeedState>(aggregateId);
        context.Set(aggregateId, state with { Value = state.Value + value, Blob = blob });
    }
}
