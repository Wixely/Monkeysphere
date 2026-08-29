# Security and privacy boundary

Monkeysphere stores sensitive personal information. The single administrator, host administrator, volume administrator, backup operator, and trusted reverse-proxy administrator are inside the deployment trust boundary.

## Initial controls

- No public registration, tenant system, roles, or anonymous record access.
- The unconfigured login is deliberately `admin` / `admin`. Deployment configuration can override the username and can override the password directly or through a password file; Docker Compose exposes interpolated credential settings for deployment overrides. The effective password is hashed in memory with the ASP.NET Core password hasher and is never persisted or logged in plaintext.
- Cookie authentication, explicit sign-out, CSRF validation, bounded session lifetime, and login rate limiting.
- Authentication before record, field, search, remote administration, API, MCP, or detailed diagnostic operations.
- Authentication before location queries or retrieval. Coordinates, accuracy, and approximation radii are treated as sensitive record data and are returned only through the existing authenticated application and scoped remote-record boundaries.
- Authentication before image delivery. Uploaded images are byte-, dimension-, pixel-, and format-bounded, decoded before acceptance, stored under opaque paths, and rendered through regenerated WebP derivatives with source metadata removed.
- DnaX remote access unavailable by default, with separate credentials, randomized routes, bounded requests, and redacted audit metadata when deliberately enabled.
- Minimal unauthenticated liveness response.

## Transport

The application serves HTTP. External confidentiality depends on an operator-managed HTTPS reverse proxy. Forwarded headers are trusted only from explicitly configured proxies or networks; remote access must not be enabled externally until this boundary is configured and tested.

The built-in credential is public and must be overridden before exposing a deployment outside a trusted local environment.

## Known initial non-goals

There is no application-level encryption of SQLite files, original media, exports, or backups. Host and storage administrators can read them. Original media is retained for future export but is not served by the browser application. Mutating remote operations, arbitrary SQL, shell access, and filesystem browsing are not provided.
