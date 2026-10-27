# Threat model

STRIDE walk of the trust boundaries. Written by the team that built the system, not by an outside tester; the [pen-test brief](pentest-brief.md) is how an independent view is obtained. Last reviewed in Phase 8.

## What matters
1. **Tenant isolation.** One organisation must never read or change another's data (SaaS). 
2. **Integrity of the record.** Work orders, approvals, evidence, stock and the audit trail must not be altered after the fact.
3. **Access secrets.** Visitor passes, calendar feed addresses, BI feed tokens, vendor invitations and license files grant access to something.
4. **Availability for people on site**, including offline technicians.
5. **Personal data.** Visitor names and contact details, user names and e-mail addresses.

## Actors
Signed-in staff (roles), vendor users (portal), visitors (a pass link, no account), BI tools (token), the identity provider, the customer's operators (hold database and server), the vendor (issues licenses), and attackers: an outsider on the internet, a signed-in user of one organisation attacking another, and a malicious or careless insider.

## Boundaries and threats
| Boundary | Threat (STRIDE) | Control | Evidence |
|---|---|---|---|
| Browser to web tier | Spoofing, tampering, information disclosure on the wire | TLS terminated at the customer's or cloud load balancer (required; HSTS sent when forwarded as https); strict CSP, frame denial, no-referrer, nosniff on the web tier | nginx template; e2e "security headers"; `HardeningTests` |
| Web tier to API | Tampering: forged client address to dodge limits or poison logs | Forwarded headers believed only from configured networks | `HardeningTests` (untrusted and trusted cases) |
| Anyone to API (anonymous routes) | Information disclosure by guessing a secret; denial of service | Only ten anonymous routes, listed and locked by a test; secrets are 128 to 256 random bits, stored as SHA-256 hashes; every wrong secret gets the same answer; 60 requests a minute per address on secret routes | `AttackSurfaceTests`; `HardeningTests`; module tests |
| Signed-in user to API | Spoofing, elevation | Tokens validated against the configured identity provider (issuer, audience, lifetime, signing key); default policy denies anonymous; each route names a permission; no self-approval, no self role change, verifier is never the worker | Authentication tests; `AttackSurfaceTests`; workflow and access tests |
| User of tenant A to tenant B's data | Information disclosure, tampering | Tenant resolved from the token's identity-provider tenant, never from a header (SaaS); EF global query filters; **SQL Server row-level security** in the database as a second wall; the API's database login cannot bypass it | `TenantIsolationSqlTests` and per-module isolation tests |
| API to database | Injection | EF Core parameterised queries; the few raw SQL statements (lock and queue-claim statements) use EF's interpolated, parameterised form | code review; no string-built SQL |
| Uploads | Malicious file, storage exhaustion, stored XSS | Type decided from the file's content, not its name or declared type; size limits per endpoint, 5 MB for everything else; files are served with `nosniff` and as downloads or images only | `FileSniffer` tests; upload tests |
| Audit trail / history | Repudiation, tampering | Append-only tables with database INSTEAD OF triggers plus an application guard; audit export is itself audited | audit and workflow tests |
| Background worker | Elevation, replay | At-least-once outbox with idempotent handlers; the worker uses the same least-privileged login | outbox tests |
| Offline client | Information disclosure from a lost phone | Saved jobs are removed on sign-out; sign-out warns about unsent work | offline tests |
| License file | Tampering to gain modules or users | ECDSA signature over the exact payload; trusted keys embedded in the build; binding to the installation | `LicenseTests` |
| Operators and backups | Disclosure at rest | Encryption at rest is provided by the database and storage platform (TDE / storage encryption): **a customer responsibility** that must be enabled; secrets come from environment, Kubernetes secrets or Key Vault, never the repository (secret scan in CI) | deployment guide |
| Supply chain | Tampered dependency or image | Package vulnerability check and `npm audit` in CI, image scanning, Dependabot, CodeQL; pinned scanner versions; SBOM and signatures at release | CI, release workflow |

## Residual risks (accepted or open)
- **No malware scanning of uploads.** Files are type-checked, not virus-scanned. Customers handling untrusted uploads should scan at the storage layer.
- **No multi-factor or session policy of our own.** Sign-in is delegated to the customer's identity provider, which must enforce MFA and session length.
- **Rate limits are per process.** With several API replicas each keeps its own count; the effective limit is higher by the replica count. A gateway-level limit is recommended for internet-facing installs.
- **Token revocation depends on lifetime** for signed-in users (deactivating a user is checked on every request, so that case is immediate).
- **Application-level column encryption is not implemented.** Sensitive fields are minimised instead (no identity-document numbers; visitor details anonymised after a retention period).
- **Worker is not licence-gated**, so an expired installation still sends notices.
- **No external penetration test has been performed.**
