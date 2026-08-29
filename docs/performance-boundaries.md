# Performance and load boundaries

- Status: Initial local-release envelope
- Verified: 2026-08-29 on Windows
- Review triggers: material schema/query changes, a supported multi-user mode, remote writes, larger accepted datasets, new media formats, or release hardware benchmarks

Monkeysphere is designed for one administrator and a personal dataset. Its safeguards are response and input bounds, not a throughput service-level agreement. The limits below prevent one browser response or import from growing with the entire dataset while retaining explicit pagination or truncation signals.

| Surface | Enforced boundary | Behaviour above the boundary |
| --- | --- | --- |
| Record and remote search | 100 records per page | Validation or clamping; deterministic pagination reports total count. |
| Saved grid view | 25 columns, 10 filters, 100 rows per page in the UI | Definition validation rejects excess fields/filters; results remain paged. |
| Record relationships | 500 relationships per query | Validation rejects a larger request. |
| Relationship graph | 500 nodes, 2,000 edges, 3 neighbour hops, 200 search characters | The store reads one extra row and reports node/edge truncation; the UI asks for narrower filters or focused expansion. |
| Spatial map | 500 locations per page, 20 selected location fields, page 1–10,000 | Validation rejects larger pages/layer selections; the UI uses 100-row pages and reports the total. |
| Calendar / iCalendar | 367-day range and 1,000 entries | Validation rejects wider ranges or larger result limits. |
| vCard import | 5 MiB, 1,000 cards, 2,000 properties per card | Parsing stops with a validation error before apply. |
| vCard export | 100 explicitly selected records | The export workflow rejects a larger selection. |
| Images | 10 MiB, 24 megapixels, 12,000 pixels per dimension, 50 images per record | Decode/validation fails before persistence. |
| Backup/restore | 100,005 archive entries; retention 1–1,000 packages | Validation rejects excess or unmanifested entries; scheduler rejects invalid retention. Package byte size is intentionally governed by available operator storage rather than an arbitrary application cap. |
| Login | Five attempts per remote address per minute, no queue | Excess attempts receive HTTP 429. |

## Graph scale evidence

The accepted graph storage target is 10,000 records and 50,000 relationships while rendering only the bounded subgraph. `RelationshipGraphEnforcesRenderingBoundsAtAcceptedStorageScale` creates that exact SQLite dataset, gives one focus record more than 500 neighbours, places more than 2,000 relationships among the selected nodes, and queries it through the production Core/Data services. The query must complete within a 10-second cancellation deadline and return exactly 500 nodes and 2,000 edges with both truncation flags set.

This is a regression/load-boundary test, not a benchmark or latency promise. The current schema has covering indexes for both relationship endpoints and uses an approximation-aware R-tree for mapped locations. Release-specific latency, concurrency, sustained backup throughput, and memory measurements remain environment-dependent and must be characterized on intended deployment hardware if those become release criteria.

## Operational guidance

- Keep SQLite, media, temporary work, and backups on storage with sufficient free space and normal host monitoring.
- Narrow graph searches before increasing depth; do not increase browser caps to compensate for an overly broad query.
- Put an upstream request/body limit and connection limit at any external reverse proxy, matching or tightening the application limits.
- Schedule backups outside the busiest interactive period for large media collections.
- Treat the single-process data-root lock and single-administrator model as intentional; this release does not claim multi-instance or high-concurrency operation.
