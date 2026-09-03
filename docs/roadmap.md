# Roadmap

- Last reviewed: 2026-09-03
- Owner: Wixely / Agent unless otherwise noted

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
