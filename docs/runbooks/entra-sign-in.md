# Runbook: sign-in with Microsoft Entra ID

Everything below is configuration; no code changes are needed. **Status: not yet verified against a real Entra tenant.** The OIDC flow,
refresh-token renewal and tenant mapping are proven against a fake provider (`web/e2e/fake-idp.mjs`); the checklist at the end is the
one-time real test.

## 1. Register the API (resource)
Entra admin center → App registrations → New registration, single-tenant (or multi-tenant for SaaS).
- Expose an API: Application ID URI `api://<api-app-id>`; add a scope `access_as_user` (admins and users can consent).
- Manifest: set `requestedAccessTokenVersion` to `2` so tokens are v2.0 (issuer `https://login.microsoftonline.com/<tid>/v2.0`).

## 2. Register the web app (public client)
- New registration → Authentication → add platform **Single-page application**, redirect URI `https://<fms-host>/auth/callback`
  (and `http://localhost:5173/auth/callback` for development). This enables authorization code + PKCE; do **not** enable implicit grant.
- API permissions: add the `access_as_user` scope of the API registration; grant consent.
- No client secret: it is a public client.

## 3. Configure the API
```
Authentication__Providers__0__Name=entra
Authentication__Providers__0__Authority=https://login.microsoftonline.com/<tenant-id-or-common>/v2.0
Authentication__Providers__0__Audience=api://<api-app-id>
Authentication__Providers__0__ClientId=<web-app-client-id>
Authentication__Providers__0__Scope="openid profile email offline_access api://<api-app-id>/access_as_user"
# Multi-tenant SaaS only (authority is .../common/v2.0):
Authentication__Providers__0__IssuerPrefix=https://login.microsoftonline.com/
Authentication__Providers__0__ExternalTenantClaim=tid
Authentication__TenantMappings__0__Provider=entra
Authentication__TenantMappings__0__ExternalTenantId=<customer directory id>
Authentication__TenantMappings__0__TenantId=<FMS tenant id>
```
On-prem single tenant needs no mapping: `Deployment__Mode=OnPrem` pins the one tenant. `offline_access` is what enables silent renewal.
Set `FMS_CSP_CONNECT_SRC="'self' https://login.microsoftonline.com"` so the browser may call the token endpoint and nothing else.

## 4. First administrator
Roles are empty for a new user, so bootstrap one administrator:
1. Sign in once; you see "No access". Your user row now exists.
2. Find the subject: `SELECT Provider, Subject FROM Users WHERE Email = '<you>'` (or decode the access token's `sub` at jwt.ms).
3. Set `FMS_INITIAL_ADMIN=entra:<subject>` and run the migrator again (it is idempotent); reload. You now have the Users screen to give
   everyone else roles.

## 5. One-time real test (checklist)
- [ ] Sign in with the Entra account; the page loads, `/api/v1/me` returns the account.
- [ ] A user from an **unmapped** directory is refused (multi-tenant only).
- [ ] After the access token lifetime (default 60-90 minutes) the app is still signed in (network tab shows a `grant_type=refresh_token` call).
- [ ] Deactivate the user on the Users screen: their next request shows "Your account has been deactivated".
- [ ] Sign out returns to the sign-in page and clears `sessionStorage`.
Record the outcome (and any claim differences found) in `docs/phase-1-summary.md`.

## Known provider behaviour
- Entra limits refresh-token lifetime for single-page apps (about 24 hours, rotating); then the person signs in again.
- A guest or personal Microsoft account may lack `tid`-based mapping; test with a work account first.
