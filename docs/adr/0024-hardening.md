# ADR-0024: Hardening and security assurance (Phase 8b)
Status: Proposed.

## Decisions
- **Self-assessment plus automated checks, not a claimed pen test** (the customer's decision). We provide a threat model, an ASVS Level 2 self-assessment with honest gaps, a brief for an independent tester, and checks that run on every change. Nothing in the repository says the system was penetration tested.
- **API response hardening** on every `/api` response (including errors and refusals): `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a `default-src 'none'` content policy, a restrictive permissions policy, `Cross-Origin-Resource-Policy: same-origin`, and `Cache-Control: no-store` unless an endpoint sets its own. HSTS is sent outside Development when the request arrived as https. No `Server` banner. The web tier hides the API's copies of the headers it sets itself so no value is sent twice.
- **Rate limiting** in the API, because on-prem installs may have no gateway: a fixed one-minute window per signed-in person (1200), per anonymous address (300), and per address on the three routes protected only by a secret in the address (60). 429 with `Retry-After` and a problem document; health checks are exempt. The limits are settings and can be switched off for installs behind a gateway that does it. **Per process**, so replicas multiply the allowance (stated in the threat model).
- **Client address** comes from forwarded headers only from networks listed in `Security:TrustedProxyCidrs` (empty trusts loopback only). The compose bundle trusts the three private ranges because the web tier sits on the container network; a bad network string stops startup.
- **Request size**: 5 MB by default; the endpoints that accept files raise their own limit.
- **Attack surface is locked by a test**: the exact list of anonymous routes is asserted, every other route must be behind the default deny, and any named policy must be a permission.
- **Containers** run as non-root; the web image is now the unprivileged nginx on port 8080; the Helm chart sets read-only root file system, dropped capabilities, no privilege escalation and a default seccomp profile.
- **Scanning in CI**: image scan (fixed critical and high findings fail), secret scan, CodeQL for C# and TypeScript, package and npm audits (already present), Dependabot. A release additionally produces an SBOM and signs the images.

## Consequences
- The web tier's port changed from 80 to 8080 (compose maps 5173 to 8080).
- Image scans can fail on a base-image vulnerability we do not control; the fix is to rebuild on a newer base or, for an accepted risk, add an expiring ignore with a reason.
- Not done: dynamic scanning (ZAP) in CI, malware scanning of uploads, shared rate-limit store, application-level encryption.
