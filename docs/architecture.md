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

Temporal values retain their explicit precision, a canonical sort key, an approximation flag, and an optional explanatory note. Structured location values use relational child rows containing optional descriptive context, an optional WGS84 coordinate pair rounded to seven decimal places, optional measurement accuracy in metres, and an optional user-declared approximation radius in kilometres. Context-only approximate areas remain valid, while coordinate accuracy requires coordinates. Relationship types use either directional forward/inverse labels or one symmetric label. Relationship rows reference two records relationally; symmetric endpoints are canonicalized to reject reverse-order duplicates.

Saved views are relational definitions with stable UUIDv7 identities. A view selects one record type, ordered field columns, up to ten typed filters, an optional grouping field, and display-name or field sorting. Field references use stable definition identities, so renames do not break views; retired fields remain visible until the administrator edits the view. View rows are rendered through authenticated Core queries rather than direct browser or filesystem access.

The authenticated calendar is a read model over record field values and introduces no duplicate event storage. Exact-date values and non-approximate temporal values with day precision are eligible for day placement; approximate values and century, decade, year, or month precision are excluded because they cannot honestly identify one day. Calendar queries are bounded to 367 days and 1,000 entries, accept optional record-type and field-definition filters, and return stable record links for both the month grid and upcoming-date list.

In-app reminders use DnaX migration 10 and identify a logical record field value by record, field definition, and ordinal. This preserves an active reminder when the date or unrelated record details change, removes it when the eligible value is removed, and lets field merge/conversion workflows carry the reminder forward. Reminders remain behind administrator authentication and have no external notification transport. Manual iCalendar export applies the same bounded eligibility query, emits all-day RFC 5545-style events with stable local UIDs and folded UTF-8 content lines, and is returned as a private, non-cacheable attachment rather than a hosted feed.

vCard interoperability is an authenticated, one-time transfer workflow for the installed Person preset. A bounded UTF-8 parser accepts versions 3.0 and 4.0, unfolds and validates content lines, preserves property groups, parameters, raw values, and extensions, and fingerprints each card. Preview maps the first compatible email, phone, birthday, URL, and note to canonical preset fields; additional or incompatible values remain opaque. Duplicate evidence combines exact prior-import fingerprints with names, aliases, email, and phone values. The administrator chooses create separately, skip, merge non-conflicting values, or replace mapped values for every contact before one SQLite transaction begins.

DnaX migration 11 stores import fingerprints and semantic property provenance. Mapped provenance follows field merge/conversion; opaque properties, including grouped Apple labels, survive export even when the local field model cannot display them. vCard 4.0 export regenerates current names and mapped values, reuses applicable source parameters/groups, includes preserved opaque properties, folds UTF-8 lines, and is limited to 100 explicitly selected Person records per download. Imported source credentials and files are never retained.

Field-schema evolution is an authenticated Core workflow over the existing relational identities. A compatible merge requires identical type identifiers and configurations, previews attachments, values, record conflicts, and saved-view references, and applies an explicit conflict policy in one SQLite transaction. Duplicate attachments preserve the stricter required setting; duplicate saved-view columns collapse onto the retained definition. The source definition remains as a retired history row. Preview results include a deterministic revision fingerprint over affected definitions and references; the mutation transaction recalculates it and rejects stale confirmation.

A conversion previews every source value against a proposed new local definition. Scalar conversions reuse the ordinary target validator; exact dates can become day-precision temporal values and only exact, non-approximate day temporal values can become exact dates. Tags, locations, and richer temporal values are not flattened. Application is all-or-nothing: create the new definition, replace validated values, move type and saved-view references, and retire the source in one transaction. These operations change data but not the database schema, so they require no new DnaX schema migration.

## Record images

Images are a record-level capability rather than configurable preset fields. SQLite stores ordered metadata and cascades it with the owning record. Validated originals and generated display derivatives live under opaque UUID-based paths within `media/records` beneath the configured writable data root. The application decodes JPEG, PNG, and WebP uploads with bounded byte, dimension, and pixel limits, then creates metadata-stripped WebP previews and thumbnails with SkiaSharp. Only those derivatives are served, through authenticated endpoints with private no-store caching and MIME-sniffing disabled; original filenames never determine filesystem paths.

## Presets and first-run setup

The project-owned catalogue defines concrete, versioned record-type presets in Core. Installed record types and fields receive fresh local UUIDv7 identities while retaining preset provenance and canonical field keys. They use the same editable tables and services as administrator-created definitions; presets are not hard-coded subclasses.

Starter packs are curated selections over that catalogue. A new authenticated dataset can select one of four complexity levels, preview example items, and remove unwanted presets before confirming. The Data implementation installs all selected types, fields, applicable relationship types, and setup completion in one SQLite transaction. The explicit blank-slate choice writes completion without creating types. Migration 5 marks datasets that already contain record types as initialized so upgrades do not enter onboarding.

Record types have active or retired lifecycle state through DnaX migration 9. Retirement keeps records, fields, views, preset provenance, and stable identity intact while blocking new records and attachments. A type merge previews record, view, field, and requiredness effects with a revision fingerprint, then moves records and views to an active target transactionally. Shared attachments remain required only when both schemas required them; source-only fields append as optional; target-only requirements relax when moved source records could not satisfy them. The retained target keeps its identity and provenance, and the source remains as retired history with its original field attachments.

The Home, Workplace, Favourite Place, and Event catalogue entries are version 2 after replacing their version 1 descriptive-location and separate-radius fields with one structured location field. Fresh installations receive version 2. Existing locally owned version 1 definitions remain unchanged until an explicit preset-upgrade workflow is implemented.
