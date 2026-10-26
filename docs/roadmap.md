# Roadmap (one phase at a time)

0. **Foundations** (in progress, see [phase-0-summary](phase-0-summary.md)): solution skeleton, CI, Docker, tenancy, auth, audit, outbox, storage/queue abstractions, i18n/RTL shell, deployment-mode switch, tenant-isolation tests.
1. **Core & Assets** (done; see [phase-1-summary](phase-1-summary.md)): org/site/building/space hierarchy, asset register, QR tags, custom fields, documents, meters.
2. **Helpdesk & Work Orders** (2a requests and lifecycle done, see [phase-2a-summary](phase-2a-summary.md); 2b SLA and notifications done, see [phase-2b-summary](phase-2b-summary.md); 2c checklists, time, costs, signatures and photos done, see [phase-2c-summary](phase-2c-summary.md); 2d technician PWA with offline sync done, see [phase-2d-summary](phase-2d-summary.md)).
3. **Preventive Maintenance** (done; see [phase-3-summary](phase-3-summary.md)): templates, schedules (fixed or floating), meter triggers, forecast calendar, compliance KPIs.
4. **Inventory** (done; see [phase-4-summary](phase-4-summary.md)): stores, stock, reservations on WOs, reorder, stock take.
5. **Vendors & Contracts**: vendors, AMC/SLA contracts, purchase requests/orders, vendor portal, ERP adapter (Dynamics 365 first).
6. **Spaces, Bookings, Visitors**: floor plans, allocation, booking with calendar sync, visitor flow and badges.
7. **Reporting & Admin**: dashboards, KPI pack, BI feed, workflow/SLA/custom-field designers.
8. **Packaging & Licensing**: Helm chart, on-prem bundle, license service, upgrade tooling, hardening and pen test.
9. **Later**: IoT/energy, compliance & permits, projects, lease, AI.

Each phase ships with tests and updated docs. Do not start the next phase until told.
