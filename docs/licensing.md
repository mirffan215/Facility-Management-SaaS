# Licensing guide

For the vendor (issuing) and for the customer's administrator (installing). Design and reasons: [ADR-0023](adr/0023-licensing.md).

## Vendor: make keys once
```
dotnet run --project tools/Fms.LicenseTool -- keygen --out /safe/offline/place --key-id fms-2027
```
Keep the `.private.pem` offline and backed up. Put the printed public entry into `src/BuildingBlocks/Fms.BuildingBlocks/Licensing/trusted-keys.json` (replace the `dev-2026` entry), then build the release. `scripts/check-release-keys.sh` refuses a release that still trusts a development key.

## Vendor: issue a license
```
dotnet run --project tools/Fms.LicenseTool -- sign --key /safe/offline/place/fms-2027.private.pem --key-id fms-2027 \
  --customer "Acme Estates" --edition Professional --modules work,pm,inventory,reporting \
  --expires 2027-12-31 --max-users 100 --tenant <the installation's tenant id> --out acme.license
```
`--modules '*'` means everything. `--tenant` binds the license to one installation (the `FMS_TENANT_ID` in the bundle's `.env`); leave it out for an unbound license. `--grace-days` defaults to 30. Check a file with `fms-license inspect acme.license`.

Modules: `work`, `pm`, `inventory`, `vendors`, `purchasing`, `spaces`, `bookings`, `visitors`, `reporting`.

## Customer: install or renew
Put the file where the API reads it:

| Setup | Where |
|---|---|
| Docker Compose bundle | `./license/fms.license` next to the compose file (mounted at `/etc/fms/license`) |
| Helm | `kubectl create secret generic fms-license --from-file=fms.license=acme.license` (name set by `license.existingSecret`) |
| Any | `License__Path` (a file) or `License__Text` (the file's text) |

A new or renewed file is picked up within about a minute, with no restart. **Administration, License** shows the state, expiry, modules and user count.

## What happens near and after expiry
30 days of grace (or what the license says): everything works with a banner. After that the installation is **read-only**: people can still sign in, read and export; nothing can be changed until a renewed file is installed. A license for another installation, a damaged file, or no file at all behaves the same way.
