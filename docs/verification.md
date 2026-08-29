# Verification status

Last verified: 2026-08-29

## Verified on Windows

- `eng/Build.ps1` restored in locked mode, built Release with zero warnings, and passed 48 tests.
- `dotnet format Monkeysphere.slnx --verify-no-changes --no-restore` passed.
- DnaX historical verification constructed and upgraded every Monkeysphere schema version to the same canonical schema.
- Automated tests cover authentication and antiforgery, cookie transport behavior, restart persistence, temporal precision, relationship lifecycle and directionality, remote-disabled defaults, DnaX API route and credential rotation, scope denial, credential separation, relationship retrieval, and an MCP tool call.

## Not yet verified

- Visual and interactive browser behavior. The available browser-control workflow requires Node.js, which this repository does not authorize.
- A separate native Windows process smoke test. Background process creation was denied by the current execution policy; equivalent startup, migration, health, authentication, and restart behavior is covered through production-host integration tests.
- Windows Service installation, start, stop, upgrade, and recovery behavior.
- Docker image build and persistent-volume smoke tests. Docker is not installed in the current environment.
- Linux interactive and systemd operation. No Linux execution environment was available.

These are verification gaps, not support claims. Re-run the full suite and update this file when an appropriate environment is available.
