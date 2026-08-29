# Monkeysphere

Monkeysphere is a private, self-hosted relationship-memory application for records about people, pets, and administrator-defined entity types. It is an unpublished work in progress.

## Current vertical slice

The first slice provides one administrator account, configurable record types and reusable typed fields, record editing, bounded search/filtering, SQLite/Dapper persistence, checksummed DnaX migrations, and disabled-by-default read-only HTTP API and MCP surfaces.

## Requirements

- .NET SDK 10.0.300 or a compatible later 10.0 feature band.
- Windows PowerShell 5.1 for repository scripts.
- An administrator password supplied through configuration or a secret file. Never commit it.

No Node.js or Python toolchain is used.

## Build and test

```powershell
.\eng\Build.ps1
```

For local development, store the password outside the repository and point `MONKEYSPHERE_ADMIN_PASSWORD_FILE` at that file, or set `MONKEYSPHERE_ADMIN_PASSWORD` only in the process environment. Then run:

```powershell
.\eng\Run.ps1
```

The default development address is `http://localhost:5080`. Production deployments serve HTTP and must use a correctly configured trusted HTTPS reverse proxy when transport confidentiality is required.

## Data

`MONKEYSPHERE_DATA_ROOT` selects the mutable data root and defaults to `data` beneath the content root. Application data and DnaX remote-access state use separate SQLite files and separate migration ledgers.

## Deployment

- Interactive Windows: use `eng/Run.ps1` or `dotnet run`.
- Windows Service: the host detects service execution; installation guidance will be verified before support is claimed.
- Linux/systemd: a unit template is under `deploy/systemd`; verification remains required.
- Docker: use `docker compose up --build` after creating the administrator password file referenced by `compose.yaml`.

See [architecture](docs/architecture.md), [security](docs/security.md), [dependencies](docs/dependencies.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## License

MIT. See [LICENSE](LICENSE).
