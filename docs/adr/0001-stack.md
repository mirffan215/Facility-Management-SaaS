# ADR-0001: Technology stack
Status: Proposed
Decision: ASP.NET Core 10 modular monolith with Clean Architecture, React/TypeScript (Vite), SQL Server + EF Core 10,
OIDC auth, same standards as VPMS.
Rationale: one skill set and shared building blocks across products; SQL Server runs both in Azure and on-prem.
Consequences: on-prem customers need SQL Server licensing; Postgres not supported in v1.
