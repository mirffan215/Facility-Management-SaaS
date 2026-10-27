# ADR-0025: Packaging, release and upgrades (Phase 8c, 8d)
Status: Proposed. Builds on [ADR-0002](0002-single-codebase-two-models.md).

## Decisions
- **One set of four images** (`fms-api`, `fms-worker`, `fms-web`, `fms-migrator`) runs everywhere. Three ways to run them: **Docker Compose bundle** (single-server on-premise), **Helm chart** (Kubernetes), **Bicep** (Azure Container Apps). No feature differs by packaging.
- **Versioning.** A release is a git tag `vX.Y.Z`. The tag becomes the .NET assembly version (reported by `/api/v1/system/info`), the image tag, the chart version and the bundle name.
- **Release pipeline** (`.github/workflows/release.yml`): refuses a build that trusts a development license key; builds and tests; pushes images with provenance and an SBOM; signs each image keylessly with cosign; packages the Helm chart to the OCI registry; builds an *online* bundle (pulls images) and an *offline* bundle (images inside, with checksums) for air-gapped servers; attaches a CycloneDX SBOM and checksums to the GitHub release.
- **The migrator is a separate one-shot image** run before the new version starts (compose: a step of `upgrade.sh`; Helm: a pre-install/pre-upgrade Job; Azure: a manual job). The API and worker never change the schema, and their database login cannot.
- **Upgrade = check, back up, stop, migrate, start, smoke test.** The migrator has a read-only `--check` mode. It refuses (exit 4) when the database has migrations the build does not know, so an older version cannot be started against a newer database. Database changes are forward-only: **rollback is restoring the backup** taken by the upgrade script, which is stated plainly to the user.
- **Outage is accepted.** Upgrades take the application down for the migration. Zero-downtime (expand/contract migrations, rolling restarts across versions) is not designed in; migrations are written to be safe to run once and are not guaranteed compatible with the previous version's code.
- **Azure SQL** has no server-level logins in a user database, so the migrator creates a contained database user for the application when it detects Azure SQL (`EngineEdition` 5).
- **No registry credentials in the repository.** Secrets are Kubernetes Secrets, Key Vault references or the user's `.env`; the chart and the compose file only name them.
- **The web image is the unprivileged nginx** on port 8080 so every container can run non-root with a read-only file system.

## Consequences
- The Helm chart, the Bicep template and the shell scripts are verified in CI by linting, rendering, schema checks, compilation and script tests, **not by deploying them**. Their first real deployment will find things; treat 0.8.0 as a release candidate for those paths.
- The offline bundle is large (four images); the online bundle is small but needs registry access.
- The full upgrade and rollback of a running compose stack are not automated tests. They are rehearsed manually before a release (checklist in `docs/upgrading.md`).
- Kubernetes needs a `ReadWriteMany` volume for local file storage; otherwise use an object store.
