# ADR-0023: Licensing (Phase 8a)
Status: Proposed. Replaces the placeholder in [deployment-models](../deployment-models.md) ("Signed license file (modules, users, expiry)").

## Decisions
- **Signed file, checked offline** (the customer's decision). The vendor signs a small JSON payload with an **ECDSA P-256** private key; the product embeds the matching public key(s) and verifies the signature over the exact payload bytes. No outbound call is ever made, so it works air-gapped. The payload names the customer, edition, licensed **modules**, an optional **user limit**, an optional **installation binding** (the on-prem tenant id), start and end dates, and a **grace period** (default 30 days, capped at 90).
- **Trusted keys live in the assembly** (`trusted-keys.json`, an embedded resource), not in configuration, so editing a settings file cannot add a key. A key id in the file selects the key, so keys can be rotated: ship a release that trusts both, then issue new licenses with the new key. A customer who recompiles the product can of course change anything; licensing here is a commercial control backed by the contract, not copy protection.
- **A development key is committed on purpose** (`tools/license/dev-2026.private.b64`, stored without PEM markers) so tests and the browser test can issue licenses. `scripts/check-release-keys.sh` fails any release build whose trusted keys include an id starting with `dev-`, and the release workflow runs it. **The production private key must be generated offline by the vendor and never enter this repository** (decision for the owner: who holds it, where it is backed up; losing it means shipping a release that trusts a new key and reissuing every license).
- **States and what each does** (on-premise only; SaaS reports `Valid` and is not restricted):

  | State | Meaning | Effect |
  |---|---|---|
  | Valid | in date | everything licensed works |
  | Grace | past end date, inside grace | everything works; banner and `X-License-State: grace` |
  | Expired | past grace | **read-only**: GET/HEAD/OPTIONS work, anything that changes data is refused (403, code `license.read_only`) |
  | NotYetValid, Invalid, Missing, WrongInstallation | not usable | read-only as above |

  Reading is kept deliberately: a customer must always be able to see and export their own data. The license page, system info, sign-in endpoints and health checks always answer.
- **Modules.** A licensed module list (`work`, `pm`, `inventory`, `vendors`, `purchasing`, `spaces`, `bookings`, `visitors`, `reporting`, or `*`). Locations, assets, meters, users, roles, audit, notifications and the license itself are core and always available. A module outside the license answers 403 (code `license.module_not_licensed`) for reads and writes, signed in or not. Paths map to modules in one place (`LicenseModules.For`), and a test checks every module has a route. Work-order parts belong to inventory.
- **User limit.** New people are refused at first sign-in (403, `license.user_limit`) and are not created; reactivating a deactivated user beyond the limit is refused (409). Existing active users are never locked out by a lowered limit. The count is of active users in the installation's organisation; two simultaneous first sign-ins at the limit can overshoot by the number of racing requests (accepted; the next check corrects it).
- **Where the file comes from.** `License:Path` (default `/etc/fms/license/fms.license`, a mounted file or Kubernetes secret) or `License:Text` (for environments that only have variables). It is re-read when its time stamp or size changes (checked at most every 15 seconds), so a renewal needs no restart and works across replicas. Dates are judged on every read, so an installation moves into grace and then read-only by itself.
- **No missing-license grace for new installs.** An on-prem install with no license is read-only. The first administrator can still sign in and see how to install one. (Alternative considered: a 30-day evaluation. It needs a stored first-start date that a reinstall could reset; revisit if sales needs trials, which can be done by issuing a short license.)
- **Vendor tool.** `tools/Fms.LicenseTool` (`fms-license keygen | sign | inspect`). Unknown module names are refused at signing time.

## Consequences
- No license server, usage metering, online renewal or per-tenant plans for SaaS in this phase (SaaS is unrestricted). The interface (`ILicenseProvider`) is where plan-based entitlements would plug in.
- Background workers are not gated: an expired installation still delivers notifications and generates scheduled work; only the API refuses changes. Revisit if that is not wanted.
- The license page is read-only; installing a license is replacing a file.
