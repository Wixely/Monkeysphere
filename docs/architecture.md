# Architecture

## Projects

- `Monkeysphere.Core` owns domain types, validation, application commands/queries, and storage ports.
- `Monkeysphere.Data` owns SQLite/Dapper repositories and the application-owned DnaX manifest.
- `Monkeysphere.Web` owns the Blazor host, administrator authentication, UI, HTTP API, MCP tools, and deployment integration.

Core does not depend on Blazor, SQLite, Dapper, or DnaX. Data implements Core ports. Web composes both and is the only network host.

## Data boundaries

The application database and DnaX remote-access database are separate files under one configurable data root. Each owns an independent DnaX manifest and ledger. Remote API and MCP operations call Core query services; they do not receive direct database or filesystem access.

## Identifiers and values

Domain entities use UUIDv7 identifiers. Reusable field definitions carry an open string type identifier. Recognized scalar values use typed relational columns; tags use ordered child rows; unknown types use lossless text fallback.
