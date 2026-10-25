# ADR-0002: One codebase for SaaS and on-premise
Status: Proposed
Decision: Deployment mode is configuration. Infrastructure concerns are interfaces with per-mode adapters. On-prem is
single-tenant, delivered as container images with Helm chart and compose files. Same migrations for both.
Rationale: avoids a fork, keeps features and fixes in step, and lets on-prem customers migrate to SaaS.
Consequences: every feature must work without cloud-only services; CI tests both modes; on-prem needs license and
upgrade tooling.
