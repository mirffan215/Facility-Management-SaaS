# License keys

`dev-2026.private.b64` is a **development-only** signing key. Anyone with this repository can sign a license with it, so it is
trusted only by builds that carry `dev-2026` in `src/BuildingBlocks/Fms.BuildingBlocks/Licensing/trusted-keys.json`.

- It exists so tests and the browser test can make licenses.
- **A release build must not trust it.** `scripts/check-release-keys.sh` fails when `trusted-keys.json` contains any key id starting with `dev-`, and the release workflow runs it.
- Production keys are made with `fms-license keygen`, kept offline by the vendor (never in this repository), and only the public part is added to `trusted-keys.json`. See `docs/licensing.md`.
