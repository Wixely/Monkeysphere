# Security and privacy boundary

Monkeysphere stores sensitive personal information. The single administrator, host administrator, volume administrator, backup operator, and trusted reverse-proxy administrator are inside the deployment trust boundary.

## Initial controls

- No public registration, tenant system, roles, or anonymous record access.
- Administrator password supplied at deployment, rejected when missing/default/invalid, hashed in memory with the ASP.NET Core password hasher, and never persisted or logged in plaintext.
- Cookie authentication, explicit sign-out, CSRF validation, bounded session lifetime, and login rate limiting.
- Authentication before record, field, search, remote administration, API, MCP, or detailed diagnostic operations.
- DnaX remote access unavailable by default, with separate credentials, randomized routes, bounded requests, and redacted audit metadata when deliberately enabled.
- Minimal unauthenticated liveness response.

## Transport

The application serves HTTP. External confidentiality depends on an operator-managed HTTPS reverse proxy. Forwarded headers are trusted only from explicitly configured proxies or networks; remote access must not be enabled externally until this boundary is configured and tested.

## Known initial non-goals

There is no application-level encryption of SQLite files, media, exports, or backups. Host and storage administrators can read them. Mutating remote operations, arbitrary SQL, shell access, and filesystem browsing are not provided.
