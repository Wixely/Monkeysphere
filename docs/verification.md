# Verification status

Last verified: 2026-08-29

## Verified on Windows

- `eng/Build.ps1` restored in locked mode, built Release with zero warnings, and passed 94 tests.
- `dotnet format Monkeysphere.slnx --verify-no-changes --no-restore` passed.
- Authenticated editor rendering and static-asset tests cover the private map-pin control and vendored OpenLayers delivery.
- Core, SQLite integration, and authenticated rendering tests cover map-query bounds, single and multi-field filters, pagination, R-tree migration history, approximation-radius viewport intersection, structured location summaries, and private map access.
- Backup integration tests create and revalidate a package containing both online SQLite snapshots and one original image, while proving that derivatives and data-protection keys are excluded.
- An offline restore integration test changes live data after backup, restores the earlier package with rollback retention, restarts against the restored data root, and verifies database rollback plus lazy image-derivative regeneration.
- Schedule tests cover daily, weekly, and end-of-month recurrence calculations; scheduling remains off by default in the application and Docker configuration.
- Core, SQLite integration, and authenticated rendering tests cover graph limits, alias search, relationship-type filtering, neighbour depth, deterministic node truncation, private route protection, and vendored Cytoscape.js delivery.
- DnaX historical verification constructed and upgraded every Monkeysphere schema version to the same canonical schema.
- Automated tests cover authentication and antiforgery, cookie transport behavior, restart persistence, alias persistence, update validation and search, structured-location validation, coordinate normalization, context-only approximations, persistence, search/filtering and remote serialization, image decoding and normalization, captions, cover selection, ordering, non-destructive correction validation, authenticated derivative and original delivery, byte-identical original retention, image deletion/promotion and record cascading, temporal precision, exact-date calendar inclusion and filtering, approximate/coarse-date exclusion, authenticated calendar rendering and export, iCalendar escaping and UTF-8 folding, reminder eligibility, duplicate prevention, persistence across date edits, dismissal and cleanup, vCard 3.0/4.0 parsing, malformed-input rejection, semantic extension round-tripping, mapping preview, exact-import and content duplicate detection, create/skip/merge/replace behavior, whole-batch rollback, authenticated selected-contact export, relationship lifecycle and directionality, saved-view CRUD, multiple filters, field grouping/sorting, rename/retirement stability, previewed compatible-field merging, explicit conflict resolution, stale-preview rejection, saved-view reference migration, fail-closed field conversion, record-type retirement, record/type/view migration, required-rule relaxation, preset provenance and versioning, customizable transactional starter installation, persistent blank-slate setup, wizard example rendering, remote-disabled defaults, DnaX API route and credential rotation, scope denial, credential separation, relationship and image-metadata retrieval, and an MCP tool call.
- An MCP-driven browser smoke test verified `admin` / `admin` login, record-type and field creation, saved-view creation and duplication, record creation, saved filter application, grouping, and configured table-column rendering in Chromium.
- A separate Release process smoke test applied DnaX migration 5 to a fresh SQLite data root, accepted the default administrator login, and served the authenticated four-tier wizard and its examples successfully.

## Not yet verified

- Comprehensive visual, responsive, and accessibility review beyond the focused saved-view browser workflow.
- Windows Service installation, start, stop, upgrade, and recovery behavior.
- Docker image build and persistent-volume smoke tests. Docker is not installed in the current environment.
- Linux interactive and systemd operation. No Linux execution environment was available.

These are verification gaps, not support claims. Re-run the full suite and update this file when an appropriate environment is available.
