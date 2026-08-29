# Monkeysphere

Monkeysphere is a private, self-hosted relationship-memory application for records about people, pets, and administrator-defined entity types. It is an unpublished work in progress.

## Current vertical slice

The current slice provides one administrator account, configurable record types and reusable typed fields, record editing, directional and symmetric relationships, bounded search/filtering, SQLite/Dapper persistence, checksummed DnaX migrations, and disabled-by-default read-only HTTP API and MCP surfaces. Recognized fields include text, multiline text, number, exact date, choice, tags, precision-aware temporal values, phone numbers, and web links; unknown type identifiers retain a lossless text fallback.

## Requirements

- .NET SDK 10.0.300 or a compatible later 10.0 feature band.
- Windows PowerShell 5.1 for repository scripts.
- An administrator password supplied through configuration or a secret file. Never commit it.

No Node.js or Python toolchain is used.

## Build and test

```powershell
.\eng\Build.ps1
```

### Test in VS Code

Open the repository root in VS Code, install the recommended C# Dev Kit extension if prompted, and press `F5`. Select **Monkeysphere Web** if VS Code asks for a configuration. Enter a temporary administrator password of at least 14 characters when prompted; the browser opens at `http://localhost:5080`, and the username is `admin`.

VS Code stores this test deployment beneath the ignored `.local/vscode-data` directory so records survive debugging restarts without entering source control. Use **Terminal → Run Task → verify** to run the locked restore, Release build, and complete test suite.

For local development, store the password outside the repository and point `MONKEYSPHERE_ADMIN_PASSWORD_FILE` at that file, or set `MONKEYSPHERE_ADMIN_PASSWORD` only in the process environment. Then run:

```powershell
.\eng\Run.ps1
```

The default development address is `http://localhost:5080`. Production deployments serve HTTP and must use a correctly configured trusted HTTPS reverse proxy when transport confidentiality is required.

## Optional remote access

HTTP API and MCP support are compiled in but unavailable by default. A deployment must explicitly enable the DnaX master gate and the desired surface, provide a stable deployment identifier, configure the trusted HTTPS/proxy boundary, and restart. The administrator can then create a one-time credential, rotate the randomized endpoint, and activate that surface from the Remote access page. API and MCP use separate credentials; anonymous MCP is not supported.

The first remote scope is `records.read`. It permits bounded record-type listing, record search, and individual record retrieval only. See [security](docs/security.md) for excluded operations and the trust boundary.

## Data

`MONKEYSPHERE_DATA_ROOT` selects the mutable data root and defaults to `data` beneath the content root. Application data and DnaX remote-access state use separate SQLite files and separate migration ledgers.

Temporal values preserve century, decade, year, month, day, minute, or second precision plus optional approximation metadata. Before/after filters accept `19c`, `1980s`, `YYYY`, `YYYY-MM`, `YYYY-MM-DD`, `YYYY-MM-DDTHH:mm`, or `YYYY-MM-DDTHH:mm:ss`.

## Deployment

- Interactive Windows: use `eng/Run.ps1` or `dotnet run`.
- Windows Service: the host detects service execution; installation guidance will be verified before support is claimed.
- Linux/systemd: a unit template is under `deploy/systemd`; verification remains required.
- Docker: use `docker compose up --build` after creating the administrator password file referenced by `compose.yaml`.

See [architecture](docs/architecture.md), [security](docs/security.md), [dependencies](docs/dependencies.md), [verification status](docs/verification.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## License

MIT. See [LICENSE](LICENSE).
