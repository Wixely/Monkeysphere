# Monkeysphere

Monkeysphere is a private, self-hosted relationship-memory application for records about people, pets, and administrator-defined entity types. It is an unpublished work in progress.

## Current vertical slice

The current slice provides one administrator account, configurable record types and reusable typed fields, record editing, searchable alternate names, directional and symmetric relationships, reusable saved grid views, bounded search/filtering, SQLite/Dapper persistence, checksummed DnaX migrations, and disabled-by-default read-only HTTP API and MCP surfaces. Every record has one primary display name and can have multiple aliases, nicknames, short names, or former names. New datasets open a four-level setup wizard with concrete examples, customizable preset selection, and a persistent blank-slate option. The catalogue currently ships 15 editable, versioned record-type presets with canonical field keys and applicable packaged relationships. Saved views package one record type, selected columns, up to ten filters, optional grouping, and display-name or field sorting. Recognized fields include text, multiline text, number, exact date, choice, tags, precision-aware temporal values, phone numbers, and web links; unknown type identifiers retain a lossless text fallback.

## Requirements

- .NET SDK 10.0.300 or a compatible later 10.0 feature band.
- Windows PowerShell 5.1 for repository scripts.
- No credential setup is required for local testing. Configure a non-default administrator password before exposing a deployment outside a trusted local environment.

No Node.js or Python toolchain is used.

## Build and test

```powershell
.\eng\Build.ps1
```

### Test in VS Code

Open the repository root in VS Code, install the recommended C# Dev Kit extension if prompted, and press `F5`. Select **Monkeysphere Web** if VS Code asks for a configuration. The browser opens at `http://localhost:5080`; sign in with username `admin` and password `admin`.

VS Code stores this test deployment beneath the ignored `.local/vscode-data` directory so records survive debugging restarts without entering source control. Use **Terminal → Run Task → verify** to run the locked restore, Release build, and complete test suite.

Without credential configuration, Monkeysphere uses `admin` / `admin`. Override the username with `MONKEYSPHERE_ADMIN_USERNAME` and the password with either `MONKEYSPHERE_ADMIN_PASSWORD` or `MONKEYSPHERE_ADMIN_PASSWORD_FILE`. Docker Compose exposes the username and password as interpolated configuration values with the same defaults, so shell variables, a Compose `.env` file, or an override file can replace them.

Then run:

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

Aliases are ordered, case-insensitively unique within a record, and cannot duplicate its primary display name. Record search matches aliases as well as primary names and field values.

Preset-derived record types and fields retain their preset key and version while remaining locally editable. The current place presets use descriptive location text plus an optional approximation radius in kilometres; structured coordinates and map-pin editing remain a later map slice.

## Deployment

- Interactive Windows: use `eng/Run.ps1` or `dotnet run`.
- Windows Service: the host detects service execution; installation guidance will be verified before support is claimed.
- Linux/systemd: a unit template is under `deploy/systemd`; verification remains required.
- Docker: use `docker compose up --build`. It defaults to `admin` / `admin`; set `MONKEYSPHERE_ADMIN_USERNAME` and `MONKEYSPHERE_ADMIN_PASSWORD` in the Docker deployment environment to override them.

See [architecture](docs/architecture.md), [security](docs/security.md), [dependencies](docs/dependencies.md), [verification status](docs/verification.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## License

MIT. See [LICENSE](LICENSE).
