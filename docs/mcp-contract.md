# MCP contract

- Contract version: 1.4
- Status: Local implementation; live deployment not verified
- Reviewed: 2026-09-07
- Owner: Agent
- Next review: 2026-09-14

## Implemented tools

| Tool | Required grant | Inputs / result |
| --- | --- | --- |
| `get_instance_info` | Any of `records.read`, `instance.read`, `records.write`, `records.delete` | No inputs. Application name, release version without build metadata, database schema version, contract version |
| `get_capabilities` | Any of `records.read`, `instance.read`, `records.write`, `records.delete` | No inputs. Granted scopes, implemented tool names and any-of scope requirements, allowed flags, Default domain ID, domain-selection support, write/file support and effective transport and record-write limits |
| `list_domains` | `records.read` | No inputs. Existing isolated domain catalog |
| `list_record_types` | `records.read` | Optional `domainId`. Up to 100 record types with their field definitions |
| `get_record_type` | `records.read` | Required `id`, optional `domainId`. One type or null |
| `search_records` | `records.read` | Optional `query`, `recordTypeId`, `page` (1), `pageSize` (25), `domainId`. Bounded result with total count |
| `get_record` | `records.read` | Required `id`, optional `domainId`. One record or null, including aliases, values, image metadata and up to 100 relationships |
| `get_record_relationships` | `records.read` | Required `id`, optional `domainId`. Up to 100 relationships |
| `query_domains` | `records.read` | `page` (1), `pageSize` (25). Complete domain catalog in bounded pages |
| `query_record_types` | `records.read` | `page`, `pageSize`, optional `domainId`. Type summaries with total count; use `get_record_type` for attached fields |
| `list_field_definitions` | `records.read` | `page`, `pageSize`, optional `domainId`. Reusable field configuration, lifecycle, choices and provenance |
| `list_relationship_types` | `records.read` | `page`, `pageSize`, optional `domainId`. Definitions with directional/inverse labels and lifecycle |
| `query_record_relationships` | `records.read` | Required `id`, `page`, `pageSize`, optional `domainId`. All links through SQL paging and snapshot-consistent total count |
| `get_field_type_schema` | `records.read` | Required `typeId`. JSON value schema, value property, recognized/custom flag and validation guidance |
| `list_field_types` | `records.read` | No inputs. Built-in types and value schemas, with custom scalar fallback support |
| `validate_record` | `records.write` | Required `domainId`, `recordTypeId`, `displayName`, `values`; optional `aliases`. Validates creation without saving; normalized name/aliases, field count and schema revision |
| `create_record` | `records.write` | Validation inputs plus required UUID `idempotencyKey`. Creates one record and returns a committed receipt |
| `patch_record` | `records.write` | Required `domainId`, `id`, `expectedRevision`, UUID `idempotencyKey`, `changes`. Explicit changes; returns a committed receipt |
| `preview_record_batch` | `records.write` | Required `domainId`, UUID `idempotencyKey`, `operations` (1-100 creates/patches). Validates all items and persists an expiring preview; no record changes |
| `get_record_batch_preview` | `records.write` | Required `domainId`, `previewId`. Ordered summaries, expiry and applied status for the owning credential |
| `apply_record_batch` | `records.write` | Required `domainId`, `previewId`, UUID `idempotencyKey`. Atomically commits the reviewed operations or rolls back all of them |
| `preview_record_deletion` | `records.delete` | Required `domainId`, `id`, `expectedRevision`, UUID `idempotencyKey`. Persists a deletion impact preview without deleting data |
| `get_record_deletion_preview` | `records.delete` | Required `domainId`, `previewId`. Counts, revisions, expiry and applied status for the owning credential |
| `delete_record` | `records.delete` | Required `domainId`, `id`, `expectedRevision`, UUID `idempotencyKey`; `previewId` required when dependent data exists. Committed receipt and current media-cleanup flag |
| `get_record_deletion_status` | `records.delete` | Required `domainId`, apply `idempotencyKey`. Original deletion receipt and current `mediaCleanupPending` status |

Remote deployment/activation and credential checks apply before invocation. Tool-level authorization is enforced even if discovery lists a denied tool. `instance.read` exposes no record names, domain catalog, credentials, endpoint paths or host paths. Discovery reports implemented functionality: `supportsWrites` is true and `supportsFileTransfer` is false. Per-tool `allowed` flags reflect the caller's grants; write support does not imply permission or complete instance-management coverage.

Omitted domain selectors retain the Default domain for backwards compatibility. Explicit malformed or unknown domain selectors fail closed. Domain listing is still under the single-administrator trust boundary. New paged tools accept page 1-10000 and page size 1-100, rejecting invalid values. They preserve deterministic ordering and report total count; empty pages do not lose that count. Legacy tools keep their response shapes and caps. Metadata catalogs currently use the shared application list services before paging; relationship rows are paged directly in SQLite.

Remote access settings select permissions for the next credential rotation. Existing grants remain selected by default, including unfamiliar existing grants that can be retained or removed. Unsupported new grants cannot be introduced. MCP offers `instance.read`, `records.read`, `records.write` and `records.delete`; HTTP API offers `records.read`. Selections take effect only on rotation, which replaces the one credential for that surface. `records.write` does not imply data reads; select `records.read` too when the client needs to discover schemas or retrieve records.

## Additive typed information in 1.1

Field definitions retain existing properties and add `configurationJson`, `lifecycle` and `choiceOptions`. Choice options come from the same Core configuration parser used by the application; other field types have an empty option list.

Record values retain the formatted `value`, `tags` and structured `location`. They additionally expose `ordinal`, `scalarValue` and `temporal`. Temporal values contain canonical stored `value`, lowercase precision, `isApproximate` and `approximationNote`. For example, a decade entered as `2010s` is returned as canonical `2010` with precision `decade`; clients must preserve both fields rather than interpreting it as an exact year. The legacy formatted value remains available for display.

Clients must ignore unknown additive response fields. Future incompatible request/result changes require a new tool or an explicitly versioned contract; existing read names remain stable. Version 1.2 adds the record-write tools below; file transfer remains planned.

Record/type reads include an opaque `revision` token. Record tokens cover content and attached validation schema changes; they are independent of media-only updates. The browser and MCP use record tokens to reject stale edits. Deletion uses a separate impact revision as described below; individual media tools remain planned.

## Record writes in 1.2

New writes require explicit domain selection. Each supplied field has `fieldDefinitionId` and exactly one matching `scalarValue`, `tags`, `temporal`, or `location` shape. Temporal input uses named precision strings; location numeric inputs are invariant strings, matching schema discovery. Unknown field types use scalar text. Nested unrecognized input properties are rejected.

Patch changes are `set_name` with `value`, `replace_aliases` with `values`, `set_field` with field ID and a matching value shape, or `clear_field` with field ID only. Omitted data survives. An empty alias list clears aliases; use `clear_field` to remove field data. Required fields cannot be cleared. Duplicate targets, empty `set_field` values and conflicting shapes fail without mutation. See the [patch example](mcp-write-contract.md#patch-shape).

Receipts contain `idempotencyKey`, `items` (ID, revision, created/updated outcome), `completedAtUtc`, and `retryUntilUtc`. Identical retries return the original receipt for 24 hours, even after later record edits/deletion. Changed content under the same key fails with `retry_conflict`. Retry identity includes credential generation, surface, domain and action. Rotation starts new ownership: retrying an old create with the replacement credential can create a second record. Resolve outstanding commands before rotating and never reuse keys beyond their retry window.

`recordWriteLimits` advertises at most 1,000 supplied fields, 1,000 patch changes, 10,000 retained commands per domain, 65,536 receipt bytes, a 24-hour retry window and seven additional days of expiry tombstones. Application and transport limits also apply. Expired entries are cleaned on subsequent writes; history quota exhaustion rejects new mutations while existing valid receipts remain replayable.

Application failures set MCP `isError` and return `structuredContent.error` with `code`, bounded `message`, and `correlationId`. Current codes are `permission_denied`, `not_found`, `validation_failed` (including command bounds), `stale_revision`, `retry_conflict`, `retry_expired`, and `temporarily_unavailable`. Malformed JSON or argument binding can fail at the protocol layer before this envelope. On an uncertain transport/storage outcome, retry with the same key and payload. Successful validation is advisory and reserves no revision or resources.

Committed audit entries share the mutation transaction. Semantic tool failures for a known domain are recorded separately using action/outcome/correlation metadata only; unknown domains and protocol-binding errors remain transport-level failures. Audit failure cannot replace an already established command error. Retention is bounded to 90 days/50,000 entries per domain, pruned on audit writes.

## Record batches in 1.3

Preview accepts 1-100 ordered `operations`. A `create` supplies `recordTypeId`, `displayName`, `values` and optional `aliases`; a `patch` supplies `id`, `expectedRevision` and `changes` using the single-record patch contract. Reject repeated target records, unsupported operations, and inputs belonging to another operation shape. Creates receive new IDs during preview. Inter-item references and deletion are not supported in this batch contract; they require separate workflows.

```json
{
  "domainId": "00000000-0000-7000-8000-000000000001",
  "idempotencyKey": "01900000-0000-7000-8000-000000000003",
  "operations": [
    {
      "operation": "create",
      "recordTypeId": "01900000-0000-7000-8000-000000000004",
      "displayName": "Fictional person",
      "values": []
    },
    {
      "operation": "patch",
      "id": "01900000-0000-7000-8000-000000000002",
      "expectedRevision": "opaque-revision-from-read",
      "changes": [{ "operation": "set_name", "value": "Updated fictional person" }]
    }
  ]
}
```

The preview returns `previewId`, `createdAtUtc`, `expiresAtUtc`, `applied` and ordered `items`. Each item identifies its index, operation, record ID and supplied field/patch counts; creates also show their normalized name and type ID. Patch summaries do not expose untouched record contents to a write-only credential. Review the submitted operations together with this summary. The server stores the validated normalized changes and source revisions, so apply accepts only the preview ID and a separate apply retry key, not replacement operations.

Previews last 15 minutes. Identical preview requests with the same key return the retained preview; changed input under that key fails. Inspection does not extend expiry. Apply checks every source revision inside the transaction, writes every record and the receipt/audit, and marks the preview consumed. Any stale item returns `stale_preview` with no committed items. Concurrent identical applies return the same receipt; another apply key against a consumed preview fails with `preview_consumed`. Normal 24-hour apply receipts remain replayable after the preview expires or is cleaned.

Preview ownership includes credential generation, surface and domain. Unknown/wrong-owner/wrong-domain IDs return `not_found`; retained expired previews return `preview_expired`. Once cleanup removes a preview it returns `not_found`. Resolve outstanding work before credential rotation. Preview keys must not be reused after the 15-minute window; apply keys follow the 24-hour command retry contract.

`recordWriteLimits` also reports `maximumBatchRecords` (100), `previewLifetimeMinutes` (15), `maximumRetainedPreviewsPerDomain` (100), and `maximumPreviewBytes` (1,048,576). The byte bound covers combined stored normalized operations and summary, which can exceed a small patch request when preserving existing record data. Split a batch if this limit is reached. At most 100 MiB of preview payload is retained per domain; SQLite file/WAL overhead is additional. Quota failures use `limit_exceeded` without record mutations. Consumed previews drop their prepared value payload immediately. Expired previews are removed on preview creation, at host startup, and every five minutes. Preview rows are in the domain database, included in its ordinary backup snapshot, and cleared by debug reset; restored expired rows are cleaned on startup.

## Record deletion in 1.4

Deletion requires the separate `records.delete` grant. Existing `records.write` credentials remain limited to creation/editing. Deletion-only credentials can discover capabilities and inspect their deletion previews/status, but do not gain record reads, creation or patching. Select `records.read` separately to retrieve record IDs and revisions through MCP.

Preview returns record ID, content revision, an opaque `impactRevision`, created/expiry times and counts of stored field values, aliases, images and their original bytes, relationships, reminders, graph selections/positions and affected graph views, import fingerprints and imported properties. It does not return names, field contents, filenames or linked-record contents. Previewing never deletes data. Its credential/domain ownership, 15-minute expiry, retry conflict detection and cleanup follow the batch-preview lifecycle. The 100-preview quota is shared by both families.

Review the counts, then call `delete_record` with the record/revision, preview ID and an apply retry key. The transaction verifies the impact revision as well as matching the reviewed record. Content, schema, media, relationship, reminder, graph-reference or import-provenance edits invalidate the preview; stale impact returns `stale_preview` without deletion. A record with no dependent rows can be deleted without a preview; the absence of dependencies and expected revision are checked inside the deletion transaction. If dependencies exist, omission of `previewId` returns `validation_failed` and requires review.

Deletion cascades through dependent database rows, including structured field children and spatial-index entries. It removes the record's graph selections and saved coordinates, preserving other records and view definitions. The deleted record no longer appears in queried grids, calendar, graph or map results. Receipt, committed audit, preview consumption and a media-cleanup queue entry share this transaction. The receipt item has outcome `deleted`; its revision identifies the deleted content version, not a live record.

The response is `{ "receipt": { ... }, "mediaCleanupPending": false }`. Filesystem removal follows commit and can remain pending because of open files or concurrent media work. Media writes and cleanup coordinate per domain/record; cleanup waits at most one second for that coordination before remaining queued. Retrying the command preserves the receipt and refreshes the cleanup flag; status can also be queried with the apply key. A retry under a different key cannot reuse a consumed preview. Status/receipts are retained for 24 hours; background cleanup continues beyond that window.

The durable queue is capped at 1,000 records per domain, advertised as `maximumPendingMediaCleanupPerDomain`. A full queue rejects further deletions before mutation. Cleanup retries at startup and every five minutes, processing at most 100 queued records per domain per sweep and rotating attempted entries so a blocked item does not permanently exclude later entries. It only removes the opaque record directory within that domain's media root and refuses cleanup if a live record with the same ID exists. File removal is not part of the SQLite transaction: a successful database deletion is never represented as rolled back because files remain locked. Browser record deletion uses the same queue.

Deletion previews and cleanup rows live in domain databases, participate in normal snapshots, and are cleared by debug reset. Backups include only database-referenced originals, so unreferenced pending-deletion files are not copied into new packages. Existing backups remain independent copies; deleting a live record does not erase those archives or historical command receipts.

## Discovery result example

Illustrative subset of `get_capabilities` output for an `instance.read` credential:

```json
{
  "contractVersion": "1.4",
  "grantedScopes": ["instance.read"],
  "tools": [
    { "name": "get_capabilities", "anyOfScopes": ["records.read", "instance.read", "records.write", "records.delete"], "allowed": true },
    { "name": "get_record", "anyOfScopes": ["records.read"], "allowed": false }
  ],
  "supportsDomainSelection": true,
  "supportsWrites": true,
  "supportsFileTransfer": false
}
```

The real response includes all registered tools, Default domain ID, configured request limits and record-write limits. Transport limits do not guarantee that every application query accepts the maximum body size. Existing application limits continue to apply independently.

## Verification and remaining work

`RemoteDiscoveryTests` exercises real MCP tool discovery and invocation, compares the capability catalog with registered tools, checks legacy and narrow scopes, checks unrelated-scope denial and revocation, and verifies choice/temporal serialization from persisted data. It also covers 105 record types, 505 relationships, directional labels, domain isolation, invalid selectors/page limits, field input schemas, permission-preserving rotation and the test-only 256 KiB transfer probe. Existing remote-surface tests cover isolation and disabled defaults. The deployed MCPHub catalog has not been refreshed or verified.

`RemoteRecordWriteTests.cs` exercises real validation/create/patch calls, narrow write grants, rotation and revocation, stale edits, atomic validation failure, retry after deletion, field preservation, wrong-domain rejection and failure-audit redaction. Store tests separately cover restart replay, expiry and transactional concurrency.

`RemoteRecordBatchTests` covers preview/apply through MCP, duplicate concurrent apply, stale whole-batch rollback after a browser-service edit, scope/domain/owner rejection, bounds, summary redaction and cleanup across domains. `RecordBatchWorkflowTests` covers restart, receipt replay after preview cleanup, expiry, schema changes, duplicate targets, payload quota and retention quota.

Deletion tests verify actual MCP permissions, direct empty-record deletion, review requirements, stale dependencies, concurrent replay, expiry, credential rotation, wrong-domain rejection and committed audit. Data tests verify every dependent family, preservation of related records/views, restart cleanup, locked-file retry on Windows, queue quota and an upload already in progress during deletion.

The [write and transfer design](mcp-write-contract.md) selects credential fingerprints, transactional retry/revision behavior and bounded chunks. Remaining work includes relationship/setup/structure management, other workflow previews, transfers and the deployed client path. Owner: Agent; review: 2026-09-14. The full remaining delivery scope stays in the [implementation plan](mcp-management-plan.md).
