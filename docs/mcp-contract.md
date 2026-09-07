# MCP contract

- Contract version: 1.15
- Status: Local implementation; live deployment not verified
- Reviewed: 2026-09-07
- Owner: Agent
- Next review: 2026-09-14

## Implemented tools

| Tool | Required grant | Inputs / result |
| --- | --- | --- |
| `get_instance_info` | Any of `records.read`, `instance.read`, `records.write`, `records.delete`, `relationships.write`, `structure.write`, `domains.manage`, `contacts.import`, `contacts.export` | No inputs. Application name, release version without build metadata, database schema version, contract version |
| `get_capabilities` | Any of `records.read`, `instance.read`, `records.write`, `records.delete`, `relationships.write`, `structure.write`, `domains.manage`, `contacts.import`, `contacts.export` | No inputs. Granted scopes, implemented tool names and any-of scope requirements, allowed flags, Default domain ID, domain-selection support, write/file support and effective transport and record-write limits |
| `create_domain` | `domains.manage` | Required new non-Default `domainId` UUID, `name`, UUID `idempotencyKey`. Creates an isolated blank domain using a durable reservation |
| `rename_domain` | `domains.manage` | Required `domainId`, `name`, `expectedRevision`, UUID `idempotencyKey`. Revision-checked rename with durable receipt |
| `list_domains` | `records.read` | No inputs. Existing isolated domain catalog |
| `list_record_types` | `records.read` | Optional `domainId`. Up to 100 record types with their field definitions |
| `get_record_type` | `records.read` | Required `id`, optional `domainId`. One type or null |
| `begin_upload` | `contacts.import` | Required `domainId`, `purpose` (`contact_import`), `byteLength`, `sha256`, `contentType`, UUID `idempotencyKey`. Bounded owned staging session |
| `write_upload_chunk` | `contacts.import` | Required `domainId`, `uploadId`, sequential `offset`, `contentBase64`, chunk `sha256`. Atomic bytes/offset with safe retry |
| `get_upload_status` | `contacts.import` | Required `domainId`, `uploadId`. State, accepted bytes, expiry and current chunk limit |
| `complete_upload` | `contacts.import` | Required `domainId`, `uploadId`. Integrity and UTF-8 vCard validation; returns upload status, contact count and `contentValidated` |
| `cancel_upload` | `contacts.import` | Required `domainId`, `uploadId`. Removes staged bytes and retains bounded terminal metadata |
| `preview_contact_import` | `contacts.import` | Required `domainId`, `uploadId`, UUID `idempotencyKey`. Durable reviewed preview header and advisory `isCurrent`; no record mutations |
| `get_contact_import_preview` | `contacts.import` | Required `domainId`, `previewId`; `page` (1), `pageSize` (25). Bounded contact summaries and current-revision status |
| `read_contact_import_evidence` | `contacts.import` | Required `domainId`, `previewId`, zero-based `contactIndex`; byte `offset` (0), `count` (16384). Complete evidence through bounded Base64 byte ranges |
| `apply_contact_import` | `contacts.import` | Required `domainId`, `previewId`, `expectedRevision`, UUID `idempotencyKey`, and one explicit selection per contact. Atomic import and durable receipt |
| `get_contact_import_result` | `contacts.import` | Required `domainId`, import `idempotencyKey`; `page` (1), `pageSize` (100). Durable receipt and paged per-contact outcomes |
| `export_contacts` | `contacts.export` | Required `domainId` and 1-100 distinct `recordIds` on the Person preset; byte `offset` (0), `count` (16384). One regenerated vCard 4.0 document read through bounded Base64 byte ranges with a whole-document digest |
| `query_records` | `records.read` | Optional `query`, `recordTypeId`, up to 10 `filters`, `sort`, `page`, `pageSize`, `domainId`. Structured filtering and sorting with strict bounds and snapshot-consistent totals |
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
| `create_relationship_type` | `structure.write` | Required `domainId`, `name`, `directionality`, UUID `idempotencyKey`; optional `inverseName`. Creates a definition and returns its ID/revision receipt |
| `create_relationship` | `relationships.write` | Required `domainId`, `typeId`, `sourceRecordId`, `targetRecordId`, `expectedTypeRevision`, `expectedSourceRevision`, `expectedTargetRevision`, UUID `idempotencyKey`; optional `note`. Creates a link and returns its ID/revision receipt |
| `delete_relationship` | `relationships.write` | Required `domainId`, link `id`, `expectedRevision`, UUID `idempotencyKey`. Deletes only the link and returns a receipt |
| `create_record_type` | `structure.write` | Required `domainId`, `name`, UUID `idempotencyKey`; optional `symbol`. Creates a custom type and returns its ID/revision receipt |
| `create_and_attach_field` | `structure.write` | Required `domainId`, `recordTypeId`, `expectedRevision`, `name`, `typeId`, UUID `idempotencyKey`; optional `isRequired` (false), `choiceOptions`. Creates and appends a field; receipt contains the created field followed by the updated type |
| `attach_field` | `structure.write` | Required `domainId`, `recordTypeId`, `fieldDefinitionId`, `expectedRevision`, `expectedFieldRevision`, UUID `idempotencyKey`; optional `isRequired` (false). Reuses an active field; receipt contains the updated type |
| `get_setup_state` | `records.read` | Optional `domainId`. Effective setup status, domain-state revision, catalog revision and installed preset count; no persistence side effects |
| `list_installed_presets` | `records.read` | Optional `domainId`, `page`, `pageSize`. Local type IDs, preset keys/versions, names and lifecycle |
| `list_presets` | `records.read` | `page`, `pageSize`. Packaged record-type definitions with fields, versions and examples; result contains catalog revision and page |
| `list_starter_packs` | `records.read` | `page`, `pageSize`. Onboarding levels and selectable keys, including the blank option; result contains catalog revision and page |
| `list_relationship_presets` | `records.read` | `page`, `pageSize`. Packaged relationship definitions and prerequisite selection rules; result contains catalog revision and page |
| `install_preset` | `structure.write` | Required `domainId`, `presetKey`, `expectedRevision`, `expectedCatalogRevision`, UUID `idempotencyKey`. Atomic single-preset installation with receipt |
| `complete_setup` | `structure.write` | Required `domainId`, `starterPackKey`, `selectedPresetKeys`, `expectedRevision`, `expectedCatalogRevision`, UUID `idempotencyKey`; `acknowledgeBlank` defaults false. Atomic selected installation and setup completion |

Remote deployment/activation and credential checks apply before invocation. Tool-level authorization is enforced even if discovery lists a denied tool. `instance.read` exposes no record names, domain catalog, credentials, endpoint paths or host paths. Discovery reports implemented functionality: `supportsWrites` is true and `supportsFileTransfer` is true for contact upload, preview, and import; contact export and image transfer remain planned. Per-tool `allowed` flags reflect the caller's grants; write support does not imply permission or complete instance-management coverage.

Both list_domains and query_domains include an opaque domain `revision`. Domain registry migration 2 persists these tokens and changes them on renaming, including a rename back to an earlier name. Browser rename submits its captured revision; the registry update checks it transactionally and publishes the matching cached catalog only after commit. Contract 1.9 adds registry-owned rename receipts and the separate `domains.manage` grant. Contract 1.10 adds recoverable creation.

Omitted domain selectors retain the Default domain for backwards compatibility. Explicit malformed or unknown domain selectors fail closed. Domain listing is still under the single-administrator trust boundary. New paged tools accept page 1-10000 and page size 1-100, rejecting invalid values. They preserve deterministic ordering and report total count; empty pages do not lose that count. Legacy tools keep their response shapes and caps. Metadata catalogs currently use the shared application list services before paging; relationship rows are paged directly in SQLite.

Remote access settings select permissions for the next credential rotation. Existing grants remain selected by default, including unfamiliar existing grants that can be retained or removed. Unsupported new grants cannot be introduced. MCP offers `instance.read`, `records.read`, `records.write`, `records.delete`, `relationships.write`, `structure.write`, `domains.manage` and `contacts.import`; HTTP API offers `records.read`. Selections take effect only on rotation, which replaces the one credential for that surface. Write grants do not imply data reads; select `records.read` too when the client needs to discover schemas or retrieve records. Both new write grants permit instance/capability discovery; they do not permit unrelated record mutations.

## Additive typed information in 1.1

Field definitions retain existing properties and add `configurationJson`, `lifecycle` and `choiceOptions`. Choice options come from the same Core configuration parser used by the application; other field types have an empty option list.

Record values retain the formatted `value`, `tags` and structured `location`. They additionally expose `ordinal`, `scalarValue` and `temporal`. Temporal values contain canonical stored `value`, lowercase precision, `isApproximate` and `approximationNote`. For example, a decade entered as `2010s` is returned as canonical `2010` with precision `decade`; clients must preserve both fields rather than interpreting it as an exact year. The legacy formatted value remains available for display.

Clients must ignore unknown additive response fields. Future incompatible request/result changes require a new tool or an explicitly versioned contract; existing read names remain stable. Version 1.2 adds the record-write tools below; contact upload is available in 1.12; other file workflows remain planned.

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
  "contractVersion": "1.15",
  "grantedScopes": ["instance.read"],
  "tools": [
    { "name": "get_capabilities", "anyOfScopes": ["records.read", "instance.read", "records.write", "records.delete"], "allowed": true },
    { "name": "get_record", "anyOfScopes": ["records.read"], "allowed": false }
  ],
  "supportsDomainSelection": true,
  "supportsWrites": true,
  "supportsFileTransfer": true
}
```

The real response includes all registered tools, Default domain ID, configured request limits and record-write limits. Transport limits do not guarantee that every application query accepts the maximum body size. Existing application limits continue to apply independently.

## Verification and remaining work

`RemoteDiscoveryTests` exercises real MCP tool discovery and invocation, compares the capability catalog with registered tools, checks legacy and narrow scopes, checks unrelated-scope denial and revocation, and verifies choice/temporal serialization from persisted data. It also covers 105 record types, 505 relationships, directional labels, domain isolation, invalid selectors/page limits, field input schemas, permission-preserving rotation and the test-only 256 KiB transfer probe. Existing remote-surface tests cover isolation and disabled defaults. The deployed MCPHub catalog has not been refreshed or verified.

`RemoteRecordWriteTests.cs` exercises real validation/create/patch calls, narrow write grants, rotation and revocation, stale edits, atomic validation failure, retry after deletion, field preservation, wrong-domain rejection and failure-audit redaction. Store tests separately cover restart replay, expiry and transactional concurrency.

`RemoteRecordBatchTests` covers preview/apply through MCP, duplicate concurrent apply, stale whole-batch rollback after a browser-service edit, scope/domain/owner rejection, bounds, summary redaction and cleanup across domains. `RecordBatchWorkflowTests` covers restart, receipt replay after preview cleanup, expiry, schema changes, duplicate targets, payload quota and retention quota.

Deletion tests verify actual MCP permissions, direct empty-record deletion, review requirements, stale dependencies, concurrent replay, expiry, credential rotation, wrong-domain rejection and committed audit. Data tests verify every dependent family, preservation of related records/views, restart cleanup, locked-file retry on Windows, queue quota and an upload already in progress during deletion.

The [write and transfer design](mcp-write-contract.md) selects credential fingerprints, transactional retry/revision behavior and bounded chunks. Remaining work includes relationship/setup/structure management, other workflow previews, transfers and the deployed client path. Owner: Agent; review: 2026-09-14. The full remaining delivery scope stays in the [implementation plan](mcp-management-plan.md).

## Contact export in 1.15

Contract 1.15 has 52 tools and adds `export_contacts` under a new `contacts.export` grant. Export sends contact data out of the deployment, so it is deliberately separate from `contacts.import`: an importing credential cannot export, an exporting credential cannot import, and neither implies `records.read`. Existing credentials never gain the new grant automatically; an administrator selects it when rotating a credential.

The tool takes an explicit `domainId` and 1-100 distinct `recordIds`, reusing the same bounded Core export service as the authenticated browser download. Every selected ID must resolve to an existing record on that domain's Person preset; one unknown, duplicate, foreign-domain or non-Person ID fails the whole request with `validation_failed` rather than silently exporting a smaller set. There is no search, filter or export-everything form: the caller must already know which records it wants, so a narrow export grant cannot be used to enumerate the domain.

The result is one vCard 4.0 document read through bounded ranges. Offset is zero-based, `count` is 1-16384, and `nextOffset` chains ranges until null. Decode each `contentBase64` chunk, concatenate the bytes in offset order, then decode UTF-8; individual ranges can split Unicode sequences, so do not decode them independently as text. As with `read_contact_import_evidence`, an offset equal to `totalBytes` returns empty content and a null `nextOffset`, and a greater offset fails.

No export state is staged or stored. Each call regenerates the document from current records, which keeps the transient-storage quotas, expiry sweeps and restore invalidation of the upload path out of a read-only operation. `contentDigest` is the uppercase SHA-256 hexadecimal digest of the complete document and is returned with every range. A client must check that one export's ranges all report the same digest: a change means the underlying records were edited between ranges, and the read must restart at offset 0. The `recordIds` order is part of the document, so requesting the same records in a different order is a different export with a different digest.

Export preserves the same semantics as the browser download, including opaque source properties and Apple labels retained at import. `get_capabilities` advertises `contactExportLimits` with the contact, chunk and response bounds. Each call records bounded `contacts.export` audit metadata with no record names, IDs or document bytes. Reads are audited but not rate-limited beyond the shared transport limits.

## Relationship revisions in 1.5

Relationship-type discovery and all relationship reads now include an opaque `revision`. A type revision changes with its labels, directionality, lifecycle or preset provenance. Link revisions change with the link contents or its type definition; the token is identical from either endpoint. Tokens persist across restart and are independent of timestamp resolution. Clients must treat them as opaque.

Shared browser writes supply these tokens for type rename/retirement, link creation (the selected type revision), and removal (the link revision). Revision checks occur in the SQL mutation or creation transaction. Version 1.5 added read metadata; version 1.6 adds the commands below.

## Relationship writes in 1.6

The three relationship commands share the record-command receipt format and storage. Receipt item IDs refer to the relationship type or link, according to the invoked tool. The returned revision is the committed revision; deletion returns the link's final revision before removal. Identical retries replay the original receipt for 24 hours, even after the entity changes or disappears. Changed payloads return `retry_conflict`; expired results return `retry_expired`. Credential rotation changes ownership. The shared 10,000-command/domain capacity, 64 KiB receipt limit and seven-day post-replay tombstone retention apply to these commands too.

Type creation reuses Core label normalization: labels are trimmed, nonempty and at most 200 characters. `directionality` accepts the named values `directional` and `symmetric` (case-insensitive). Directional types require an inverse label; symmetric types discard it, matching the browser. Duplicate labels fail validation.

Link creation requires two distinct records and the current revisions of both records and the active relationship type. Read record tokens with `get_record` and type tokens with `list_relationship_types`. All references resolve inside the explicit domain. Revision checks, symmetric endpoint ordering, insertion, receipt and audit commit in one transaction. An optional note is trimmed and limited to 2,000 characters; omitted, null or whitespace-only notes become null. Duplicate links fail validation, including reversed symmetric endpoints. A retry with the original key does not create another link.

Deletion requires the link revision returned by relationship reads and preserves both endpoint records. A type or note change invalidates an earlier link revision. Missing references return `not_found`, stale tokens return `stale_revision`, invalid/duplicate inputs return `validation_failed`, and insufficient grants return `permission_denied`. Database, I/O and cancellation failures use the shared `temporarily_unavailable` result: retry the identical command with its original key to resolve a possible completed commit. No contact contents or note text is stored in application audit rows or receipts.

Relationship-type rename/retirement adapters remain in M4; link note/endpoint editing has no shared application command yet. Independent `relationships.read` and `structure.read` grants remain proposed: existing reads continue to require `records.read`.

## Schema creation in 1.7

All three schema commands require explicit domain selection and structure.write. They use the same credential-bound receipts, limits, 24-hour replay and redacted audit transaction as record/relationship commands. A repeated successful field creation replays its original field and type IDs/revisions even after the type changes. New requests with an old revision fail rather than attaching against a changed schema.

Record type and field names are trimmed, nonempty and limited to 200 characters. A record-type symbol is optional; whitespace clears it, and nonempty symbols allow at most four text elements, 32 UTF-16 code units and no control characters. Field type identifiers use Core normalization (lowercase; start with a letter; letters, digits, dot, underscore and hyphen only; at most 100 characters). Unknown type identifiers retain the existing scalar-text fallback. Choice fields require 1-1000 trimmed, nonempty options of at most 200 characters, distinct without regard to case. Choice options are ignored for other field types, matching the browser; there is no arbitrary configuration JSON input.

Read the type revision with get_record_type. Reusable and attached field reads now also return an opaque revision, persisted by migration 26 and changed by field metadata, configuration, lifecycle or provenance updates. Attaching an existing field checks both revisions in the mutation transaction. Duplicate attachments and retired entities fail; fields append at the next sort position.

Required attachment fails if any existing record lacks a stored value for that field. This check also applies to browser field creation and attachment, and failure leaves no orphan definition or partial association. Add optional fields to populated types; a new required field can be added to an empty type. Browser handlers submit their displayed revisions, so concurrent MCP schema changes invalidate stale browser selections. Field renaming, retirement, merges and conversion remain M4 work; these commands do not expose those operations or change requiredness on an existing attachment.

## Onboarding and presets in 1.8

The packaged catalogs are deployment-wide and use the same 1-100 page-size and 1-10000 page bounds as other discovery. Every catalog page includes the same opaque catalog revision, covering preset definitions, fields, relationship rules and starter packs. Installed preset discovery is domain-specific and includes retired or customized types with their recorded versions; it does not promise that local contents still match a packaged definition or offer preset upgrades.

get_setup_state performs one read transaction and never persists the implicit completion used by the browser's legacy setup getter. Existing record types imply completed onboarding with starterPackKey `existing`; completedAtUtc is null when no completion event was stored. The domain-bound revision covers persisted setup state, record-type revisions/provenance and relationship-type revisions. It changes when relevant domain structures change, including browser edits or persisted implicit completion. The snapshot reads structure metadata, not record contents.

New installation commands require both the domain revision from get_setup_state and the catalog revision from discovery. Invalid selections and name/identity collisions fail without partial installation. complete_setup accepts a distinct subset of the chosen pack (at most the number of packaged presets). Any empty selection requires acknowledgeBlank=true, acknowledging that preset-dependent features may need presets installed later. It rejects already completed setup, including domains that already contain custom record types. install_preset can add a missing preset after setup; it does not replace local structures or upgrade a previously installed key.

Installation uses the same Core definitions and selection rules as the browser, including relationships applicable to the current installation selection. It does not retroactively add all possible relationships between separately installed presets. Mutations, stored setup completion, receipt and redacted audit commit in one domain transaction. Receipt items contain created record/relationship type IDs and revisions, followed by the domain ID and resulting setup revision with outcome `setup_completed` or `presets_installed`. The normal bounded command history and 24-hour retry window apply. Identical retries read an existing receipt before revalidating the catalog, so a subsequent catalog or schema change does not duplicate installation. Domain creation is available in contract 1.10.

## Domain rename in 1.9

`rename_domain` preserves domain identity, Default status and content. Obtain the revision from domain discovery using `records.read`; `domains.manage` grants rename and capability discovery independently of data reads. Names are trimmed, limited to 100 characters and unique under the shared catalog rules.

Registry migration 3 commits the rename, receipt and redacted audit together, then publishes the matching cached catalog. Identical retries return the original receipt for 24 hours, including after later browser edits, without undoing those edits. Changed requests or targets under the same credential/action/key fail with `retry_conflict`; credential rotation starts new ownership.

`get_instance_info` includes `domainRegistrySchemaVersion`. Capability `domainWriteLimits` advertises 1,000 retained registry commands, 65,536 receipt bytes, a 24-hour retry window and seven additional days of tombstones. Cleanup runs during subsequent writes. Audit retains at most 50,000 events and 90 days, with IDs/action/outcome/correlation/time only; names and bearer credentials are excluded. Registry history is included in deployment-wide database snapshots. Domain deletion remains unavailable over MCP.
## Recoverable domain creation in 1.10

Create a client-generated domain UUID and retry UUID once, then call `create_domain` with those IDs and a name. Preserve all three on retry. This is a deployment-wide operation: `domainId` is the proposed new identity, not an existing domain selector. Empty/Default/existing IDs, duplicate names and IDs with unowned storage are rejected. Creation preserves the existing Default domain. Use `get_setup_state` and `complete_setup` separately to initialize the new domain.

Registry migration 4 reserves the identity, normalized name and credential-bound request before initializing its database. Pending domains are hidden from both catalog reads and domain-scoped application access. Once storage is initialized, publication, receipt, committed audit and reservation removal share one registry transaction; cache publication follows commit. Failure during final publication leaves a resumable reservation and no visible domain or successful receipt.

Cancellation before reservation leaves no intent. After reservation, a request failure or cancellation does not cancel the accepted creation: an identical retry or application startup resumes the same domain. A pending request has no automatic expiry. Startup completes reservations before serving requests and fails if recovery cannot complete; correct the underlying storage/migration fault and restart. Credential rotation or revocation prevents new calls under the old credential but does not undo previously accepted intent. Changed payloads or targets under an existing retry key fail with `retry_conflict`; a new credential cannot take over that reservation.

`domainWriteLimits.maximumPendingCreations` is 16 across browser and MCP. Each MCP reservation also holds a slot in the 1,000-command registry history limit. Existing pending retries and committed receipt replay remain available when new requests hit quota. The 24-hour receipt replay window starts on completion, followed by seven days of tombstones. Browser creation uses the same reservation/migration/publication path and can resume its pending name on retry or startup.

Backups include pending intent in the registry snapshot and include database/media only for published domains. Restoring such a backup recreates the still-empty reserved database and completes the same domain identity at startup. Pending files are never exposed for user writes or blindly deleted after an interrupted request. Local interruption, recovery, backup/restore, scope and retry tests pass; live MCPHub and interactive browser behavior remain unverified. Owner: Agent; review: 2026-09-14.
## Structured search in 1.11

`query_records` adds structured filtering and sorting while preserving the existing `search_records` inputs, default ordering and legacy page clamping. The new tool rejects page values outside 1-10000 and page sizes outside 1-100. Omitted `domainId` selects Default. An explicit unknown domain fails; supplied type, filter and sort IDs must exist in that domain. A valid field need not be attached to the selected record type: a filter then matches no values and a sort treats absent values as missing.

`query` searches names, aliases and stored values with literal substring matching; SQL wildcard characters are escaped. Query and filter values are trimmed, limited to 500 and 2000 characters respectively. Each non-null filter has `fieldDefinitionId`, `operator` and non-blank `value`. At most 10 filters are accepted, combined with AND; each can match any stored value of its field. Operators are exact lowercase names:

| Operator | Shared application behavior |
| --- | --- |
| `equals` | Equality against stored text, numeric/date/temporal representation, a tag, or location display context; text/tag/context comparison uses SQLite NOCASE |
| `contains` | Literal substring in text, a tag, or location display context |
| `greater_than`, `less_than` | Compare numeric sort values with a finite invariant number; NaN/infinity are rejected |
| `before`, `after` | Strict comparison against normalized temporal sort keys; exact dates compare at midnight, so the same day does not match either operator |

Temporal bounds accept `19c`, `1980s`, `YYYY`, `YYYY-MM`, `YYYY-MM-DD`, `YYYY-MM-DDTHH:mm`, or `YYYY-MM-DDTHH:mm:ss`. Coarse and approximate values use their stored sort key; these filters do not establish an exact calendar occurrence or interval overlap.

Omit `sort` for recently updated first, with display name and ID tie-breakers. An empty sort object selects display-name order; `descending` defaults false. A sort with `fieldDefinitionId` uses the first stored value by ordinal (and first tag where applicable), then display name and ID. SQLite missing-value ordering applies: missing values sort first ascending and last descending. Results contain bounded record summaries and `totalCount`; count and rows share one SQLite read transaction, including empty pages. Separate page requests see live data and do not pin a multi-request snapshot.

```json
{
  "domainId": "10000000-0000-0000-0000-000000000001",
  "recordTypeId": "20000000-0000-0000-0000-000000000001",
  "query": "alias",
  "filters": [
    { "fieldDefinitionId": "30000000-0000-0000-0000-000000000001", "operator": "greater_than", "value": "12" }
  ],
  "sort": { "fieldDefinitionId": "30000000-0000-0000-0000-000000000001", "descending": true },
  "page": 1,
  "pageSize": 25
}
```

Use IDs returned by discovery; the example IDs are placeholders. Application errors use `isError` and `structuredContent.error` with `permission_denied`, `not_found`, `validation_failed` or `temporarily_unavailable`, a bounded message and correlation ID. Malformed protocol arguments and unknown nested properties can fail at binding before this envelope. `records.read` is required independently of write grants. Search is read-only and does not issue command receipts.

## Contact upload tools in 1.12

A selected `contacts.import` credential can now upload contact files and validate their contents. This grant independently permits instance/capability discovery; select `records.read` separately to discover target domains. Existing credentials do not gain it automatically. API scopes remain unchanged. All five tools require an explicit domain and recheck the authenticated MCP credential on every request; rotation/revocation prevents the old credential from continuing, and a replacement cannot take over its upload.

Begin with purpose `contact_import`, a UUID retry key, the exact byte length, whole-file SHA-256 hexadecimal digest and content type `text/vcard` or `text/x-vcard`. No filename, server path, URL fetch or image-upload purpose is supported. Send sequential chunks with their own SHA-256 digest. Identical accepted offsets and bytes replay without appending; changed metadata/bytes, gaps, overlaps, incorrect checksums and bounds fail. Begin retries report the current session, including a cancelled/expired state, rather than allocating again. Runtime chunk-limit changes do not suppress an existing begin-key replay.

`uploadLimits` advertises the implemented staging quotas and effective chunk/file sizes. Chunk size is the lesser of 256 KiB and the decoded Base64 capacity of the configured request ceiling minus 8192 bytes of JSON overhead. Maximum contact bytes is the lesser of 5 MiB and 256 effective chunks. At request ceilings of 8192 bytes or less, new uploads are unavailable and the effective sizes are zero. Use compact Base64 JSON; unusually escaped or oversized RPC envelopes may hit the HTTP request ceiling, so reduce the chunk size in that case. Requests exceeding the transport ceiling can fail before tool-level structured errors.

Payload expires 60 minutes after begin, with no extension. Begin-key replay lasts 24 hours, followed by seven more days of metadata retention. At most 256 chunks per upload, 64 MiB reserved bytes, 64 active sessions globally, eight active sessions per domain, four per credential/domain and 1000 metadata rows are retained. Expiry and cancellation remove staged bytes; application startup and a five-minute worker clean expired payload/metadata. Respect transport rate limits and inspect status after a lost response before resuming.

`complete_upload` seals byte integrity, streams strict UTF-8 vCard 3.0/4.0 validation and returns the validated contact count. It does not reveal contact contents, create records or create an import preview. Internal `sealed` status means integrity only; malformed vCards can remain sealed and must not be interpreted as validated. Call completion again to verify content while the session is live. Contact preview and inspection are implemented in 1.13; apply is implemented in 1.14. Selected-contact export is implemented in 1.15 under a separate grant. Image lifecycle tools remain unfinished.

Staging schema 2 adds redacted audit: domain/upload IDs, action, outcome, correlation ID and time. Begin/chunk/seal/cancel mutations commit with their audit; replay requests can add audit entries without repeating byte mutations. Completion records validation success; failures carry only bounded codes and metadata. Audit failure prevents successful mutation responses and rolls back the associated staging transaction. Staging audit is bounded to 50000 entries and seven days, excluded from backup archives and cleared with staging on successful offline restore. Names, uploaded bytes, bearer credentials, credential fingerprints and digests are absent from audit rows.

Errors use `isError` plus `structuredContent.error` with `permission_denied`, `not_found`, `validation_failed`, `limit_exceeded`, `retry_conflict`, `retry_expired`, `upload_expired`, `upload_cancelled`, `upload_not_ready`, `upload_not_writable` or `temporarily_unavailable`. Body/argument binding and expired credentials may fail at the protocol/HTTP layer first. Local tests include an actual 5 MiB MCP transfer, retries, malformed content, audit rollback, configured request bounds and revocation mid-transfer. Deployed MCPHub behavior remains unverified. Owner: Agent; next action: durable contact preview/apply; review: 2026-09-14.

## Contact preview and import tools in 1.13-1.14

Contract 1.13 added preview creation and inspection under contacts.import. Contract 1.14 has 51 tools and adds atomic apply plus durable result inspection under the same grant. This purpose grant includes access to matching saved-contact names, IDs and duplicate reasons required to review an import; records.read is not also required. Every call demands the current credential and explicit domain. The domain must have the Person preset. New preview creation validates the uploaded vCard using shared browser mapping and duplicate rules, without changing records.

Use preview_contact_import with the complete upload and a retry UUID. It returns preview (previewId, uploadId, recordTypeId/name, revision, contactCount, expiresAtUtc) plus isCurrent. Repeating that key returns the same reviewed evidence and original expiry, even after domain edits. isCurrent is advisory at response time; apply must still validate the captured revision in its mutation transaction. A stale preview needs a new preview key. Expired keys fail during retained tombstones. Upload completion remains separate and creates no preview itself.

get_contact_import_preview returns status plus page/pageSize and contacts. Pages are 1-10000, sizes 1-25. Contact summaries include zero-based contactIndex, displayName, displayNameTruncated, recommendedAction and counts of aliases, field mappings, opaque properties, saved duplicate candidates and in-file duplicate candidates. Labels are limited to 160 UTF-16 code units without splitting surrogate pairs. Truncation is explicit; use the evidence reader for the full label. An out-of-range page is empty while retaining the total contactCount.

read_contact_import_evidence provides immutable formatVersion 1 evidence as base64-utf8-json byte ranges. Offset is zero-based, count is 1-16384; totalBytes remains stable for that preview/contact. Decode Base64 chunks, concatenate their bytes in offset order, then parse UTF-8 JSON. Follow nextOffset until null. Individual chunks can split Unicode sequences; do not decode them independently as text. Offset equal to totalBytes returns empty content and null nextOffset; greater offsets fail. Readers can retry an identical range safely. Every range rechecks credential/domain ownership, preview expiry and source-upload availability.

Evidence JSON uses camelCase names and snake_case enum strings. Its fields are index, card (version, properties and fingerprint), displayName, aliases, fieldMappings, opaquePropertyIndexes, duplicateCandidates, recommendedAction and importDuplicateCandidates. Each source property retains group, name, parameter names/values and the raw escaped value. Mappings identify source propertyIndex, fieldDefinitionId, fieldName, canonicalKey and typed input. Saved duplicate candidates retain recordId, displayName, reasons and isExactPriorImport; in-file candidates retain contactIndex, displayName, reasons, isExactCard and isStrongMatch. Recommended actions are create_separately, skip, merge_non_conflicting and replace_mapped_values. Unknown source properties and Apple labels remain inspectable. These are server-owned review results; there is no endpoint accepting a client-modified preview.

get_capabilities now includes contactPreviewLimits: page/label/evidence-chunk limits, a 128 KiB serialized tool-result ceiling (including structured content and its text copy, excluding the outer JSON-RPC/SSE envelope), storage quotas and lifetime. Evidence reads use SQLite BLOB slicing rather than loading the complete contact payload. Summary generation currently deserializes the requested contacts, bounded by the stored preview limit. Read requests cannot extend preview expiry. Cancellation removes dependent payloads; revocation or credential rotation denies old ownership. Success/failure audit contains bounded operation metadata, without labels, evidence or credential secrets.

`apply_contact_import` requires the preview ID and revision, a retry UUID, and exactly one selection for each consecutive zero-based contact index. Actions use the same snake-case names as preview recommendations. Merge and replace require an `existingRecordId` listed in that contact's saved-record duplicate evidence; create and skip reject a target. The first attempt requires live preview/source staging. Preparation reuses the browser rules, then the application transaction rechecks the domain import revision. Record changes, aliases, values, opaque-property provenance, one receipt, all outcomes and redacted application audit commit together or roll back together.

The response is a compact receipt with created/merged/replaced/skipped counts, contact count, completion time and 24-hour retry deadline. `get_contact_import_result` returns that receipt plus outcomes in pages of 1-100. Each outcome identifies contact index and action; creates, merges and replacements include the committed record ID and final record revision, while skips have null record data. The application database retains at most 1,000 import command headers and 100,000 live outcomes per domain. Outcomes are deleted after the retry window; the command tombstone remains seven more days. New commands fail at quota while valid existing receipts remain replayable.

Exact retries return the receipt before consulting temporary preview/upload state, so clients can resolve a lost response after expiry, cancellation or restart. Reusing a key with another revision, preview or selection payload returns `retry_conflict`. A second key cannot consume an already applied preview and returns `preview_consumed`, including while its application tombstone remains. A replacement credential cannot read or replay another credential's receipt. Result pages depend on the receipt window, not staging. `get_capabilities.contactPreviewLimits` advertises import selection, outcome page, receipt/outcome quota and retention limits.

Additional structured errors include `concurrency_conflict`, `preview_expired`, `preview_unavailable`, `preview_consumed`, `retry_conflict` and `retry_expired`. Images and deployed MCPHub verification remain outstanding. Owner: Agent; next action: record image transfer, then M4-M7. Review: 2026-09-14.
