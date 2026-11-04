using GameServer.Accounts;
using GameServer.Actors;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Commands;

/// <summary>
/// The view one account initializer gets of the creation's shared <see cref="CommandContext"/> (research R3): reads and writes
/// limited to its own touches, claim markers counted against its own limit, everything else forwarded. Initializers that declare
/// the same aggregate see each other's writes in run order because the context underneath is shared.
/// </summary>
internal sealed class InitializerContext : ICommandContext
{
    private readonly CommandContext _inner;
    private readonly AccountInitializerRegistration _registration;
    private readonly IDictionary<string, string> _writers;
    private readonly List<OutboxEntry> _outbox = [];
    private int _claims;

    /// <param name="inner">The context of the creation attempt, shared by every initializer.</param>
    /// <param name="registration">The initializer this view belongs to.</param>
    /// <param name="writers">Shared map aggregate → name of the initializer that set it last (labels a size violation).</param>
    public InitializerContext(CommandContext inner, AccountInitializerRegistration registration, IDictionary<string, string> writers)
    {
        _inner = inner;
        _registration = registration;
        _writers = writers;
        Operation = AccountCreation.InitializerOperation(registration.Name);
    }

    /// <summary><c>identity.create_account (initializer {name})</c>, the label of this initializer's rule lines.</summary>
    public string Operation { get; }

    public string InitializerName => _registration.Name;

    /// <summary>Outbox entries added through this view, in order (the audit rule of the creation).</summary>
    public IReadOnlyList<OutboxEntry> Outbox => _outbox;

    public ActorContext Actor => _inner.Actor;

    public DateTimeOffset Now => _inner.Now;

    public string ConfigVersion => _inner.ConfigVersion;

    public IGameConfig Config => _inner.Config;

    public TState Get<TState>(string aggregateId)
        where TState : class
    {
        if (!Declares(aggregateId))
        {
            throw new AccountCreationRuleException(
                $"Command '{Operation}' accessed aggregate '{aggregateId}' which is not declared in Touches or Reads (DAT-11).");
        }

        return _inner.Get<TState>(aggregateId);
    }

    public void Set<TState>(string aggregateId, TState state)
        where TState : class
    {
        if (!Declares(aggregateId))
        {
            throw new AccountCreationRuleException($"Command '{Operation}' wrote aggregate '{aggregateId}' which is not declared in Touches (DAT-09).");
        }

        _inner.Set(aggregateId, state);
        _writers[aggregateId] = _registration.Name;
    }

    public ValueTask<bool> TryClaimAsync(string collection, string claimKey) => TryClaimAsync(collection, claimKey, null);

    public async ValueTask<bool> TryClaimAsync(string collection, string claimKey, IReadOnlyDictionary<string, string>? attributes)
    {
        // A key this creation already holds reserves nothing new: the shared context answers false, as the runner's does, and
        // it never counts against the limit (T082 m4).
        if (!_inner.Claims.ContainsKey(new DocumentKey(collection, claimKey)) && _claims >= _registration.MaxClaimMarkers)
        {
            throw new AccountCreationRuleException($"Command '{Operation}' exceeded MaxClaimMarkers = {_registration.MaxClaimMarkers} (DAT-04).");
        }

        bool claimed;
        try
        {
            claimed = await _inner.TryClaimAsync(collection, claimKey, attributes);
        }
        catch (InvalidOperationException ex) when (!CommandContext.IsStoreFailure(ex))
        {
            // The context's own rules (a forbidden or undeclared collection, an empty key): a module bug, reported as a creation
            // rule naming this initializer, not as a throw (T082 m4; round 2 m1).
            throw new AccountCreationRuleException($"Command '{Operation}': {ex.Message}");
        }

        if (claimed)
        {
            _claims++;
        }

        return claimed;
    }

    public void AddOutbox(string kind, object payload)
    {
        _inner.AddOutbox(kind, payload);
        _outbox.Add(_inner.Outbox[^1]);
    }

    /// <summary>Scoped to this initializer: two initializers that use the same purpose draw different ids (T082 m3).</summary>
    public string NewId(string purpose) => _inner.NewId(Scoped(purpose));

    /// <summary>Scoped to this initializer, like <see cref="NewId"/>.</summary>
    public long NewSeed(string purpose) => _inner.NewSeed(Scoped(purpose));

    private string Scoped(string purpose) => _registration.Name + "|" + purpose;

    private bool Declares(string aggregateId) => _registration.Touches.Contains(aggregateId, StringComparer.Ordinal);
}

/// <summary>
/// An account initializer broke a commit rule through its view (undeclared read or write, claim limit): a module bug, reported
/// with this line and never counted as the initializer's own exception (errors-and-report-lines §2, "creation rule violated").
/// </summary>
internal sealed class AccountCreationRuleException(string line) : InvalidOperationException(line);
