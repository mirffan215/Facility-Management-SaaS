# Upgrading and rolling back

Applies to the Compose bundle (`upgrade.sh`, `rollback.sh`). For Helm, `helm upgrade` runs the migrator as a Job first; for Azure, start the migrate job before moving traffic. Reasons: [ADR-0025](adr/0025-packaging-and-upgrades.md).

## Principles
- **Database changes only go forward.** Every release migrates the database; the older version cannot run on it. The migrator refuses to run, and `--check` reports, when the database is *newer* than the version you are starting, so an old version can never quietly damage a newer database.
- **The way back is a backup.** Rolling back restores the database taken just before the upgrade; everything entered since is lost. So upgrade at a quiet time.
- **No skipping the backup.** The script makes and verifies one (bundled SQL Server) or refuses until you confirm you made one (your own SQL Server).
- A short outage is expected: the application is stopped while the database migrates.

## Upgrade (Compose)
1. Unpack the new bundle **next to** the old one; copy `.env`, `license/` over (or keep a shared folder).
2. `cd fms-onprem-<new>` and run `./upgrade.sh` (add `--backup-confirmed` if you use your own SQL Server and took a verified full backup of `Fms` just now; add `--yes` to skip the question).
3. The script: checks the version order and disk space; loads images (offline bundle); runs the migrator in **check** mode to list what will change (and stops if the database is newer than this version); backs up and verifies; stops the app; migrates; starts the new version; waits for readiness; checks `/api/v1/system/info` reports the new version.
4. Read the release notes for anything to do by hand. Keep the backup until you are satisfied.

## Roll back (Compose)
From the **old** version's bundle folder: `./rollback.sh <backup file name> <old version>` (bundled SQL Server; asks you to type RESTORE), or restore the backup yourself and run `./rollback.sh --restored <old version>`. It restores, starts the old version and waits until it is ready.

## If the migration fails halfway
The script tells you and stops; the database may be partly changed. Roll back with the backup it just made. Do not start the old version on the partly migrated database.

## Checking without changing anything
`docker compose --profile migrate run --rm migrator --check` lists pending migrations (exit 0) or reports a newer database (exit 4).

## What is tested
The migrator's check mode and its refusal of a newer database are integration-tested against SQL Server; the helper functions and refusals of the scripts are tested in CI (`scripts/test-onprem-scripts.sh`). **`scripts/rehearse-onprem.sh` does the whole thing for real** (build two versions, bundle them, install with the bundled SQL Server, upgrade with the backup, enter data, roll back, check the data is gone, and check a second install is refused). It is a job in CI (images from the real Dockerfiles). It was first run in Phase 11 on a development machine with images built from that machine's own build output (`--host-build`), and it found five defects in the bundle that are now fixed (see the Phase 11 summary). It does not cover your own SQL Server, an offline bundle with images, or a real identity provider and license: try those on a copy before relying on them.

The compose project is called `fms` whatever the bundle's folder is called, so the upgrade run from the next version's folder finds the same database, files and backups.
