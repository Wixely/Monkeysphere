# Verification status

Last verified: 2026-08-29

## Verified on Windows

- `eng/Build.ps1` restored in locked mode, built Release with zero warnings, and passed 57 tests.
- `dotnet format Monkeysphere.slnx --verify-no-changes --no-restore` passed.
- DnaX historical verification constructed and upgraded every Monkeysphere schema version to the same canonical schema.
- Automated tests cover authentication and antiforgery, cookie transport behavior, restart persistence, alias persistence, update validation and search, image decoding and normalization, metadata persistence, authenticated image delivery, image deletion and record cascading, temporal precision, relationship lifecycle and directionality, saved-view CRUD, multiple filters, field grouping/sorting, rename/retirement stability, preset provenance, customizable transactional starter installation, persistent blank-slate setup, wizard example rendering, remote-disabled defaults, DnaX API route and credential rotation, scope denial, credential separation, relationship and image-metadata retrieval, and an MCP tool call.
- An MCP-driven browser smoke test verified `admin` / `admin` login, record-type and field creation, saved-view creation and duplication, record creation, saved filter application, grouping, and configured table-column rendering in Chromium.
- A separate Release process smoke test applied DnaX migration 5 to a fresh SQLite data root, accepted the default administrator login, and served the authenticated four-tier wizard and its examples successfully.

## Not yet verified

- Comprehensive visual, responsive, and accessibility review beyond the focused saved-view browser workflow.
- Windows Service installation, start, stop, upgrade, and recovery behavior.
- Docker image build and persistent-volume smoke tests. Docker is not installed in the current environment.
- Linux interactive and systemd operation. No Linux execution environment was available.

These are verification gaps, not support claims. Re-run the full suite and update this file when an appropriate environment is available.
