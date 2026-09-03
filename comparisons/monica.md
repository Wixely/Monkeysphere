# Monkeysphere compared with Monica

Reviewed: 2026-09-03

## Summary

[Monica](https://github.com/monicahq/monica) is an established personal relationship manager built primarily around people, their relationships, and a history of interactions. Monkeysphere overlaps with that purpose, but its core model is broader: people, pets, places, vehicles, media, events, and administrator-defined record types all use the same configurable record and relationship system.

The clearest product distinction is:

- **Monica is a personal CRM:** it helps someone maintain relationships with people.
- **Monkeysphere is a private personal knowledge graph:** it helps someone remember people and the wider world connected to them.

Monkeysphere should not try to reproduce every Monica feature. It should preserve its flexible record model, explicit uncertainty, visual exploration, and low-dependency self-hosting while learning from Monica's mature relationship-oriented workflows.

## Comparison baseline

This comparison uses the Monkeysphere implementation in this repository and Monica's public repository and documentation as reviewed on 2026-09-03. Monica's default `main` branch identifies itself as a development beta and points users to the `4.x` branch for the stable version. Some capabilities described below are therefore branch-dependent; this document avoids treating every `main` feature as available in the stable release.

Monkeysphere is currently an early work in progress preparing for its initial alpha release. Monica has a long-running public project and a substantially larger production feature set. A checked box should not be read as equivalent maturity, depth, or operational history.

## Product and feature comparison

| Area | Monkeysphere | Monica | Assessment |
| --- | --- | --- | --- |
| Primary purpose | Remember arbitrary people, places, possessions, companions, events, and their connections | Manage personal and professional relationships with people | The products overlap around people but have different centres of gravity |
| Core data model | Administrator-defined record types, reusable typed fields, premade presets, and generic relationships | Contacts inside private vaults, with dedicated relationship and life-management features | Monkeysphere is more structurally general; Monica is more purpose-built |
| People | Person preset, aliases, images, configurable fields, dates, locations, and relationships | Rich contact sheets, contact methods, family/social relationships, notes, labels, and other dedicated contact features | Monica currently provides the deeper personal-CRM workflow |
| Non-person records | First-class presets for pets, homes, workplaces, favourite places, vehicles, books, games, plants, trips, events, and more | Pets and related life information are primarily associated with contacts | This is a defining Monkeysphere advantage |
| Custom structure | Editable record types, reusable fields, relationship types, versioned presets, retirement, merge, and conversion workflows | Custom contact field types, genders, activity types, labels, and configurable contact sections | Both are customizable, but Monkeysphere exposes a more generic schema system |
| Names and aliases | One display name plus ordered aliases, nicknames, former names, and local names on every record | Dedicated contact naming model and configurable name presentation | Monkeysphere applies aliases uniformly beyond people |
| Relationships | Generic directional or symmetric relationships between any records | Contact relationships and a contact's social graph | Monkeysphere supports broader graph semantics; Monica has stronger people-specific semantics |
| Relationship visualization | Interactive graph with type filters, connected/isolated modes, saved graph views, images, and direct record opening | Relationship information is part of the contact-oriented experience | Monkeysphere makes graph exploration a primary workflow |
| Activities and interaction history | Can represent events through records and relationships, but has no dedicated call, conversation, or activity timeline | Dedicated activities, calls/interactions, notes, how-you-met context, and activity types | This is one of Monkeysphere's largest functional gaps for personal-CRM use |
| Tasks and follow-ups | No general task system | Dedicated tasks and relationship follow-up workflows | Monica is stronger |
| Journal and mood | No dedicated journal or mood tracker | Journal/diary and day or mood tracking | Monica is stronger; this may remain outside Monkeysphere's intended scope |
| Reminders | Private in-app reminders on eligible date fields with multiple local lead times | General reminders, automatic birthday reminders, and notification-channel support | Monica is broader operationally; Monkeysphere keeps reminder data local and field-driven |
| Calendar | Exact-date calendar, upcoming dates, filtering, and manual iCalendar export | Vault calendar and reminders | Both cover important dates; Monkeysphere deliberately excludes approximate or coarse dates from exact calendar cells |
| Temporal uncertainty | Century through second precision, with explicit approximation metadata | Contact and reminder dates, including support for some unknown ages/dates | Explicit precision and approximation are distinctive Monkeysphere capabilities |
| Places and maps | Structured locations, measurement accuracy, approximation radius, provider-free coordinate entry, spatial index, and multi-record map | Contact addresses; the development configuration provides optional LocationIQ and Mapbox integrations | Monkeysphere is stronger for private, uncertainty-aware place data and avoids runtime map-provider requests |
| Images and documents | Multiple images per record, cover selection, captions, ordering, rotation/crop metadata, private originals, and generated previews | Photos and document uploads | Monica includes general documents; Monkeysphere has a more deliberately bounded image pipeline but no general document model |
| Search and reusable views | Bounded record search across names, aliases, and values; saved grid and graph views | Contact search, labels, reports, and configurable contact presentation | The approaches differ: query/view reuse versus contact organization and reporting |
| Dashboard | Configurable ordered record categories plus recurring upcoming dates | Vault dashboard and configurable default presentation | Both are configurable, with Monkeysphere oriented around mixed record categories |
| Import and export | Previewed vCard 3.0/4.0 import with duplicate evidence and explicit create/skip/merge/replace choices; scoped vCard 4.0 export | vCard export and address-book/DAV work are present in the public project; broader account data workflows depend on version | Monkeysphere currently emphasizes safe, reviewable contact ingestion rather than broad synchronization |
| Backup and restore | Versioned, checksummed application backup including both SQLite databases and referenced original media; offline validated restore | Conventional application/database/storage backup procedures | Monkeysphere has a more opinionated in-application backup format |
| Users and privacy partitions | One administrator account and one dataset | Multiple accounts/users and private vaults with membership boundaries | Monica is substantially stronger for shared or partitioned deployments |
| Remote integration | Disabled-by-default, read-only HTTP API and MCP surfaces with separate credentials | Public application API and a broader integration ecosystem | Monkeysphere is intentionally narrower and fail-closed today |
| Localization | English UI at present | Public project advertises translations in 27 languages | Monica is substantially stronger |
| Maturity | Unpublished work in progress | Long-running open-source project with many releases and a large community | Monica is the safer current choice for a proven personal CRM |

## Technical and operational comparison

| Area | Monkeysphere | Monica |
| --- | --- | --- |
| Server stack | C# on .NET 10, ASP.NET Core, Blazor | PHP 8.3+, Laravel 12, Inertia, and Vue 3 on the development branch |
| Browser toolchain | No Node.js runtime or asset build pipeline; required assets are vendored | Vite, Vue, Tailwind CSS, and Yarn-based frontend tooling on the development branch |
| Persistence | SQLite with Dapper and checksummed DnaX migrations | Laravel database layer; the development configuration defaults to SQLite, while documented Docker arrangements commonly use MariaDB and supporting services |
| Deployment | Interactive executable, Windows Service integration, systemd template, and Docker | Web application deployment and Docker images, commonly with a database, queue worker, and scheduler |
| External services | Normal operation deliberately avoids public CDN, tile, geocoding, notification, and analytics dependencies | Can integrate with mail, search services, Uploadcare, LocationIQ, Mapbox, Telegram, and other infrastructure depending on configuration |
| License | MIT | AGPL-3.0-or-later |

The license difference matters. Monica is an excellent behavioral reference, but AGPL-covered source must not be copied into Monkeysphere's MIT codebase. Product ideas should be independently designed and implemented.

## Where Monica is currently stronger

For someone whose only goal is relationship management, Monica currently offers the more complete experience:

- dedicated calls, conversations, activities, notes, tasks, and follow-ups;
- richer people-specific organization and contact presentation;
- journals, mood tracking, gifts, debts, and other life-management workflows;
- multi-user accounts and privacy-separated vaults;
- notification delivery and a broader API/integration surface;
- localization, community history, and production maturity.

These are meaningful product gaps, not merely missing presets. Modeling a call as a generic Event record, for example, is possible in Monkeysphere but does not yet provide the fast capture and chronological contact history of a dedicated interaction feature.

## Where Monkeysphere is distinct or stronger

- Every important object can be first-class. A home, cat, vehicle, book, trip, workplace, or custom concept does not need to be squeezed into a contact-owned accessory.
- Relationships can connect any two records and can be explored as a filtered visual graph.
- Date and time values preserve what the user actually knows, including century, decade, year, month, day, minute, or second precision and explicit approximation.
- Locations preserve descriptive context, coordinate accuracy, and approximate radius without pretending uncertain information is exact.
- Maps work without sending private places to a tile or geocoding provider.
- Presets remain editable local structures with provenance rather than hard-coded entity subclasses.
- Schema changes have previewed merge and conversion paths designed to avoid silent data loss.
- vCard import presents duplicate evidence and requires an explicit resolution for each proposed contact.
- The default deployment remains small: one application, SQLite storage, local assets, and no required queue, search, mail, or third-party service.
- Backup creation and restore validation are application-aware rather than being left entirely to database and filesystem procedures.

## Recommendations for Monkeysphere

### Preserve the broader identity

Do not reposition Monkeysphere as a Monica clone. “Private personal knowledge graph” is a more accurate and defensible identity than “another personal CRM.” People should remain the best-supported default record type, but not a privileged database entity that limits the rest of the model.

### Add an interaction timeline before copying peripheral modules

The most valuable lesson from Monica is not gifts, debts, or mood tracking. It is that relationship memory needs a quick chronological answer to “what happened with this person?” A future interaction capability should:

- capture a call, message, visit, shared activity, or free-form note quickly;
- connect one interaction to multiple people, places, or other records;
- display a chronological timeline on every involved record;
- support approximate timestamps using the existing temporal model;
- be implemented as a reusable record/preset pattern where possible, with dedicated UI only where it materially improves capture and browsing.

This would strengthen the People use case without compromising the generic graph.

### Keep tasks and outbound notifications separate decisions

Tasks, recurring “stay in touch” prompts, email, and push delivery introduce workflow and privacy obligations beyond the current reminder model. They should be evaluated deliberately instead of being treated as automatic parity work.

### Treat multi-user support as an architectural project

Monica's accounts and vaults are not a small feature. Equivalent support would affect authorization, database ownership, media paths, backups, APIs, MCP, and migrations. Monkeysphere should remain explicitly single-administrator until a complete tenancy and sharing model is designed.

### Build migration around open formats, not Monica's database schema

The safest initial Monica migration path is:

1. import people through vCard;
2. report unsupported Monica concepts before writing anything;
3. later add a separately versioned importer for exported activities, notes, reminders, and relationships if Monica exposes a stable documented format;
4. preserve source identifiers as provenance so repeated imports can be reconciled;
5. never require direct access to Monica's internal database tables.

The current vCard duplicate-resolution workflow is a good foundation, but it is not a full Monica migration because activities, journal entries, tasks, vault boundaries, documents, and other application-specific data do not fit in ordinary vCards.

## Choosing between them today

Choose Monica when the primary need is a mature personal CRM centred on contacts, interaction history, follow-ups, journals, multiple users, or localization.

Choose Monkeysphere when the primary need is a compact self-hosted system for flexible record types, arbitrary relationships, visual graph and map exploration, uncertainty-aware dates and places, and minimal external infrastructure—and when using work-in-progress software is acceptable.

They can also be complementary during Monkeysphere's development: Monica can remain the operational relationship manager while Monkeysphere is used for broader structured memory and graph exploration.

## Sources

Monica sources reviewed on 2026-09-03:

- [Monica repository and feature list](https://github.com/monicahq/monica)
- [Monica next-version documentation: accounts, vaults, and contacts](https://github.com/monicahq/docs-gitbook/blob/main/README%20%281%29.md)
- [Monica application dependencies](https://github.com/monicahq/monica/blob/main/composer.json)
- [Monica frontend dependencies](https://github.com/monicahq/monica/blob/main/package.json)
- [Monica example configuration](https://github.com/monicahq/monica/blob/main/.env.example)
- [Monica routes](https://github.com/monicahq/monica/blob/main/routes/web.php)
- [Monica releases](https://github.com/monicahq/monica/releases)
- [Monica Docker documentation](https://github.com/monicahq/docker)

Monkeysphere sources are the repository's [README](../README.md), [architecture](../docs/architecture.md), [security model](../docs/security.md), and current implementation and tests.
