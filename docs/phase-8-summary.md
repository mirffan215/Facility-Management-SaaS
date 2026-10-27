# Phase 8: Packaging, licensing and hardening

Design records: [ADR-0023](adr/0023-licensing.md) (licensing), [ADR-0024](adr/0024-hardening.md) (hardening), [ADR-0025](adr/0025-packaging-and-upgrades.md) (packaging, release, upgrades). Guides: [deployment](deployment.md), [licensing](licensing.md), [upgrading](upgrading.md), [security documents](security/). Delivered in four slices.

## Built
| Slice | Area | What |
|---|---|---|
| 8a | Licensing | Signed license file (ECDSA P-256) checked offline against keys built into the product; customer, edition, modules, user limit, optional binding to the installation, dates, grace period. In date: all licensed modules. Past the end date: grace with a banner, then **read-only** (reading and exporting always work). Missing, damaged or foreign license: read-only. Modules outside the license answer 403. New people beyond the user limit are refused (existing users never locked out). A renewed file is picked up within a minute, no restart. Vendor tool `fms-license` (keygen, sign, inspect). Administration, License page; warning banner; English and Arabic. |
| 8b | Hardening | Strict headers on every API answer (including refusals), no caching of answers, no server banner, 5 MB default request size; rate limits (per person, per anonymous address, and a small budget per address for the three secret-address routes) with 429 and `Retry-After`; forwarded client address believed only from configured networks; the list of anonymous routes locked by a test; unprivileged web image; image scan, secret scan, CodeQL and Dependabot in CI. Threat model, OWASP ASVS L2 self-assessment, brief for an external penetration test. |
| 8c | Packaging | Docker Compose on-premise bundle (online and offline with images and checksums), Helm chart (non-root, read-only, migrator Job as a hook, optional network policy), Azure Bicep template (Container Apps, SQL, Blob, Key Vault, migrator job), release workflow (tagged build, provenance and SBOM, keyless image signing, chart to the registry, bundles and checksums attached to the release, refuses a development license key). |
| 8d | Upgrades | Migrator `--check` mode (what would change) that refuses a database newer than the running version (exit 4); `upgrade.sh` (version order, disk, check, verified backup, stop, migrate, start, readiness, version smoke test) and `rollback.sh` (restore the backup, return to the old version); Azure SQL contained-user path for the application login; version in `/api/v1/system/info`. |

## Tests
Suites green at the end of the phase: 523 unit, 41 API, 339 integration, 74 web, 39 browser steps, all also green in CI.

- Unit: license signature, tampering, wrong or unknown key, garbage input; date states and grace cap; installation binding; module mapping for every route family; renewal picked up without restart; configuration text.
- API: security headers on success, refusal and not-found; 429 with `Retry-After`; health exempt; secret-route budget; spoofed forwarded address ignored from an untrusted caller and honoured from a trusted network; limiter can be disabled; malformed network stops startup.
- Integration (real SQL Server): read-only without a valid license for every bad state, module closure for signed-in and anonymous callers, grace, license page permission and user counts, user limit (newcomer refused and not created, deactivate then admit, reactivation refused at the limit, other organisations not counted); the exact anonymous route list; every other route behind the default deny and named permissions; migrator check mode changes nothing and reports pending then zero; a database from a newer release is refused by both check and a real run.
- Browser: license page, an expired license swapped in on the server turns the whole app read-only with the right messages (and a renewal restores it), accessibility scans in English and Arabic, and the API and web tier send each security header once.
- Scripts: `scripts/test-onprem-scripts.sh` covers version comparison, `.env` handling and the refusals of install, upgrade and rollback (run in CI); `scripts/make-onprem-bundle.sh` was run locally (online bundle, checksum, and its refusal of the development key under `--release`).

## What CI has and has not verified
Verified by CI on the final commit (all jobs green): Helm lint and render, the rendered manifests against the Kubernetes 1.30 schemas, pod lock-down checks, Bicep compilation, the on-premise script tests, the bundle build, image scan (no fixed critical or high findings), secret scan, and the browser suite through the real unprivileged nginx image.

Not verified (only a real deployment or a first release can):
- **The chart and the Bicep template have never been deployed** to a cluster or to Azure. They render and compile; that is all that is known.
- **The compose bundle's `install.sh`, `upgrade.sh` and `rollback.sh` were not run against a live stack** (Docker Hub rate-limited the pulls); their logic and refusals are tested, the full run is not. Rehearse an upgrade and rollback on a copy first.
- **Azure SQL** contained-user path for the application login is unexercised.
- **The release workflow** (signing, SBOM, chart push, bundles) and **CodeQL** have not run yet; they run on the first tag and on the next pull request or push to the default branch. Dependabot is now enabled and will open update pull requests.

## Decisions for the owner
1. **Production signing key.** Generate it offline with `fms-license keygen`, decide who holds and backs it up, put only the public key in `trusted-keys.json`. Until then the development key (whose private half is in the repository on purpose) is trusted and a release is refused.
2. **Missing license = read-only** for on-premise (no automatic trial). For a trial, issue a short license.
3. **Registry and names.** Images, chart and bundles default to `ghcr.io/mirffan215`; change `image.registry` / `FMS_REGISTRY` if you publish elsewhere.

## Not done / known gaps
- No external penetration test (a brief is provided); no ZAP/dynamic scan in CI; no malware scanning of uploads; no application-level column encryption; rate limits are per process.
- SaaS has no plan-based entitlements, usage metering or online license service. The worker is not license-gated.
- Zero-downtime upgrades are not designed; rollback is restore-from-backup. A license is installed by replacing a file, not through the web.
- No Terraform, no Azure Marketplace packaging, no Helm chart tests beyond CI rendering.
