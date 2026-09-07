# MCP write and transfer contract decisions

- Status: Record writes/deletion and basic relationship commands locally tested; other workflow previews and transfers planned
- Reviewed: 2026-09-07
- Owner: Agent
- Next review: 2026-09-14
- Parent: [management plan](mcp-management-plan.md)

## Authorization and credential identity

Keep exact application scope checks and the existing deployment gates. Scope selection is explicit on credential issuance/rotation; existing grants do not expand automatically. The pinned DnaX release has one credential per surface. Profiles select grants for that credential and are not per-client accounts. Issue `instance.read` for discovery only, or retain `records.read` for existing read clients.

For durable workflow ownership, derive an opaque SHA-256 fingerprint from the bearer credential only after DnaX has authenticated the request. The credential is generated with 256 bits of randomness. Do not use its eight-character display suffix as a unique identity, persist the bearer text, or emit the fingerprint in client-visible audit records. Include surface and domain in workflow keys. Recheck authorization on every invocation, including each upload/download chunk and apply request; rotation changes ownership and the old credential cannot resume a workflow.

The fingerprint identifies a credential generation, not a human or client. Shared credentials share authority. The underlying authentication boundary must be tested to prevent a cookie-only caller or a malformed Authorization header from acquiring a remote workflow identity. Remote administrative grants must not permit issuing grants outside the caller's allowed administrative policy.

## Mutations and revisions

Contract 1.6 applies the same bounded receipt/audit transaction to relationship commands. Type creation requires structure.write. Link creation/deletion requires relationships.write; creation compares the active type and both endpoint record revisions, and deletion compares the link revision. These grants also authorize instance/capability discovery but do not imply records.read or record mutation grants. See [the tool contract](mcp-contract.md#relationship-writes-in-16) for request fields, limits, replay semantics and remaining type-lifecycle work.

New domain writes require explicit `domainId`. Existing domain reads retain optional Default selection. Deployment-wide actions reject a domain selector.

- Create: include a UUID `idempotencyKey`; return created identity and revision.
- Update: include entity ID, `expectedRevision`, `idempotencyKey`, and explicit changes.
- Delete: include identity, revision and retry key; if dependent records/media/links would be affected, obtain and apply a deletion impact preview.
- Preview/apply: preview returns an opaque UUID, summary and relevant source revision. Apply includes the preview ID, complete choices and retry key. Reject expired, wrong-domain, wrong-credential or stale previews.

Record and record-type revisions are now opaque random tokens maintained by migration 21 triggers. Record tokens change when names, aliases, scalar/tag/location values or attached validation schemas change, including writes performed by import/schema services. Record-type tokens change with type or attached field configuration/lifecycle changes. Compare the token inside the same SQLite transaction as the change. Browser record edit submissions carry the token captured when their form loaded, and preserve that token through image refreshes. Media-only changes do not invalidate a content edit; migration 24 provides deletion-impact revisions for dependent changes; individual media/relationship commands still need their own revision contracts. Existing field/type preview fingerprints remain unchanged. This avoids relying on timestamps or missing mutations outside the MCP adapter.

Extend store transaction entry points as needed so validation, revision comparison, mutation, retry result and application audit outcome commit together. Reuse Core normalization; do not duplicate validators in remote SQL. Record batches now use a dedicated one-domain transaction entry point accepting the bounded validated operation set. Do not simulate atomic batches with a loop over separately committed service calls.

## Patch shape

Use a bounded list of named operations, not a full record replacement inferred from omitted properties:

```json
{
  "domainId": "00000000-0000-7000-8000-000000000001",
  "id": "01900000-0000-7000-8000-000000000002",
  "expectedRevision": "opaque-revision-from-read",
  "idempotencyKey": "01900000-0000-7000-8000-000000000003",
  "changes": [
    { "operation": "set_name", "value": "Fictional person" },
    { "operation": "replace_aliases", "values": ["Example alias"] },
    {
      "operation": "set_field",
      "fieldDefinitionId": "01900000-0000-7000-8000-000000000004",
      "temporal": { "value": "2010", "precision": "decade", "isApproximate": true }
    }
  ]
}
```

Allowed record operations initially: `set_name`, `replace_aliases`, `set_field`, `clear_field`. Omitted state is unchanged. An empty alias list clears aliases. `clear_field` has no value payload and still enforces required-field rules. `set_field` must contain exactly the appropriate scalar/tags/temporal/location value shape. Reject null operation payloads, multiple operations targeting the same field/name/alias collection, or unrelated properties. Current Core permits one value per field; preserve ordered stored values when reading and do not introduce unsupported multi-value editing. Use lowercase precision strings matching discovery and convert them explicitly to Core enums.

Field and record-type actions remain separate tools with explicit request types. Configuration changes, merge policies and retirement never arise implicitly from a record edit.

## Durable retry and preview state

Migration 22 adds a per-domain command ledger keyed by surface, credential fingerprint, action and retry UUID. It stores canonical request hash, committed result descriptor, committed timestamp and expiry. Canonicalization recursively sorts object property names while preserving array order, typed values and omitted/null distinctions; the command adapter must include domain, action, payload and expected revision. Reusing a key with different content returns `retry_conflict`.

The implemented record ledger retains results for 24 hours and tombstones for seven further days. It rejects known expired keys with `retry_expired`, caps retained commands at 10,000 per domain and receipts at 64 KiB, and cleans expired state during subsequent writes. A replay returns the original receipt (IDs, revisions, outcomes and commit/retry timestamps), not a fresh mutation or current replacement record. The ledger, record batch and committed audit entry share one transaction. Tests cover restart replay, changed hashes, expiry, ownership/domain mismatch, concurrent duplicate calls and all-or-nothing rollback. These bounds are advertised by capability discovery. Clients must not reuse keys after the finite retry window; the adapter rechecks permissions before replay.

Record batch preview metadata belongs to the selected domain with a 15-minute expiry. Migration 23 limits each preview to 1 MiB of prepared operations and summary and each domain to 100 previews. Startup, five-minute periodic and on-create cleanup remove expired previews; apply immediately drops the prepared values. Other preview families remain planned. Store only what apply needs, and invalidate previews when relevant revision evidence changes. Contact bytes and image staging live under private opaque temporary paths, outside exported media and backup entry selection. Apply consumes a preview transactionally; retries use the command ledger. Expired/abandoned staging cleanup runs at startup and periodically. Set explicit metadata/result size bounds for each later preview family before its migration and implementation; long output is paged or represented by an operation/download ID.

Deployment-wide commands need a separate application-owned ledger adjacent to the domain catalog; do not edit DnaX-owned tables. Snapshot/backup implications of the ledger and credential rotation must be covered in M6. Do not claim transaction atomicity across separate SQLite databases: orchestrated operations need staged state and restart recovery where a single database transaction cannot cover them.

## File transfer

Select MCP chunk tools for the initial implementation. A disposable test-host probe through the pinned DnaX/SDK HTTP transport verified a 256 KiB random binary payload and its SHA-256 digest, plus rejection above the 1 MiB request ceiling. This proves local transport behavior, not deployed MCPHub compatibility. No connector configuration was changed. Direct streaming can be added later if its authenticated client path is demonstrated.

Proposed tools and semantics:

1. `begin_upload(domainId, purpose, byteLength, sha256, contentType, idempotencyKey)` returns an opaque upload ID, expiry and maximum decoded chunk bytes. Purpose is contact import or record image; require that purpose's scope.
2. `write_upload_chunk(uploadId, offset, contentBase64, sha256)` accepts at most 256 KiB decoded bytes. Require sequential offsets; an identical retry at an accepted offset succeeds without appending again. Reject gaps, overlaps with different bytes, length overflow and bad checksums before changing state.
3. `get_upload_status(uploadId)` returns accepted byte count, expiry and state. `complete_upload(uploadId)` validates total length/hash and content, then makes it available to the relevant preview/add-image command. `cancel_upload(uploadId)` marks it unavailable and schedules cleanup.
4. Exports and explicit image/backup downloads return a credential-bound download ID and byte length. `read_download_chunk(downloadId, offset, maximumBytes)` returns bounded Base64 content, next offset, checksum and end-of-file status. Byte offsets make retry deterministic. Never return an arbitrary server path or bearer-bearing URL.

Keep current decoded contact/image limits. Advertise the effective chunk size reduced as necessary for configured request limits and Base64/JSON overhead; reject an unusably small configuration clearly. Apply quota and free-space checks before staging, and stream file reads/writes so memory scales with chunk size. Backup archives are never held as a single JSON result.

The default 60-requests/minute limit permits a 5 MiB contact upload in 20 data chunks and a 10 MiB image in 40, plus lifecycle calls. Larger downloads must respect 429/backoff and resume from the last confirmed offset. Test long transfers, revocation midway, expiry and interrupted cleanup. Chunk completion does not imply contact import or image attachment; those are separate authorized mutations.

## Errors, audit and operations

Use stable structured error codes: `permission_denied`, `not_found`, `validation_failed`, `stale_revision`, `stale_preview`, `preview_expired`, `retry_conflict`, `retry_expired`, `limit_exceeded`, `operation_not_cancellable` and `temporarily_unavailable`. Include a bounded field path/correlation ID where useful, never uploaded contents or host paths. Mark MCP tool failures as errors independently of HTTP transport status.

Persist application action/outcome and domain with correlation metadata. The pinned DnaX transport audit does not supply domain or guarantee an accurate tool-level result when an MCP error is carried by HTTP 200. Do not log content, bearer credentials, preview bodies or import filenames.

Long operations expose queued/running/succeeded/failed/cancelled state and an authoritative terminal result. Cancellation is accepted only before an irreversible commit; if commit happened, return success and its result. A disconnected client must use operation status or the retry key to establish outcome. Background operations must use a fresh explicit domain scope rather than retain a request-scoped service.

## Verification gates

Before a write tool ships, test its exact-scope denial, invalid domain, stale revision, bad patch, transaction rollback, concurrent browser/MCP edits, duplicate request, changed retry payload and restart replay. Upload/download tests additionally cover mismatched content, bounds, offset replay, quotas, cleanup and revoked credentials. Test-only transfer probes must never be registered by the production application.

Remaining work: implement relationship/setup commands, other preview state, operation lifecycle and file transfer; finalize preview/upload quotas and verify the live client path during M7. MCP validate/create/patch and previewed batches now use the ledger, structured errors and bounded failure audit. Real tool tests cover authorization, concurrency, replay, preservation and isolation. Agent owns implementation; review on 2026-09-14. These decisions do not narrow the remaining management scope in M4-M7.

Contract 1.7 extends structure.write to custom record-type creation and field creation/attachment. Type and reusable-field revisions are checked transactionally. Shared browser paths enforce the same revision and required-value rules. Schema commands reuse the existing command history and return ordered receipt items: created field then updated type for create-and-attach, or just updated type for reuse. Other schema lifecycle operations remain planned.

Contract 1.8 extends structure.write to onboarding and preset installation. A read-only domain setup revision and a packaged-catalog revision bind new requests to reviewed state. Blank selections require acknowledgement. Command receipts are checked before current-catalog validation, and installation/completion commits with receipt and audit. Domain creation/rename requires a separate registry transaction and recovery design; it is not exposed yet.


## Contact staging implementation checkpoint

Reviewed 2026-09-07. Owner: Agent. Next review: 2026-09-14.

The Core/Data upload store is implemented and locally tested. It is not yet registered as MCP tools; the production contract remains 1.11 with supportsFileTransfer=false. The proposed complete_upload operation still requires content validation and auditing before exposure. Internal sealing currently verifies only declared length and whole-file SHA-256; sealed bytes are not validated contacts and do not authorize import.

A separate DnaX-managed remote-transfers.db holds credential/domain-bound sessions and binary chunks. No client filename or server path is accepted. Initial purposes are limited internally to contact staging with text/vcard or text/x-vcard and 1 byte through 5 MiB declared length. Begin uses a UUID retry key bound to normalized type, length and SHA-256; exact retries return the current session status. Writes are sequential, at most 256 KiB decoded, with at most 256 chunks per session. An identical accepted offset and byte sequence replays without appending; gaps, changed boundaries, checksum mismatch and declared-length overflow fail before persistence. Byte insertion and accepted-offset advancement share a transaction. Hashing and internal copying consume one chunk at a time.

| Bound | Implemented value |
| --- | --- |
| Active reserved payload, deployment-wide | 64 MiB |
| Active sessions, deployment-wide | 64 |
| Active sessions per domain | 8 |
| Active sessions per credential within a domain | 4 |
| Retained session metadata, deployment-wide | 1000 |
| Payload lifetime | 60 minutes from begin; no extension |
| Begin-key replay | 24 hours from begin |
| Metadata tombstones after replay expiry | 7 additional days |

Declared bytes reserve quota before any payload arrives. Completed/sealed payloads continue to occupy quota until cancellation or expiry. Pending allocations and chunk writes check free space with a two-times growth allowance and 16 MiB headroom. Small-chunk metadata growth is bounded independently. Existing begin/chunk retries remain usable when new allocations reach quota. The future transport adapter must reduce decoded chunk size for configured request limits, ensure the declared upload fits within 256 chunks, and advertise the resulting bounds before accepting it.

Cancellation deletes staged chunks transactionally and preserves terminal metadata. Expiry is enforced on use; startup, five-minute background sweeps and begin requests clean expired bytes and old metadata. SQLite secure_delete is enabled on staging connections, but this is not a forensic-erasure guarantee for filesystem journals or storage devices. Upload state is excluded from backup archives. Successful offline restore discards the former staging database, including from the generated rollback directory, and invalidates old handles; failed restore can recover prior staging alongside the original deployment.

Remaining before exposing uploads: authenticated contacts.import adapters with permission checks on every call, effective transport limits, redacted mutation audit, vCard content validation, and protocol-level cancellation/revocation/retry tests. Next implement durable credential/domain-bound contact previews and revision-checked apply using the existing Core mapping and conflict decisions. Deployed MCPHub transfer remains unverified and no integration changes are authorized by this checkpoint.


2026-09-07 validation checkpoint: ContactUploadService now seals upload integrity and invokes the shared vCard parser through a scoped asynchronous stream over stored chunks. ParseAsync reads at most 16 KiB per request and enforces the existing 5 MiB, 1000-card and 2000-properties/card bounds with strict incremental UTF-8 decoding. It handles split multi-byte characters and folded lines without a complete decoded-file copy or repeated concatenation of the growing folded value. Parsed cards and the largest logical line still occupy memory proportional to their bounded content; this is not a constant-memory object model. The synchronous browser parser shares the same state machine and fingerprint rules. Read leases are disposed after their callback, require complete consumption and recheck upload availability before returning. Semantic failure leaves integrity-sealed staging, not a validated/imported contact. Remaining exposure gates: authenticated contacts.import tools, effective request/chunk limits and redacted audit, followed by durable preview/apply. Owner: Agent; review: 2026-09-14.


2026-09-07 remote upload checkpoint: contract 1.12 exposes begin_upload, write_upload_chunk, get_upload_status, complete_upload and cancel_upload under contacts.import. The adapter derives ownership only from authenticated MCP credentials and rechecks the purpose grant on every call. It advertises request-aware Base64 chunk capacity with 8192-byte envelope headroom and a file limit bounded by 256 chunks. Existing begin retries survive lowered runtime limits; new allocations must fit current limits. Staging schema 2 records bounded redacted audit in mutation transactions and logs validation/failure outcomes separately. Full 5 MiB transfer, malformed content, retry, scope, expiry, audit rollback and revocation tests pass locally. Earlier exposure-gate notes are superseded. Contact preview/apply/export and live MCPHub transfer remain incomplete. Owner: Agent; next action: durable contact previews and revision-checked apply; review: 2026-09-14.

## Durable contact preview storage

Reviewed 2026-09-07. Owner: Agent. Next review: 2026-09-14.

Staging migration 3 stores server-generated preview headers and separately ordered contact payloads. The shared service parses a live sealed upload using the selected domain's Person mapping and captures the domain import revision. Clients cannot submit preview objects through MCP. This storage checkpoint adds no remote tool; contract 1.12 still exposes upload/validation only.

A preview UUID is bound to domain, credential fingerprint and source upload. A retry UUID identifies the original upload request and returns the same reviewed result even if records subsequently change; applying must check the captured revision. Reusing that key for another upload fails. Preview lifetime is 15 minutes, capped by the source upload's expiry, without extension on replay. Source cancellation invalidates access and deletes preview payloads in the cancellation transaction. Reads check ownership, expiry and source state in one SQLite snapshot. The internal contact reader supports page 1-10000 and page size 1-100; it is not a transport response contract.

Storage limits are 32 MiB of complete serialized contact evidence per preview, 128 MiB deployment-wide, 32 active previews globally, 8 per domain, 4 per credential/domain and 1000 retained headers. Oversized evidence fails explicitly without truncating mappings or duplicate candidates. The upload's 5 MiB limit does not imply every expanded preview fits these bounds; clients may need to split a file. Serialization, contact rows and duplicate discovery are bounded by different limits; this is not a constant-memory pipeline. Preview headers and payloads commit with a redacted contacts.preview audit entry. Concurrent identical retries share one identity.

Expiry and abandoned-source cleanup run through the staging startup/five-minute/begin sweep and new preview allocations. Payloads and record-type names/revisions are removed; minimal retry tombstones remain until eight days from creation, then are deleted. Expired retry keys cannot create replacement previews during that interval. Previews are excluded from backups and invalidated on offline restore with the rest of staging.

Remaining work: response-size-bounded MCP preview summaries and detail inspection, atomic apply with durable per-contact outcomes and retry receipts in the application database, then export/images. Owner: Agent; next action: implement the inspection projection and application receipt transaction before exposing import apply.
2026-09-07 preview exposure update: contract 1.13 now exposes the durable preview service through authenticated contacts.import tools. Header/current-revision status, summary paging and complete evidence ranges are documented in mcp-contract.md. Evidence format 1 uses camelCase properties and snake_case enums; the stored JSON is sliced by SQLite byte offsets with a 16 KiB read bound. This supersedes the earlier storage-only exposure boundary. Owner: Agent; next action: atomic import receipts and per-contact outcomes in the application database; review: 2026-09-14.

2026-09-07 contact apply update: application migration 28 and contract 1.14 implement revision-checked atomic apply, credential-owned 24-hour receipts, seven-day tombstones, and paged outcomes. A first attempt leases its live staging preview, prepares every selection with shared browser rules, and commits mutations, provenance, final record revisions, outcome rows, receipt and audit in the domain database. Exact replay checks the receipt before staging; a changed payload returns retry_conflict and a different key returns preview_consumed. Limits are 1,000 selections, 100 outcomes/page, 1,000 retained command headers/domain and 100,000 live outcomes/domain. Owner: Agent; next action: selected-contact export transfer; review: 2026-09-14.
