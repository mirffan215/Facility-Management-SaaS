# ADR-0004: Authentication and tenant resolution
Status: Proposed

## Decision
- The API is an OIDC **resource server**: it validates bearer JWTs from configured providers (Microsoft Entra ID, Google,
  any OpenID Connect provider). Browsers sign in with authorization code + PKCE directly against the provider.
- Each provider is a named JwtBearer scheme. A routing scheme picks the provider from the token's (untrusted) issuer; the
  provider's scheme then validates signature, issuer, audience and lifetime. Unsigned tokens are rejected.
- **The tenant is never taken from the token.** After validation the server removes any `fms_*` claims the token carried,
  then sets `fms_tid` from `ITenantDirectory`, which maps (provider, provider-tenant id) to an FMS tenant. No mapping means
  no `fms_tid`, and the fallback authorization policy returns 403.
- Multi-tenant providers (Entra "common") must declare `IssuerPrefix` and `ExternalTenantClaim`, so a token from any
  customer directory is only accepted when that directory is mapped to an FMS tenant.
- On-prem: same providers; every authenticated user maps to the single configured tenant.
- All endpoints require sign-in plus a resolved tenant unless marked `AllowAnonymous` (health, system info, provider list).

## Consequences
- Onboarding a SaaS customer = creating a tenant and a mapping (config now, database-backed directory later).
- Per-tenant sign-in restrictions (allowed provider, domain) and local accounts for on-prem sites without an IdP are not
  covered yet and need a follow-up ADR.
- Users, roles and permissions are separate (next step); a token proves who someone is, not what they may do.
