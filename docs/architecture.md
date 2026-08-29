# Architecture

## Projects

- `Monkeysphere.Core` owns domain types, validation, application commands/queries, and storage ports.
- `Monkeysphere.Data` owns SQLite/Dapper repositories and the application-owned DnaX manifest.
- `Monkeysphere.Web` owns the Blazor host, administrator authentication, UI, HTTP API, MCP tools, and deployment integration.

Core does not depend on Blazor, SQLite, Dapper, or DnaX. Data implements Core ports. Web composes both and is the only network host.

## Data boundaries

The application database and DnaX remote-access database are separate files under one configurable data root. Each owns an independent DnaX manifest and ledger. Remote API and MCP operations call Core query services; they do not receive direct database or filesystem access.

## Identifiers and values

Domain entities use UUIDv7 identifiers. Each record has one primary display name plus zero or more ordered alternate names in relational child rows. Alternate names are case-insensitively unique within a record, cascade with that record, and participate in bounded text search. Reusable field definitions carry an open string type identifier. Recognized scalar values use typed relational columns; tags use ordered child rows; unknown types use lossless text fallback.

Temporal values retain their explicit precision, a canonical sort key, an approximation flag, and an optional explanatory note. Relationship types use either directional forward/inverse labels or one symmetric label. Relationship rows reference two records relationally; symmetric endpoints are canonicalized to reject reverse-order duplicates.

Saved views are relational definitions with stable UUIDv7 identities. A view selects one record type, ordered field columns, up to ten typed filters, an optional grouping field, and display-name or field sorting. Field references use stable definition identities, so renames do not break views; retired fields remain visible until the administrator edits the view. View rows are rendered through authenticated Core queries rather than direct browser or filesystem access.

## Presets and first-run setup

The project-owned catalogue defines concrete, versioned record-type presets in Core. Installed record types and fields receive fresh local UUIDv7 identities while retaining preset provenance and canonical field keys. They use the same editable tables and services as administrator-created definitions; presets are not hard-coded subclasses.

Starter packs are curated selections over that catalogue. A new authenticated dataset can select one of four complexity levels, preview example items, and remove unwanted presets before confirming. The Data implementation installs all selected types, fields, applicable relationship types, and setup completion in one SQLite transaction. The explicit blank-slate choice writes completion without creating types. Migration 5 marks datasets that already contain record types as initialized so upgrades do not enter onboarding.
