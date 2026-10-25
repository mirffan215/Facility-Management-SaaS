# ADR-0003: Technician client: PWA first, native optional
Status: Proposed (supersedes the earlier "offline-first PWA" draft)

Decision: The technician experience is built against a client-agnostic API (idempotent sync endpoints, client-generated
IDs, delta pull by version token) so several clients can coexist:
1. **PWA (first)**: service worker, IndexedDB, background sync. Same React stack, no app-store dependency, works for on-prem.
2. **Native (React Native or .NET MAUI), when needed**: for NFC, Bluetooth, reliable background sync, MDM distribution.
3. **Third-party/contractor clients**: via the public REST API and webhooks.

The sync contract is the product; the client is replaceable. The native technology choice is deferred to an ADR
when a customer requirement (NFC/BLE/background GPS) justifies it.

Consequences: sync API design and conflict rules (server wins for status, merge for notes and readings) are fixed in
the Work Orders phase before any client is built; no client may rely on server-side session state.
