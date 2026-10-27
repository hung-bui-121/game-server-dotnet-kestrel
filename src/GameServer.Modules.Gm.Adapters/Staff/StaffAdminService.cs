using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Modules.Gm;

/// <summary>Who changes staff, for what reason, under which request id (SEC-14 audit fields + idempotency).</summary>
/// <param name="ActorId">Staff id, or <c>system:…</c> for bootstrap and SSO binding.</param>
/// <param name="ActorPermissions">Effective permissions of the actor; null for system actors (no escalation check).</param>
public sealed record StaffChange(string ActorId, IReadOnlySet<string>? ActorPermissions, string RequestId, string Reason, string CorrelationId);

public sealed record StaffResult(StaffRecord Staff, bool Replayed);

/// <summary>New staff: invitation by email, a pre-bound OIDC identity, or (Development/Test) a local password.</summary>
public sealed record NewStaff(
    string StaffId,
    string DisplayName,
    string? Email,
    StaffOidcIdentity? Oidc,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string? PasswordHash = null,
    bool Disabled = false)
{
    /// <summary>Grant scope; defaults to this deployment. Another scope seeds a grant this deployment ignores (SEC-07).</summary>
    public (string? GameId, string? EnvironmentId) Scope { get; init; }
}

/// <summary>
/// Every staff change (SEC-15) in one transaction with its audit entry (SEC-14, research R10): the staff document,
/// identity keys (<c>gm_staff_identities</c>), the staff-manager index and <c>gm_audit</c>. Idempotent per
/// <c>X-Request-Id</c>; refuses privilege escalation and leaving the deployment without a staff manager.
/// </summary>
public sealed class StaffAdminService(
    IAuditedStore audited,
    IKeyValueStore store,
    StaffDirectory directory,
    ModuleRegistry registry,
    GameServerOptions gameServer,
    IClock clock,
    ILogger<StaffAdminService> logger)
{
    public const string IdentityCollection = "gm_staff_identities";
    public const string AuditKind = "gm_staff_audit";

    public static DocumentKey OidcKey(string issuer, string subject) =>
        new(IdentityCollection, "oidc:" + Hash(issuer + "\n" + subject));

    public static DocumentKey EmailKey(string email) => new(IdentityCollection, "email:" + email.Trim().ToLowerInvariant());

    private DocumentKey ManagersKey => new(IdentityCollection, $"managers:{gameServer.GameId}:{gameServer.EnvironmentId}");

    public IReadOnlySet<string> Catalog => registry.Modules.SelectMany(m => m.Descriptor.Permissions).ToHashSet(StringComparer.Ordinal);

    /// <summary>Expands role templates (SEC-15) and validates every permission against the catalog (SEC-08).</summary>
    public IReadOnlyList<string> Expand(IReadOnlyList<string> roles, IReadOnlyList<string> permissions)
    {
        var catalog = Catalog;
        var unknownRoles = roles.Where(r => !GmRoles.Names.Contains(r, StringComparer.Ordinal)).ToArray();
        if (unknownRoles.Length > 0)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"Unknown role(s): {string.Join(", ", unknownRoles)}.");
        }

        var unknown = permissions.Where(p => !catalog.Contains(p)).ToArray();
        if (unknown.Length > 0)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"Permission(s) not in the catalog: {string.Join(", ", unknown)}.");
        }

        return roles.SelectMany(r => GmRoles.Expand(r, catalog)).Concat(permissions)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public Task<StaffResult> CreateAsync(StaffChange change, NewStaff request, CancellationToken cancellationToken)
    {
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        var permissions = Expand(request.Roles, request.Permissions);
        var identityKeys = new List<DocumentKey>();
        if (email is not null)
        {
            identityKeys.Add(EmailKey(email));
        }

        if (request.Oidc is not null)
        {
            identityKeys.Add(OidcKey(request.Oidc.Issuer, request.Oidc.Subject));
        }

        var body = new { request.StaffId, request.DisplayName, email, request.Oidc?.Issuer, request.Oidc?.Subject, permissions, request.Disabled, request.Scope.GameId, request.Scope.EnvironmentId, password = request.PasswordHash is not null };
        return RunAsync(change, "staff.create", request.StaffId, body, identityKeys, (current, identities) =>
        {
            if (current is not null || identities.Any(i => i.Value is not null))
            {
                throw new GameServerException(GmErrorCodes.StaffExists, $"Staff '{request.StaffId}' or its email/identity already exists.");
            }

            EnsureGrantable(change, permissions);
            var created = new StaffRecord
            {
                StaffId = request.StaffId,
                DisplayName = request.DisplayName,
                Email = email,
                Oidc = request.Oidc,
                Status = request.Oidc is not null || request.PasswordHash is not null ? StaffStatus.Active : StaffStatus.Invited,
                Disabled = request.Disabled,
                PasswordHash = request.PasswordHash,
            }.WithGrant(request.Scope.GameId ?? gameServer.GameId, request.Scope.EnvironmentId ?? gameServer.EnvironmentId, permissions);
            var inserts = identities.Keys.Select(k => (k, IdentityDocument(request.StaffId, k, request.Oidc))).ToArray();
            return new Mutation(created, inserts);
        }, cancellationToken);
    }

    /// <summary>Replaces the grant of this game/environment; other scopes are kept.</summary>
    public Task<StaffResult> SetGrantAsync(StaffChange change, string staffId, IReadOnlyList<string> roles, IReadOnlyList<string> extra, CancellationToken cancellationToken)
    {
        var permissions = Expand(roles, extra);
        return RunAsync(change, "staff.update", staffId, new { permissions }, [], (current, _) =>
        {
            var staff = current ?? throw new GameServerException(CoreErrorCodes.NotFound, $"Staff '{staffId}' does not exist.");
            EnsureCanManage(change, staff);
            EnsureGrantable(change, permissions);
            return new Mutation(staff.WithGrant(gameServer.GameId, gameServer.EnvironmentId, permissions), []);
        }, cancellationToken);
    }

    /// <summary>Disabling also increments the session epoch: every session ends within the cache window (SEC-10).</summary>
    public Task<StaffResult> SetDisabledAsync(StaffChange change, string staffId, bool disabled, CancellationToken cancellationToken) =>
        RunAsync(change, disabled ? "staff.disable" : "staff.enable", staffId, new { disabled }, [], (current, _) =>
        {
            var staff = current ?? throw new GameServerException(CoreErrorCodes.NotFound, $"Staff '{staffId}' does not exist.");
            EnsureCanManage(change, staff);
            return new Mutation(staff with { Disabled = disabled, SessionEpoch = disabled ? staff.SessionEpoch + 1 : staff.SessionEpoch }, []);
        }, cancellationToken);

    /// <summary>Ends every session of the staff member on every instance within the cache window (SEC-10).</summary>
    public Task<StaffResult> RevokeSessionsAsync(StaffChange change, string staffId, CancellationToken cancellationToken) =>
        RunAsync(change, "staff.revoke_sessions", staffId, new { }, [], (current, _) =>
        {
            var staff = current ?? throw new GameServerException(CoreErrorCodes.NotFound, $"Staff '{staffId}' does not exist.");
            if (staff.StaffId != change.ActorId)
            {
                EnsureCanManage(change, staff);
            }

            return new Mutation(staff with { SessionEpoch = staff.SessionEpoch + 1 }, []);
        }, cancellationToken);

    /// <summary>
    /// Binds an OIDC identity to the staff invited with <paramref name="email"/> (research R3): the identity key is
    /// inserted once, so two concurrent first sign-ins bind it once. Returns null when nothing can be bound.
    /// </summary>
    public async Task<StaffRecord?> BindInvitationAsync(string email, StaffOidcIdentity identity, string correlationId, CancellationToken cancellationToken)
    {
        var invitation = await store.GetAsync(EmailKey(email), cancellationToken);
        var staffId = invitation?.Content["staffId"]?.GetValue<string>();
        if (staffId is null)
        {
            return null;
        }

        var change = new StaffChange("system:sso", null, Hash(identity.Issuer + "\n" + identity.Subject), "First SSO sign-in of an invited staff member", correlationId);
        try
        {
            var result = await RunAsync(change, "staff.bind", staffId, new { identity.Issuer, identity.Subject }, [OidcKey(identity.Issuer, identity.Subject)], (current, identities) =>
            {
                var staff = current ?? throw new GameServerException(CoreErrorCodes.NotFound, $"Staff '{staffId}' does not exist.");
                if (staff.Oidc is not null || identities.Values.Any(v => v is not null) || staff.Status != StaffStatus.Invited)
                {
                    throw new GameServerException(GmErrorCodes.StaffExists, "The invitation was already used or the identity is bound to another staff member.");
                }

                return new Mutation(
                    staff with { Oidc = identity, Status = StaffStatus.Active },
                    [(OidcKey(identity.Issuer, identity.Subject), IdentityDocument(staffId, OidcKey(identity.Issuer, identity.Subject), identity))]);
            }, cancellationToken);
            return result.Staff;
        }
        catch (GameServerException ex) when (ex.Code == GmErrorCodes.StaffExists)
        {
            return null;
        }
    }

    /// <summary>
    /// Development/Test seed: creates the staff, or brings an existing record to the seed. The request id depends on
    /// the seed content (password as a fingerprint, not its salted hash), so an unchanged seed replays on restart and
    /// does not undo changes made through the API.
    /// </summary>
    public async Task<StaffResult> SeedAsync(NewStaff seed, string passwordFingerprint, CancellationToken cancellationToken)
    {
        var permissions = Expand(seed.Roles, seed.Permissions);
        var requestId = Hash(JsonSerializer.Serialize(
            new { seed.StaffId, seed.DisplayName, permissions, seed.Disabled, seed.Scope.GameId, seed.Scope.EnvironmentId, passwordFingerprint },
            GameServerJson.Options));
        var change = new StaffChange("system:bootstrap", null, requestId, "Configured bootstrap", "bootstrap");
        foreach (var operation in new[] { "staff.create", "staff.bootstrap" })
        {
            if (await store.GetAsync(AuditKey(change, operation, seed.StaffId), cancellationToken) is not null)
            {
                var unchanged = StaffDirectory.Parse(await store.GetAsync(StaffDirectory.Key(seed.StaffId), cancellationToken)) ?? new StaffRecord();
                return new StaffResult(unchanged, true);
            }
        }

        var existing = StaffDirectory.Parse(await store.GetAsync(StaffDirectory.Key(seed.StaffId), cancellationToken));
        if (existing is null)
        {
            try
            {
                return await CreateAsync(change, seed, cancellationToken);
            }
            catch (GameServerException ex) when (ex.Code == GmErrorCodes.StaffExists)
            {
                // Another instance starting at the same time created it: bring it to the seed below.
            }
        }

        return await RunAsync(change, "staff.bootstrap", seed.StaffId, new { permissions, seed.Disabled, password = seed.PasswordHash is not null }, [], (current, _) =>
        {
            var staff = current ?? throw new GameServerException(CoreErrorCodes.NotFound, $"Staff '{seed.StaffId}' does not exist.");
            return new Mutation(
                (staff with
                {
                    DisplayName = seed.DisplayName,
                    PasswordHash = seed.PasswordHash ?? staff.PasswordHash,
                    Disabled = seed.Disabled,
                    SessionEpoch = seed.Disabled && !staff.Disabled ? staff.SessionEpoch + 1 : staff.SessionEpoch,
                }).WithGrant(seed.Scope.GameId ?? gameServer.GameId, seed.Scope.EnvironmentId ?? gameServer.EnvironmentId, permissions),
                []);
        }, cancellationToken);
    }

    /// <summary>Reads the stored record (no cache): startup checks and administration.</summary>
    public async Task<StaffRecord?> GetAsync(string staffId, CancellationToken cancellationToken) =>
        StaffDirectory.Parse(await store.GetAsync(StaffDirectory.Key(staffId), cancellationToken));

    /// <summary>Number of staff who can sign in and hold <c>staff.manage</c> here (bootstrap, R11).</summary>
    public async Task<int> CountManagersAsync(CancellationToken cancellationToken) =>
        ReadManagers(await store.GetAsync(ManagersKey, cancellationToken)).Count;

    /// <summary>
    /// Audit entry (key + content) for an operation committed by another audited transaction, e.g. the key-ring
    /// rotation that the platform writes together with this entry (review M3). Target <c>system</c>.
    /// </summary>
    public (DocumentKey Key, Func<object, JsonObject> Build) SystemAudit(StaffChange change, string operation, object? body = null)
    {
        if (string.IsNullOrWhiteSpace(change.Reason) || change.Reason.Length > 500)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "reason is required (1-500 characters).");
        }

        var key = AuditKey(change, operation, "system");
        return (key, details =>
        {
            var document = AuditDocument(change, operation, "system", BodyHash(body ?? change.RequestId), key, null, null, clock.UtcNow);
            document["payload"]!["after"] = System.Text.Json.JsonSerializer.SerializeToNode(details, GameServerJson.Options);
            return document;
        });
    }

    private static DocumentKey AuditKey(StaffChange change, string operation, string targetStaffId) =>
        new(GmModule.AuditCollection, "gm_audit::" + Hash($"{change.ActorId}|{operation}|{targetStaffId}|{change.RequestId}"));

    private sealed record Mutation(StaffRecord? Staff, IReadOnlyList<(DocumentKey Key, JsonObject Content)> Inserts);

    private async Task<StaffResult> RunAsync(
        StaffChange change,
        string operation,
        string targetStaffId,
        object body,
        IReadOnlyList<DocumentKey> identityKeys,
        Func<StaffRecord?, IReadOnlyDictionary<DocumentKey, StoredDocument?>, Mutation> mutate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(change.Reason) || change.Reason.Length > 500)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "reason is required (1-500 characters).");
        }

        var bodyHash = Hash(JsonSerializer.Serialize(body, GameServerJson.Options));
        var auditKey = AuditKey(change, operation, targetStaffId);
        var staffKey = StaffDirectory.Key(targetStaffId);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                        var result = await audited.RunAsync(
                    async tx => await ApplyAsync(tx, change, operation, targetStaffId, bodyHash, auditKey, staffKey, identityKeys, mutate));
                directory.Invalidate(targetStaffId);
                if (!result.Replayed)
                {
                    logger.LogInformation("GM staff change {Operation} on {TargetStaffId} by {ActorId}.", operation, targetStaffId, change.ActorId);
                }

                return result;
            }
            catch (DocumentExistsException ex) when (ex.Key == auditKey && attempt == 1)
            {
                // The same request committed concurrently: run again to return its result as a replay.
            }
            catch (DocumentExistsException ex) when (ex.Key == ManagersKey && attempt < 3)
            {
                // Concurrent first write of the manager index: read it again (conflict on one document, by design).
            }
            catch (DocumentExistsException)
            {
                throw new GameServerException(GmErrorCodes.StaffExists, $"Staff '{targetStaffId}' or its email/identity already exists.");
            }
            catch (TransactionCommitAmbiguousException ex)
            {
                // The audit entry is committed with the change: if it is visible, the change committed (CMD-04).
                if (await store.GetAsync(auditKey, CancellationToken.None) is null)
                {
                    throw new GameServerException(CoreErrorCodes.CommitUnknownRetry, ex.Message);
                }

                directory.Invalidate(targetStaffId);
                return new StaffResult(StaffDirectory.Parse(await store.GetAsync(staffKey, CancellationToken.None)) ?? new StaffRecord(), false);
            }
            catch (TransactionConflictExpiredException ex)
            {
                throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, ex.Message);
            }
        }
    }

    private async Task<StaffResult> ApplyAsync(
        IAuditedTransaction tx,
        StaffChange change,
        string operation,
        string targetStaffId,
        string bodyHash,
        DocumentKey auditKey,
        DocumentKey staffKey,
        IReadOnlyList<DocumentKey> identityKeys,
        Func<StaffRecord?, IReadOnlyDictionary<DocumentKey, StoredDocument?>, Mutation> mutate)
    {
        var keys = new List<DocumentKey> { staffKey, auditKey, ManagersKey };
        keys.AddRange(identityKeys);
        var read = await tx.GetManyAsync(keys);
        var staffDocument = read[0];
        var current = StaffDirectory.Parse(staffDocument);
        if (read[1] is { } audit)
        {
            if (audit.Content["payload"]?["bodyHash"]?.GetValue<string>() != bodyHash)
            {
                throw new GameServerException(CoreErrorCodes.IdempotencyPayloadMismatch, "X-Request-Id was already used with a different body.");
            }

            return new StaffResult(current ?? new StaffRecord(), true);
        }

        var identities = identityKeys.Select((k, i) => (k, read[3 + i])).ToDictionary(p => p.k, p => p.Item2);
        var mutation = mutate(current, identities);
        var now = clock.UtcNow;
        if (mutation.Staff is { } next)
        {
            next = next with { UpdatedAtUtc = AggregateDocumentSerializer.FormatTime(now) };
            var content = (JsonObject)JsonSerializer.SerializeToNode(next, GameServerJson.Options)!;
            if (staffDocument is null)
            {
                await tx.InsertAsync(staffKey, content);
            }
            else
            {
                await tx.ReplaceAsync(staffDocument, content);
            }

            await UpdateManagersAsync(tx, read[2], targetStaffId, IsManager(next));
            mutation = mutation with { Staff = next };
        }

        foreach (var (key, content) in mutation.Inserts)
        {
            await tx.InsertAsync(key, content);
        }

        await tx.InsertAsync(auditKey, AuditDocument(change, operation, targetStaffId, bodyHash, auditKey, current, mutation.Staff, now));
        return new StaffResult(mutation.Staff ?? current ?? new StaffRecord(), false);
    }

    /// <summary>
    /// The manager index serializes concurrent changes (write-write conflict on one document) so two simultaneous
    /// demotions cannot both pass the "last manager" check.
    /// </summary>
    private async Task UpdateManagersAsync(IAuditedTransaction tx, StoredDocument? document, string staffId, bool isManager)
    {
        var managers = ReadManagers(document);
        var changed = isManager ? managers.Add(staffId) : managers.Remove(staffId);
        if (!changed)
        {
            return;
        }

        if (managers.Count == 0)
        {
            throw new GameServerException(GmErrorCodes.LastStaffManager, $"Staff '{staffId}' is the last staff manager of this deployment.");
        }

        var content = new JsonObject { ["staffIds"] = new JsonArray(managers.Order(StringComparer.Ordinal).Select(id => (JsonNode)id).ToArray()) };
        if (document is null)
        {
            await tx.InsertAsync(ManagersKey, content);
        }
        else
        {
            await tx.ReplaceAsync(document, content);
        }
    }

    /// <summary>Managers who can actually sign in (active, not disabled): an unused invitation does not count (review M2).</summary>
    private bool IsManager(StaffRecord staff) =>
        staff.CanSignIn && staff.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Contains(GmPermissions.StaffManage);

    private static HashSet<string> ReadManagers(StoredDocument? document) =>
        document?.Content["staffIds"]?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];

    /// <summary>
    /// A manager may change only staff whose permissions are a subset of their own: no downgrading, disabling or
    /// re-enabling someone more privileged (review M1).
    /// </summary>
    private void EnsureCanManage(StaffChange change, StaffRecord target)
    {
        if (change.ActorPermissions is null)
        {
            return;
        }

        var extra = target.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Except(change.ActorPermissions).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length > 0)
        {
            throw new GameServerException(GmErrorCodes.PermissionNotGrantable, $"Staff '{target.StaffId}' holds permissions you do not hold: {string.Join(", ", extra)}.");
        }
    }

    private static void EnsureGrantable(StaffChange change, IReadOnlyList<string> permissions)
    {
        if (change.ActorPermissions is null)
        {
            return;
        }

        var missing = permissions.Where(p => !change.ActorPermissions.Contains(p)).ToArray();
        if (missing.Length > 0)
        {
            throw new GameServerException(GmErrorCodes.PermissionNotGrantable, $"You do not hold: {string.Join(", ", missing)}.");
        }
    }

    private static JsonObject IdentityDocument(string staffId, DocumentKey key, StaffOidcIdentity? oidc) =>
        key.Key.StartsWith("oidc:", StringComparison.Ordinal) && oidc is not null
            ? new JsonObject { ["staffId"] = staffId, ["issuer"] = oidc.Issuer, ["subject"] = oidc.Subject }
            : new JsonObject { ["staffId"] = staffId };

    private JsonObject AuditDocument(
        StaffChange change,
        string operation,
        string targetStaffId,
        string bodyHash,
        DocumentKey auditKey,
        StaffRecord? before,
        StaffRecord? after,
        DateTimeOffset now) => new()
    {
        ["entryId"] = auditKey.Key["gm_audit::".Length..],
        ["kind"] = AuditKind,
        ["gameId"] = gameServer.GameId,
        ["environmentId"] = gameServer.EnvironmentId,
        ["actorId"] = change.ActorId,
        ["operation"] = operation,
        ["targetStaffId"] = targetStaffId,
        ["committedAtUtc"] = AggregateDocumentSerializer.FormatTime(now),
        ["payload"] = new JsonObject
        {
            ["actorId"] = change.ActorId,
            ["permission"] = operation.StartsWith("ops.", StringComparison.Ordinal) ? operation : GmPermissions.StaffManage,
            ["action"] = operation,
            ["targetStaffId"] = targetStaffId,
            ["reason"] = change.Reason,
            ["before"] = Summary(before),
            ["after"] = Summary(after),
            ["correlationId"] = change.CorrelationId,
            ["requestId"] = change.RequestId,
            ["bodyHash"] = bodyHash,
        },
    };

    /// <summary>Audit view of a staff record: no password hash.</summary>
    private JsonObject? Summary(StaffRecord? staff) => staff is null
        ? null
        : new JsonObject
        {
            ["status"] = staff.Status,
            ["disabled"] = staff.Disabled,
            ["sessionEpoch"] = staff.SessionEpoch,
            ["oidcBound"] = staff.Oidc is not null,
            ["permissions"] = new JsonArray(staff.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId)
                .Order(StringComparer.Ordinal).Select(p => (JsonNode)p).ToArray()),
        };

    /// <summary>Hash of a request body, stored in the audit entry to detect a reused request id with another body.</summary>
    public static string BodyHash(object body) => Hash(JsonSerializer.Serialize(body, GameServerJson.Options));

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
