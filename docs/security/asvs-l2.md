# OWASP ASVS 4.0.3 Level 2: self-assessment

A map of the requirement areas to where the system does (or does not) meet them. **This is a self-assessment by the developers, not a certification and not a penetration test.** Status: **Met** (built and tested), **Partial** (built with a stated limit), **Delegated** (done by the identity provider, database or platform the customer runs), **Gap** (not built), **N/A**.

| Chapter | Area | Status | How / where | Evidence |
|---|---|---|---|---|
| V1 Architecture | Threat model, trust boundaries, secure defaults | Met | [Threat model](threat-model.md); ADRs 0005 (isolation), 0023, 0024; default-deny authorisation | docs; `AttackSurfaceTests` |
| V2 Authentication | Passwords, MFA, recovery | Delegated | OpenID Connect only; no local passwords. The identity provider must enforce MFA and password rules | ADR-0004 |
| V2 | Secrets in links and tokens | Met | 128 to 256 random bits; shown once; SHA-256 stored; identical answer for any wrong secret; rate limited per address | module tests; `HardeningTests` |
| V3 Session | Token lifetime, logout | Partial | Bearer tokens from the provider; the browser keeps them in session storage, cleared on sign-out; a deactivated user is refused on the next request; no server-side session list | auth tests; e2e |
| V4 Access control | Deny by default, least privilege, tenant separation | Met | Fallback policy denies anonymous; a named permission per route; system roles; maker-checker rules; EF filters plus row-level security | `AttackSurfaceTests`; isolation tests |
| V4 | Direct object references | Met | IDs are looked up inside the tenant filter; another tenant's id answers 404 | isolation tests per module |
| V5 Validation | Input validation, injection | Met | Typed DTOs with explicit validation; EF parameterised queries; enums as strings with 400 on unknown values; OData accepts only a tiny known grammar and refuses the rest | validation tests; `ODataServiceTests` |
| V5 | Output encoding | Met | React escapes by default; the API sends JSON with `nosniff`; CSV export neutralises formula characters | CSV tests |
| V6 Cryptography | At rest | Delegated | TDE / storage encryption by the platform; no custom column encryption (see residual risks) | deployment guide |
| V6 | Algorithms | Met | ECDSA P-256 / SHA-256 for licenses; SHA-256 for secret hashes; random values from the OS generator | `LicenseTests` |
| V7 Errors, logging | No sensitive data in errors; audit | Met | RFC 7807 problems without stack traces; structured logs; append-only audit trail with viewer and export | audit tests |
| V7 | Log protection | Partial | Logs go to the platform's log store; tamper protection there is the operator's | runbook |
| V8 Data protection | Minimise personal data, retention | Met | No identity-document numbers; visitor details removed after retention; secrets never logged; API answers are `no-store` | visitor retention tests; `HardeningTests` |
| V9 Communication | TLS | Delegated | TLS ends at the customer's load balancer; HSTS is sent when the request arrived as https | nginx; `UseHsts` |
| V10 Malicious code | Dependencies, build | Partial | Vulnerability check, npm audit, image and secret scan, CodeQL, Dependabot, SBOM and signed images at release; no reproducible build | CI |
| V11 Business logic | Sequence, limits, anti-automation | Met | State machines for work, bookings, approvals; capacity and double-booking constraints under concurrency; rate limits | concurrency tests |
| V12 Files | Upload type, size, storage | Met | Content-based type check, size limits, stored outside the web root under tenant paths, served as data | file tests |
| V12 | Antivirus | Gap | Not built (see residual risks) | n/a |
| V13 API | Versioned REST, content types, no mass assignment | Met | `/api/v1`; request records list exactly the accepted fields; problem details | API tests |
| V13 | CORS | Met | No cross-origin access: one origin through the web tier | nginx; no CORS configured |
| V14 Configuration | Hardened headers, no defaults | Met | Strict headers on API and web tier; non-root containers; read-only root file system and dropped capabilities in the Helm chart; no server banner; secrets from the environment | `HardeningTests`; e2e; chart |
| V14 | Dependency hygiene | Partial | As V10 | CI |

## Known gaps to close before a regulated deployment
1. Independent penetration test (brief provided).
2. Malware scanning of uploads.
3. Optional application-level encryption of chosen fields.
4. Gateway-level (shared) rate limiting for multi-replica internet-facing installs.
5. Log shipping with tamper protection, as a deployment pattern.
