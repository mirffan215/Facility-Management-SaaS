# ADR-0003: Technician app as offline-first PWA
Status: Proposed
Decision: Build the technician experience as a PWA (service worker, IndexedDB, background sync) with client-generated
IDs and idempotent sync endpoints. Native wrapper only if device APIs require it later.
Rationale: one frontend stack, no app-store dependency for on-prem customers.
Consequences: limited background capabilities on iOS; revisit if NFC/Bluetooth needs grow.
