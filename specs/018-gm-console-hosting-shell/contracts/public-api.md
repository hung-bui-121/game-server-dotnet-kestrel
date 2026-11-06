# Contract: public API of slice 018 (gm-console-hosting-shell)

Signatures only. C# changes are in the GM module (`GameServer.Modules.Gm.Adapters`, namespace `GameServer.Modules.Gm`);
Platform.Web changes only if T004 shows a status without CORS headers (FR-002). TypeScript is the shell package
`@gameserver/gm-shell`.

## 1. Options (`GmOptions.cs`)

```csharp
public sealed class GmOptions
{
    // existing members unchanged …
    public GmCookieOptions Cookies { get; set; } = new();

    public GmConsoleOptions Console { get; set; } = new();
}

/// <summary>Section <c>Gm:Cookies</c> (slice 018): SameSite of the session and CSRF cookies, independent of CORS.</summary>
public sealed class GmCookieOptions
{
    /// <summary><c>Lax</c> (default, console on the same site) or <c>None</c> (needs CORS origins; always Secure).</summary>
    public string SameSite { get; set; } = "Lax";
}

/// <summary>Section <c>Gm:Console</c> (slice 018): where the console of this environment lives.</summary>
public sealed class GmConsoleOptions
{
    /// <summary>Absolute URL of the console root, path ending with '/'; its origin is in Gm:Cors:AllowedOrigins.</summary>
    public string? BaseUrl { get; set; }
}
```

`GmOptionsValidator` gains the rules of data-model §1 (one report with the existing lines).

## 2. Cookies (`Security/GmCookies.cs`)

```csharp
public static class GmCookies
{
    public const string HostSessionName = "__Host-gs_gm";
    public const string HostCsrfName = "__Host-gs_gm_csrf";
    public const string DevelopmentSessionName = "gs_gm_dev";
    public const string DevelopmentCsrfName = "gs_gm_dev_csrf";

    /// <summary>Session cookie name of this deployment: the __Host- name outside Development.</summary>
    public static string SessionName(GameServerOptions options);

    /// <summary>Antiforgery cookie name of this deployment.</summary>
    public static string CsrfName(GameServerOptions options);

    /// <summary>Options for writing (expiresAt set) or deleting (null) the session cookie: Path=/, HttpOnly, SameSite from
    /// Gm:Cookies, Secure per environment/scheme/SameSite (FR-012, FR-013).</summary>
    public static CookieOptions Session(HttpContext http, DateTimeOffset? expiresAt);

    /// <summary>Antiforgery cookie, header name and CSRF trusted origins (unchanged registration point).</summary>
    public static void Configure(IServiceCollection services);
}
```

Removed (BC-2): `GmCookies.CsrfCookieName`, `GmCookieAuthenticationHandler.CookieName`. The handler, `GmSessionService`,
`GmModule` and `GmSso` read `GmCookies.SessionName(options)`.

## 3. CORS (`Security/GmCors.cs`)

```csharp
public static class GmCors
{
    /// <summary>Exposed to the console (FR-001).</summary>
    public static IReadOnlyList<string> ExposedHeaders { get; } = ["X-Request-Id", "Retry-After", "ETag"];

    /// <summary>Preflight cache (FR-001; Chrome caps at 7200 s).</summary>
    public static TimeSpan PreflightMaxAge { get; } = TimeSpan.FromSeconds(7200);

    public static void Add(IServiceCollection services);
}

public static class GmCorsValidation
{
    // existing Validate + IsAllowed; Validate adds the one-origin rule outside Development (FR-003)
}
```

## 4. SSO (`Sso/GmSso.cs`, new `Sso/GmConsoleUrls.cs`)

```csharp
/// <summary>Redirect targets after SSO (FR-022..FR-025): the console when Gm:Console:BaseUrl is set; without a console the
/// Development page where development tools are on, else /gm/v1/me. Never an absolute URL taken from input.</summary>
public sealed class GmConsoleUrls(IOptions<GmOptions> options, GameServerOptions gameServer)
{
    public const string DevelopmentDefault = "/gm/v1/ui";
    public const string NoConsoleDefault = "/gm/v1/me";
    public const string DeniedQuery = "?error=sso_denied";
    public const string DeniedFragment = "#/login?error=sso_denied";

    /// <summary>The relative value kept in the OIDC properties: the accepted return path, or null.</summary>
    public string? AcceptReturnPath(string? returnUrl);

    /// <summary>Where a successful sign-in lands.</summary>
    public string SuccessTarget(string? acceptedReturnPath);

    /// <summary>Where every denial lands.</summary>
    public string DeniedTarget();
}
```

Removed: `GmSso.DefaultReturnUrl`, `GmSso.DeniedRedirect`, `GmSso.SafeReturnUrl` (callers use `GmConsoleUrls`; registered
as a singleton by `GmSso.Add`).

## 5. Routes (`GmRoutes.cs`)

```csharp
public static RouteDeclaration Ui { get; } =
    RouteDeclaration.Get("gm.ui", GmModule.UiRoute, SecurityProfile.Public, RouteSurface.GmApi, condition: RouteConditions.DevelopmentTools);
```

`GmModule.MapGmApi` maps `gm.ui` only when `AllowsDevelopmentTools` (like `gm.auth_login`).

## 6. Shell package `@gameserver/gm-shell` (`packages/shell/src/index.ts`)

```ts
/** Mounts the console into #root (or the given element). GM-4 adds `extensions` additively. */
export function createGmConsole(options?: GmConsoleOptions): void;

export interface GmConsoleOptions {
  root?: HTMLElement;
}

export interface GmConsoleConfig {
  apiBaseUrl: string;
  gameId: string;
  environmentId: string;
}

export interface GmBuiltFor {
  frameworkVersion: string;
  gameVersion?: string;
}

export interface GmMe {
  staffId: string;
  displayName: string;
  gameId: string;
  environmentId: string;
  permissions: ReadonlySet<string>;
  frameworkVersion: string;
  gameVersion: string;
}

export interface GmProblem {
  status: number;             // 0 = no readable answer (network/CORS)
  code: string;
  codeNumber?: number;
  title?: string;
  detail?: string;
  retryable: boolean;
  errors: Record<string, string[]>;
  requestId?: string;         // body requestId, else exposed X-Request-Id header
}
```

Internal modules (not exported in GM-2, shapes kept for GM-3/GM-4): `api/createGmClient` (`openapi-fetch` `Client<paths>` +
middleware), `catalog/parseCatalog` (`GmCatalog`, `GmOperation`, data-model §8), `navigation/registry`
(`GmConsoleRegistry { addPage(def: GmPageDef): void; addPlayerTab(def: GmPlayerTabDef): void }`, design §7.3 shapes),
`session/*`, `pages/*`, `generated/gm-api.d.ts`.

```ts
// internal: api/client.ts
export interface GmClientOptions {
  config: GmConsoleConfig;
  fetch?: typeof fetch;                    // tests inject a fake
  csrf: { get(): Promise<string>; refresh(): Promise<string> };
  onUnauthorized(): void;                  // FR-057
  onForbidden(problem: GmProblem): void;   // FR-058
}
export function createGmClient(options: GmClientOptions): import('openapi-fetch').Client<import('../generated/gm-api').paths>;
/** Per-call option for GM-3: `headers: { 'X-Request-Id': id }` is passed through untouched; GM-2 never sets it. */
```

## 7. Default app `@gameserver/gm-console-default`

```ts
// app/src/main.tsx
import { createGmConsole } from '@gameserver/gm-shell';
createGmConsole();
```

## 8. Scripts (workspace root `gm-console/package.json`)

| Script | Does |
| --- | --- |
| `gen:api` | `openapi-typescript ../docs/api/gm-openapi.json -o packages/shell/src/generated/gm-api.d.ts` (+ LF normalize if V12 needs it) |
| `check:api` | `gen:api`, then `git diff --exit-code -- packages/shell/src/generated` |
| `lint` | ESLint over `packages`, `app`, `e2e`, `scripts` |
| `typecheck` | `tsc --noEmit` per workspace |
| `test` | Vitest unit/component (shell, app, deploy consistency) |
| `build` | `vite build` of `app` |
| `check:dist` | `node scripts/check-dist.mjs app/dist` (FR-083, SC-007) |
| `verify` | `check:api`, `lint`, `typecheck`, `test`, `build`, `check:dist` |
| `dev` | Vite dev server of `app` on `localhost:5173` |
| `preview` | Vite preview of `app/dist` on `localhost:5173` with `deploy/security-headers.json` |
| `e2e` | Vitest e2e config (`e2e/`), needs a running run-local host |
