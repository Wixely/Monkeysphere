# Roadmap

- Last reviewed: 2026-09-07
- Owner: Wixely / Agent unless otherwise noted

## MCP coverage for new features

Status: Adopted planning and completion requirement from 2026-09-07.

Basic application functionality should be usable through MCP as well as the browser. Every new feature or material extension must consider MCP during design, including its reads, mutations, configuration, and import/export workflows. Deliver applicable MCP support with the feature by default, using the same application services and validation.

- Record an MCP disposition in the feature plan and PR: **Included**, **Deferred**, or **Not applicable**.
- Included work specifies tools, permissions, domain selection, input/output limits, and relevant verification, and ships with the feature.
- Deferred work requires a concrete reason, a linked roadmap item, an owner (or TBD), and an ISO review date. Deferral must remain visible as incomplete coverage.
- Not applicable work requires a reason, such as a purely visual browser interaction or an operation that must run while the application is offline. Consider exposing its underlying data or configuration even when the interaction itself is browser-only.
- Review existing tools when extending a feature: new fields, filters, settings, and lifecycle actions must not silently leave the MCP contract behind.
- This requirement does not automatically enable remote access, expand existing credentials, or require a matching HTTP API endpoint for every tool.

Owner: Wixely / Agent. Next action: apply this checklist to every new feature plan and PR; review coverage on 2026-10-01.

## MCP instance management

Status: M0 in progress. Baseline reviewed: 2026-09-07.

Build out authenticated MCP management of application data and runtime administration, preserving domain isolation and the offline restore boundary. The [MCP implementation plan](mcp-management-plan.md) defines contracts, dependencies, delivery phases, acceptance criteria, and remaining decisions.

The [MCP options plan](mcp-options-plan.md) expands this into tool families, access profiles, settings, file-transfer choices, and client workflows. Pinned dependency inspection and a local chunk-transfer probe are documented; deployed-client verification and final contracts remain open. Local implementation status does not establish deployed availability.

| Milestone | Scope | Status | Owner | Review date |
| --- | --- | --- | --- | --- |
| M0 | Source/deployment inventory, capability discovery, contract and permission design | In progress; local discovery/schema tools and chunk probe pass | Agent | 2026-09-14 |
| M1 | Write authorization, concurrency, retry protection, previews, and audit foundations | In progress; revisions/receipts, failure audit, batch/deletion previews and media cleanup tested; other workflows pending | Agent | 2026-09-14 |
| M2 | Records, relationships, domain setup, and minimum structure creation | Implemented locally; full two-domain MCP setup, typed editing, relationships, structured search and reviewed deletion pass; live client/browser gates remain | Agent | 2026-09-21 |
| M3 | File transfer, vCard preview/apply/export, and record images | In progress; MCP upload/validation/preview/apply verified locally; contact export and images remain | Agent | 2026-09-21 |
| M4 | Complete field and record-type lifecycle management | Planned; depends on M2 | Agent | 2026-09-28 |
| M5 | Saved views, graph/map queries, calendar, reminders, and settings | Planned; depends on M2 | Agent | 2026-09-28 |
| M6 | Backup operations, operational status, and separately scoped remote administration | Planned; depends on M1 and M3 | Agent | 2026-10-01 |
| M7 | Coverage review, end-to-end verification, documentation, and staged release | Planned; depends on M3-M6 | Wixely / Agent | 2026-10-01 |

Dates are review checkpoints, not delivery commitments. Recommended next action: Agent implements bounded authenticated contact export while closing the remaining M0-M1 gates. Image transfer follows export to complete M3.

## Domain-separated spheres

Status: Implemented on `feature/domains`; pending review and merge.

Domains let one deployment hold independent spheres such as Personal friends, Online friends, or TV show characters. They are an organizational and data-isolation boundary for the single administrator, not a multi-user authorization system.

### Invariants

- The existing `monkeysphere.db` and `media/records` data remain in a stable, built-in Default domain. Its display name can be changed without moving data.
- Every additional domain has a separate, fully migrated SQLite database and media root under an opaque UUID directory. Record IDs cannot be resolved, related, searched, exported, graphed, mapped, or rendered through another domain.
- Record types, fields, presets, setup state, records, aliases, images, relationships, saved views, dashboard configuration, calendar data, reminders, imports, and map settings belong to exactly one domain.
- The selected domain is held in a protected, HTTP-only, SameSite cookie. Invalid or obsolete browser selections return to Default. Explicit invalid remote selectors fail closed.
- New domains start with their own first-run setup wizard. Duplicate structure and record names are valid in different domains.
- API callers select a non-default domain with `X-Monkeysphere-Domain`; MCP tools accept an optional `domainId`. Omission retains backwards-compatible Default behavior.
- Backups are deliberately deployment-wide: the domain registry, every domain database and original-media tree, and remote-access state are validated and restored as one unit.
- Cross-domain links are not supported. A future transfer/copy workflow must use an explicit preview and create independent destination records rather than weakening isolation.

### Delivery plan

| Work item | Status | Owner | Review date |
| --- | --- | --- | --- |
| Domain registry, renameable Default domain, and isolated database/media paths | Complete | Agent | 2026-09-03 |
| Header switcher and Settings / Domains management | Complete | Agent | 2026-09-03 |
| Per-domain setup, structures, records, relationships, views, dashboard, calendar, reminders, vCard, map, graph, and media | Complete | Agent | 2026-09-03 |
| Explicit API header and MCP domain selection | Complete | Agent | 2026-09-03 |
| Deployment-wide backup format 2 with format 1 compatibility and atomic restore | Complete | Agent | 2026-09-03 |
| Isolation, cookie, remote-surface, backup/restore, and browser regression tests | Complete | Agent | 2026-09-03 |
| Domain deletion/archive and previewed record transfer/copy | Later; design required | TBD | 2026-10-01 |
| Per-domain visual identity and optional structure-template duplication | Later; user research | TBD | 2026-10-01 |

## Release follow-up

- Produce a clean DnaX package release whose compiled assemblies do not contain local build paths, then update Monkeysphere before the next public prerelease. Owner: Wixely / Agent; approval required for the separate DnaX repository.
- Complete live privileged Windows Service and installed-systemd lifecycle verification. Owner: Wixely / Agent.

## Saved graph layouts

Status: Implemented on `feature/domains`; pending review and merge.

- Saved graph views persist bounded coordinates for every displayed record through DnaX migration 19.
- Existing nodes retain their saved or current coordinates when filters refresh or new matching records appear.
- Initial layout, newly introduced nodes, and drag completion enforce deterministic minimum spacing so nodes do not overlap.
- Later consideration: user-configurable spacing and an explicit automatic relayout action. Owner: TBD; review date: 2026-10-01.
