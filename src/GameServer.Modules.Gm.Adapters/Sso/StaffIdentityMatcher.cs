using GameServer.Platform.Persistence;

namespace GameServer.Modules.Gm;

/// <summary>
/// Explicit authorization for SSO (SEC-03, research R3): a company identity signs in only when it is bound to a staff
/// record of this deployment (issuer + subject), or when it is the first sign-in of an email invitation with a
/// verified email — then the identity is bound once and the email is never used again for matching.
/// </summary>
public sealed class StaffIdentityMatcher(IKeyValueStore store, StaffDirectory directory, StaffAdminService admin)
{
    public async Task<StaffRecord?> MatchAsync(string issuer, string subject, string? email, bool emailVerified, string correlationId, CancellationToken cancellationToken)
    {
        var bound = await store.GetAsync(StaffAdminService.OidcKey(issuer, subject), cancellationToken);
        if (bound?.Content["staffId"]?.GetValue<string>() is { } staffId)
        {
            directory.Invalidate(staffId);
            var staff = await directory.GetAsync(staffId, cancellationToken);
            return staff?.Oidc is { } oidc && oidc.Issuer == issuer && oidc.Subject == subject ? staff : null;
        }

        if (string.IsNullOrWhiteSpace(email) || !emailVerified)
        {
            return null;
        }

        return await admin.BindInvitationAsync(email, new StaffOidcIdentity { Issuer = issuer, Subject = subject }, correlationId, cancellationToken);
    }
}
