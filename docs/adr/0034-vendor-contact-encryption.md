# ADR-0034: Encrypting vendor contact details (Phase 12c)
Status: Proposed. Extends [ADR-0032](0032-security-follow-ups.md).

## Decisions
- **Vendor and vendor-contact e-mail and phone are encrypted** with the same key ring and format as visitors' details (`enc1:<key id>:...`), each column with its own name bound in as associated data (`vendors.Vendors.Email`, `.Phone`, `vendors.Contacts.Email`, `.Phone`). Columns were widened to 1600 / 400 characters by a migration.
- **Why now**: ADR-0032 left them out believing they were searched. They are not: vendor search matches code, name, Arabic name and tax number only. Nothing queries, sorts, indexes or joins on these four columns.
- **Not encrypted, on purpose**: users' sign-in e-mail (how people are found when they sign in), portal invitation e-mail (matched when accepted), vendor names and tax numbers (searched, unique), and bank details (the system holds none: it prepares no payments).
- `Fms.Migrator --encrypt-existing` now protects visits, vendors and vendor contacts in one run, reports each count, and re-protects everything with the current key after a rotation. It is safe to run repeatedly.

## Consequences
- The widened columns are not narrowed by the migration's `Down`; narrowing with encrypted values present would fail, so rolling back past it needs the values to be decrypted first.
- A lost key means lost vendor contact details (same as visitors). Back keys up apart from the database.
- Anyone who later adds a search on e-mail or phone must find the person another way (for example a hash column); they cannot be matched in the database.
