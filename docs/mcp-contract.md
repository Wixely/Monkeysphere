# MCP contract

- Contract version: 1.37
- Status: Local implementation; live deployment not verified
- Reviewed: 2026-09-27
- Owner: Agent
- Next review: 2026-10-24

## Transport requirements for clients

The surface speaks MCP revision `2026-07-28` over Streamable HTTP. These requirements come from the official MCP SDK, not from Monkeysphere or DnaX, so a client built on that SDK satisfies them automatically. They are recorded here because a hand-written JSON-RPC client will otherwise fail with an opaque HTTP 400 before reaching any tool.

Every request must carry:

- `Authorization: Bearer <credential>` for the activated surface.
- `MCP-Protocol-Version: 2026-07-28`.
- `Mcp-Method: <json-rpc method>`, for example `tools/list` or `tools/call`. Omitting it returns `-32020` "Missing required Mcp-Method header."
- `Mcp-Name: <tool name>` in addition, on `tools/call`. Omitting it returns `-32020` "Missing required Mcp-Name header."
- `params._meta` carrying `io.modelcontextprotocol/protocolVersion` and an `io.modelcontextprotocol/clientCapabilities` object.

`initialize` is not available on this revision and is rejected with `-32601`; begin with `tools/list` or `tools/call`. Responses may arrive as `application/json` or as a `text/event-stream` frame, so accept both and read the `data:` line when the response is streamed.

A minimal exchange that succeeds, with the randomized endpoint from the Remote access page:

```
POST /dnax-mcp-<random>/mcp
Authorization: Bearer <credential>
MCP-Protocol-Version: 2026-07-28
Mcp-Method: tools/call
Mcp-Name: get_instance_info
Accept: application/json, text/event-stream

{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
  "name":"get_instance_info","arguments":{},
  "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28",
           "io.modelcontextprotocol/clientCapabilities":{}}}}
```

## Implemented tools

| Tool | Required grant | Inputs / result |
| --- | --- | --- |
| `get_instance_info` | Any of `records.read`, `instance.read`, `records.write`, `records.delete`, `relationships.write`, `structure.write`, `views.manage`, `domains.manage`, `contacts.import`, `contacts.export`, `media.read`, `media.write` | No inputs. Application name, release version without build metadata, database schema version, contract version |
| `get_capabilities` | Any of `records.read`, `instance.read`, `records.write`, `records.delete`, `relationships.write`, `structure.write`, `views.manage`, `domains.manage`, `contacts.import`, `contacts.export`, `media.read`, `media.write` | No inputs. Granted scopes, implemented tool names and any-of scope requirements, allowed flags, Default domain ID, domain-selection support, write/file support and effective transport and record-write limits |
| `create_domain` | `domains.manage` | Required new non-Default `domainId` UUID, `name`, UUID `idempotencyKey`. Creates an isolated blank domain using a durable reservation |
| `rename_domain` | `domains.manage` | Required `domainId`, `name`, `expectedRevision`, UUID `idempotencyKey`. Revision-checked rename with durable receipt |
| `list_domains` | `records.read` | No inputs. Existing isolated domain catalog |
| `list_record_types` | `records.read` | Optional `domainId`. Up to 100 record types with their field definitions |
| `get_record_type` | `records.read` | Required `id`, optional `domainId`. One type or null |
| `list_saved_views` | `records.read` or `views.manage` | Optional `domainId`. One domain's saved views without their columns and filters |
| `get_saved_view` | `records.read` or `views.manage` | Required `id`, optional `domainId`. One view's full definition, or null |
| `run_saved_view` | `records.read` | Required `id`; `page` (1), `pageSize` (25), `includeValues` (true), optional `domainId`. The view's rows with the values of the fields it lists as columns |
| `create_saved_view` | `views.manage` | Required `domainId`, `name`, `recordTypeId`; optional `query`, `columnFieldDefinitionIds`, `filters`, `tags`, `groupByFieldDefinitionId`, `sortFieldDefinitionId`, `sortDescending`, `showTags`. A new view |
| `update_saved_view` | `views.manage` | Required `domainId`, `id`, `name`, `recordTypeId`; the same optional inputs. Replaces the definition; omitted lists are stored empty |
| `duplicate_saved_view` | `views.manage` | Required `domainId`, `id`, `name`. A copy under a new name |
| `delete_saved_view` | `views.manage` | Required `domainId`, `id`. Removes the definition. **Destructive**; no record content is touched |
| `list_graph_views` | `records.read` or `views.manage` | Optional `domainId`. One domain's graph views without their arrangement |
| `get_graph_view` | `records.read` or `views.manage` | Required `id`, optional `domainId`. One view with its node positions and viewport, or null |
| `query_graph` | `records.read` | Optional `graphViewId` or explicit `displayMode`/`selectedRecordIds`/`recordTypeIds`; `search`, `relationshipTypeId`, `focusRecordId`, `depth` (1), `nodeLimit`, `edgeLimit`, `domainId`. Nodes, edges, truncation flags and the limits applied |
| `create_graph_view` | `views.manage` | Required `domainId`, `name`; optional `displayMode`, `selectedRecordIds`, `recordTypeIds`, `nodePositions`, `viewport`. A new graph view |
| `update_graph_view` | `views.manage` | Required `domainId`, `id`, `name`; the same optional inputs. Replaces the view; an arrangement left out is forgotten |
| `delete_graph_view` | `views.manage` | Required `domainId`, `id`. Removes the arrangement. **Destructive**; no record or relationship is touched |
| `query_calendar` | `records.read` | Required `from`, `to`; optional `recordTypeId`, `recordTypeIds`, `fieldDefinitionId`, `limit` (500), `domainId`. Dated values in the range with their repeats |
| `export_calendar` | `records.read` | Required `from`, `to`; the same narrowing plus `offset` (0), `count` (16384), `generatedAtUtc` (echo it back when paging). The iCalendar document in base64 byte ranges with a SHA-256 of the whole |
| `list_reminders` | `records.read` | Optional `domainId`. Undismissed reminders with the value each watches and its due date |
| `create_reminder` | `records.write` | Required `domainId`, `fieldValueId`, `leadDays` (0-3650). A reminder on one dated value |
| `dismiss_reminder` | `records.write` | Required `domainId`, `id`. Deals with the occurrence now showing; a repeating value comes round again |
| `query_map` | `records.read` | Optional `south`/`west`/`north`/`east` (whole world), `recordTypeId`, `fieldDefinitionId`, `fieldDefinitionIds`, `page` (1), `pageSize` (100), `domainId`. Located records as pins with their precision |
| `get_map_settings` | `records.read` or `structure.write` | Optional `domainId`. Whether external tiles are on, the one host they reach, and the privacy disclosure |
| `set_map_settings` | `structure.write` | Required `domainId`, `externalTilesEnabled`; `acknowledgeExternalRequests` required to enable. Turns the external basemap on or off |
| `get_dashboard_settings` | `records.read` or `structure.write` | Optional `domainId`. Categories, recurring date fields, look-ahead, and the bounds and default |
| `set_dashboard_settings` | `structure.write` | Required `domainId`; optional `recordTypeIds`, `recurringFieldDefinitionIds`, `upcomingDays`. Merges onto what is stored |
| `list_upcoming_dates` | `records.read` | Optional `domainId`. Dates within the look-ahead, at their next occurrence, soonest first |
| `begin_upload` | `contacts.import` or `media.write` | Required `domainId`, `purpose` (`contact_import` or `record_image`), `byteLength`, `sha256`, `contentType`, UUID `idempotencyKey`. Bounded owned staging session; the grant required depends on the purpose |
| `write_upload_chunk` | `contacts.import` | Required `domainId`, `uploadId`, sequential `offset`, `contentBase64`, chunk `sha256`. Atomic bytes/offset with safe retry |
| `get_upload_status` | `contacts.import` | Required `domainId`, `uploadId`. State, accepted bytes, expiry and current chunk limit |
| `complete_upload` | `contacts.import` | Required `domainId`, `uploadId`. Integrity and UTF-8 vCard validation; returns upload status, contact count and `contentValidated` |
| `cancel_upload` | `contacts.import` | Required `domainId`, `uploadId`. Removes staged bytes and retains bounded terminal metadata |
| `preview_contact_import` | `contacts.import` | Required `domainId`, `uploadId`, UUID `idempotencyKey`. Durable reviewed preview header and advisory `isCurrent`; no record mutations |
| `get_contact_import_preview` | `contacts.import` | Required `domainId`, `previewId`; `page` (1), `pageSize` (25). Bounded contact summaries and current-revision status |
| `read_contact_import_evidence` | `contacts.import` | Required `domainId`, `previewId`, zero-based `contactIndex`; byte `offset` (0), `count` (16384). Complete evidence through bounded Base64 byte ranges |
| `apply_contact_import` | `contacts.import` | Required `domainId`, `previewId`, `expectedRevision`, UUID `idempotencyKey`, and one explicit selection per contact. Atomic import and durable receipt |
| `get_contact_import_result` | `contacts.import` | Required `domainId`, import `idempotencyKey`; `page` (1), `pageSize` (100). Durable receipt and paged per-contact outcomes |
| `add_record_image` | `media.write` | Required `domainId`, `recordId`, `uploadId`; optional `fileName`. Attaches a completed `record_image` upload as a new image and returns its metadata |
| `read_record_image` | `media.read` | Required `domainId`, `recordId`, `imageId`; `variant` (`preview`), byte `offset` (0), `count` (16384). One image variant read through bounded Base64 byte ranges with a whole-content digest |
| `export_contacts` | `contacts.export` | Required `domainId` and 1-100 distinct `recordIds` on the Person preset; byte `offset` (0), `count` (16384). One regenerated vCard 4.0 document read through bounded Base64 byte ranges with a whole-document digest |
| `query_records` | `records.read` | Optional `query`, `recordTypeId`, up to 10 `filters`, `sort`, `page`, `pageSize`, `domainId`. Structured filtering and sorting with strict bounds and snapshot-consistent totals |
| `search_records` | `records.read` | Optional `query`, `recordTypeId`, `page` (1), `pageSize` (25), `domainId`. Bounded result with total count |
| `get_record` | `records.read` | Required `id`, optional `domainId`. One record or null, including aliases, values, image metadata and up to 100 relationships |
| `get_record_relationships` | `records.read` | Required `id`, optional `domainId`. Up to 100 relationships, each with `expired` and `expiresAtUtc` |
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
| `create_relationship` | `relationships.write` | Required `domainId`, `typeId`, `sourceRecordId`, `targetRecordId`, `expectedTypeRevision`, `expectedSourceRevision`, `expectedTargetRevision`, UUID `idempotencyKey`; optional `note`, `expired`, `expiresAtUtc`. Creates a link and returns its ID/revision receipt |
| `update_relationship` | `relationships.write` | Required `domainId`, link `id`, `typeId`, `expectedRevision`, `expectedTypeRevision`, UUID `idempotencyKey`; optional `note`, `expired`, `expiresAtUtc`. Changes the link in place, keeping its ID, and returns its ID/revision receipt |
| `delete_relationship` | `relationships.write` | Required `domainId`, link `id`, `expectedRevision`, UUID `idempotencyKey`. Deletes only the link and returns a receipt |
| `list_tags` | `records.read` or `tags.manage` | Optional `domainId`. Every tag with `colour`, `icon`, `domainIds` and `revision`, or only those offered in one domain |
| `count_tag_usage` | `records.read` or `tags.manage` | Required `tagId`. Records carrying it, per observable domain |
| `create_tag` | `tags.manage` | Required `domainId`, `name`. Resolves an existing name to that tag and adds the domain rather than duplicating it |
| `set_tag_appearance` | `tags.manage` | Required `tagId`, `colour` (`#rrggbb`), `icon`, `expectedRevision`. Presentation only |
| `rename_tag` | `tags.manage` | Required `tagId`, `name`, `expectedRevision`. Renames it everywhere; renaming onto an existing name is refused |
| `set_tag_domains` | `tags.manage` | Required `tagId`, complete `domainIds`, `expectedRevision`. **Destructive**: a dropped domain loses the tag from every record in it |
| `delete_tag` | `tags.manage` | Required `tagId`, `expectedRevision`. **Destructive**: removed from every record in every domain |
| `get_record_source` | `contacts.export` | Required `domainId`, `recordId`. Import occasions and every retained line, values summarized with a 256-character preview |
| `read_record_source_value` | `contacts.export` | Required `domainId`, `recordId`, `ordinal`; optional `offset` (0), `count` (16384). One retained value in bounded ranges with a whole-value digest |
| `get_graph_settings` | `records.read` or `structure.write` | Optional `domainId`. The domain's graph limits, its layout flags, the bounds a limit must lie within, and the shipped defaults |
| `set_graph_settings` | `structure.write` | Required `domainId`; optional `warnUnsavedChanges`, `nodeLimit`, `edgeLimit`, `keepRecordsApart`. Omitted values are left as they stand; an out-of-bounds limit is refused |
| `create_record_type` | `structure.write` | Required `domainId`, `name`, UUID `idempotencyKey`; optional `symbol`. Creates a custom type and returns its ID/revision receipt |
| `create_and_attach_field` | `structure.write` | Required `domainId`, `recordTypeId`, `expectedRevision`, `name`, `typeId`, UUID `idempotencyKey`; optional `isRequired` (false), `choiceOptions`. Creates and appends a field; receipt contains the created field followed by the updated type |
| `attach_field` | `structure.write` | Required `domainId`, `recordTypeId`, `fieldDefinitionId`, `expectedRevision`, `expectedFieldRevision`, UUID `idempotencyKey`; optional `isRequired` (false). Reuses an active field; receipt contains the updated type |
| `get_setup_state` | `records.read` | Optional `domainId`. Effective setup status, domain-state revision, catalog revision and installed preset count; no persistence side effects |
| `list_installed_presets` | `records.read` | Optional `domainId`, `page`, `pageSize`. Local type IDs, preset keys/versions, names and lifecycle |
| `list_presets` | `records.read` | `page`, `pageSize`. Packaged record-type definitions with fields, versions and examples; result contains catalog revision and page |
| `list_starter_packs` | `records.read` | `page`, `pageSize`. Onboarding levels and selectable keys, including the blank option; result contains catalog revision and page |
| `list_relationship_presets` | `records.read` | `page`, `pageSize`. Packaged relationship definitions and prerequisite selection rules; result contains catalog revision and page, each with a `description` |
| `install_preset` | `structure.write` | Required `domainId`, `presetKey`, `expectedRevision`, `expectedCatalogRevision`, UUID `idempotencyKey`. Atomic single-preset installation with receipt |
| `complete_setup` | `structure.write` | Required `domainId`, `starterPackKey`, `selectedPresetKeys`, `expectedRevision`, `expectedCatalogRevision`, UUID `idempotencyKey`; `acknowledgeBlank` defaults false. Atomic selected installation and setup completion |

Remote deployment/activation and credential checks apply before invocation. Tool-level authorization is enforced even if discovery lists a denied tool. `instance.read` exposes no record names, domain catalog, credentials, endpoint paths or host paths. Discovery reports implemented functionality: `supportsWrites` is true and `supportsFileTransfer` is true for contact upload, preview and import, for selected-contact export (1.15), and for record image transfer (1.16). Image lifecycle beyond attaching and reading — deletion, captions and ordering — remains browser-only. Per-tool `allowed` flags reflect the caller's grants; write support does not imply permission or complete instance-management coverage.

Both list_domains and query_domains include an opaque domain `revision`. Domain registry migration 2 persists these tokens and changes them on renaming, including a rename back to an earlier name. Browser rename submits its captured revision; the registry update checks it transactionally and publishes the matching cached catalog only after commit. Contract 1.9 adds registry-owned rename receipts and the separate `domains.manage` grant. Contract 1.10 adds recoverable creation.

Omitted domain selectors retain the Default domain for backwards compatibility. Explicit malformed or unknown domain selectors fail closed. Domain listing is still under the single-administrator trust boundary. New paged tools accept page 1-10000 and page size 1-100, rejecting invalid values. They preserve deterministic ordering and report total count; empty pages do not lose that count. Legacy tools keep their response shapes and caps. Metadata catalogs currently use the shared application list services before paging; relationship rows are paged directly in SQLite.

Remote access settings select permissions for the next credential rotation. Existing grants remain selected by default, including unfamiliar existing grants that can be retained or removed. Unsupported new grants cannot be introduced. MCP offers `instance.read`, `records.read`, `records.write`, `records.delete`, `relationships.write`, `structure.write`, `domains.manage`, `contacts.import`, `contacts.export`, `media.write`, `media.read`, `tags.manage` and `backstage`; HTTP API offers `records.read`. `backstage` is not a scope in the ordinary sense: it authorizes no tool and permits no read on its own, and instead widens what the tools a credential already holds are allowed to see. See [Backstage records in 1.17](#backstage-records-in-117). Selections take effect only on rotation, which replaces the one credential for that surface. Write grants do not imply data reads; select `records.read` too when the client needs to discover schemas or retrieve records. Both new write grants permit instance/capability discovery; they do not permit unrelated record mutations.

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
  "contractVersion": "1.22",
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

## Record images in 1.16

Contract 1.16 has 54 tools and completes the M3 transfer surface with `add_record_image` and `read_record_image`, under a new `media.write` and `media.read` pair. The two are split for the same reason `contacts.export` was split from `contacts.import`: reading an `original` returns the retained bytes as uploaded, which can still carry camera metadata such as location that the display copies remove. Writing images does not permit reading them back, and neither implies `records.read`.

An upload now declares its purpose. `begin_upload` takes `contact_import` or `record_image`, authenticates against the grant that purpose requires, and the purpose is persisted with the session by staging migration 4. `contact_import` accepts `text/vcard` or `text/x-vcard` up to 5 MiB; `record_image` accepts `image/jpeg`, `image/png` or `image/webp` up to 10 MiB, matching the browser's own image limit. A retry that reuses a key with a different purpose fails with `retry_conflict`, and attaching an upload staged for another purpose fails with `purpose_mismatch`, so contact bytes can never arrive as an image or the reverse.

`complete_upload` behaves according to that purpose. A contact upload is parsed and reports `contentValidated: true` with its contact count. An image upload is sealed for byte integrity only and reports `contentValidated: false`, because the image itself is decoded when it is attached. Sealing has always meant integrity rather than semantics, and for images that distinction is now explicit.

`add_record_image` reuses the same pipeline the browser uses, so every existing rule still applies: the format must decode, dimensions are capped at 24 megapixels and 12,000 pixels per side, a record holds at most 50 images, the original is stored under an opaque name, and metadata-stripped WebP preview and thumbnail derivatives are generated. The optional `fileName` is retained as display metadata only, at most 200 characters, and never becomes a server path.

`read_record_image` returns one variant through bounded ranges: `preview` and `thumbnail` are the generated WebP copies, `original` is byte-identical to what was uploaded. Offset is zero-based, `count` is 1-16384, and `nextOffset` chains ranges until null. `contentDigest` is the uppercase SHA-256 of the complete variant and must match across every range of one read; a change means the image was replaced, so restart at offset 0. An offset equal to `totalBytes` returns empty content and a greater offset fails, matching `read_contact_import_evidence` and `export_contacts`. Nothing is staged for a read: each call reads the stored variant, so a torn read is detectable rather than silently mixed.

`get_capabilities` advertises `imageLimits` with the chunk, response, upload-byte, per-record and file-name bounds. Both tools record bounded `images.add` and `images.read` audit metadata containing no record names, image identifiers or bytes.

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

`complete_upload` seals byte integrity, streams strict UTF-8 vCard 3.0/4.0 validation and returns the validated contact count. It does not reveal contact contents, create records or create an import preview. Internal `sealed` status means integrity only; malformed vCards can remain sealed and must not be interpreted as validated. Call completion again to verify content while the session is live. Contact preview and inspection are implemented in 1.13; apply is implemented in 1.14. Selected-contact export is implemented in 1.15 under a separate grant, and image transfer in 1.16 under `media.write` and `media.read`. Image lifecycle — deletion, captions and ordering — remains unfinished.

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

## Backstage records in 1.17

Contract 1.17 keeps the same 54 tools and adds one permission, `backstage`. It is not a scope in the ordinary sense: it authorizes no tool and grants no read on its own, and a credential holding it alone can call nothing. What it does is change what the tools the credential already holds are allowed to see.

Monkeysphere lets an administrator mark a record with a backstage policy state. The first such state is `hidden`, and a hidden record is withheld from every ordinary read: `search_records` and `query_records` do not return it and do not count it, `get_record` answers as though it does not exist, and it appears in no relationship, graph, calendar, map, dashboard, reminder, duplicate-discovery or contact-export result. A visible record linked to a hidden one discloses no trace of the link. A credential without the grant cannot tell a hidden record apart from one that was never created.

A credential **with** the grant sees those records through every tool it can already use, exactly as an ordinary record. Two properties of the grant matter to an integrator:

- **It does not expire.** The browser's backstage mode ends 24 hours after it is entered, whether or not anyone remembers to leave. The grant has no equivalent: it applies on every call until an administrator removes the permission and rotates the credential. Treat it as standing authority and review it whenever credentials are rotated.
- **The deployment gate overrules it.** Backstage is off unless the deployment enables `Monkeysphere:Backstage:Available`. While it is off, a credential carrying the grant sees nothing of hidden records, the same as any other credential.

Existing credentials never gain the grant automatically; an administrator selects it when rotating a credential, the same as every other permission. It is offered on the MCP surface only.

Writes are unchanged: the grant does not permit changing a record's backstage state, which remains a browser operation performed by an account standing backstage.

## Retained source material in 1.18

Contract 1.18 has 56 tools and adds `get_record_source` and `read_record_source_value`, both under the existing `contacts.export` grant.

Monkeysphere has always kept the raw material an import arrived as, not only the parts it understood. What 1.18 adds is the ability to read it back. `get_record_source` returns the import occasions for one record — source kind, the producer's own format label such as a vCard version, the content fingerprint of the imported item, and when it happened — followed by every retained line with its grouping, name and parameters exactly as received.

The field that matters is `usedAs`. A line reported as `display_name`, `aliases` or `field_value` also exists as ordinary record data, and `field_value` names the field it landed in. A line reported as `unused` was understood by nothing: custom `X-` properties, vendor labels and embedded payloads all fall here, and they exist nowhere else in the record. That is what makes this surface worth calling rather than reading the record.

Values are summarized, never inlined. Each line carries `valueLength` and a `valuePreview` of at most 256 characters with `isPreviewTruncated` set when there is more, because an embedded photo or key routinely runs past what any listing should carry. `read_record_source_value` then reads one line in full through bounded character ranges: offset from 0, count 1-16384, `nextOffset` chaining until null, and a `contentDigest` that must be identical across every range of one read. It is returned as text exactly as retained, so an embedded payload arrives in whatever encoding the source used, commonly Base64. An offset equal to `totalLength` returns empty content and a null `nextOffset`; a greater offset fails. Nothing is staged and no record changes.

Two deliberate choices:

- **No new grant.** `export_contacts` already returns this same material, round-tripped into a vCard, so a separate permission would restrict nothing while implying a boundary that does not exist. A credential that can export contacts can inspect retained source material, and one that cannot do the former cannot do the latter.
- **A record that does not resolve and a record that retained nothing answer identically**, with empty content. Neither can be used to probe for the existence of records, and a record withheld by backstage policy discloses nothing here either.

`importId` is null for material retained before imports were individually attributed, which is everything imported by a deployment upgraded from schema 29 or earlier where the record had more than one import.

## Partial contact files in 1.19

Contract 1.19 adds no tools. It changes what `complete_upload` does with a contact file that is only partly usable.

Previously a single card the parser could not read failed the whole upload, so one unusable entry in a 200-contact export cost the caller all 200. That is the wrong trade for a format whose files are produced by other people's address books. A card that cannot be read is now set aside and reported, and the rest of the file proceeds.

`complete_upload` gains `rejectedContacts`. Each entry carries:

- `position` — where the card sat in the file, counting from 1 and including rejected cards, so it can be found in the source.
- `label` — a best-effort identifier taken from whatever the card did carry: `FN`, then `N`, `ORG`, `EMAIL`, `TEL`, `UID`. Null when the card carried nothing usable. Bounded to 100 characters and collapsed to a single line.
- `reason` — why it could not be read, in the same words the whole file used to fail with.

`contactCount` counts only the cards that can be imported, and rejected cards are absent from every later preview and apply step. A file whose cards are all unusable completes with `contactCount` 0 and every reason listed, which tells a caller more than a bare failure did.

What still fails the whole upload is structural damage: a missing `END` marker, a nested `BEGIN`, content outside a card, a continuation line with nothing to continue, invalid UTF-8, or exceeding the file, card-count or byte ceilings. In those cases card boundaries are unknowable, so salvaging part of the file would mean inventing contacts.

The same rule applies to the browser import, which lists rejected cards above the reviewable ones and imports the rest. `preview_contact_import` does not re-report the rejections: the parse happens at `complete_upload`, which is where the caller already receives them, and the staged preview holds only importable contacts. Storing them a second time would need a staging schema change to repeat information the caller has.

## Related records carry their picture in 1.20

Contract 1.20 adds no tools and no grants. `query_record_relationships` gains two nullable fields on each result: `imageId`, the related record's cover image, and `recordTypeSymbol`, its type's symbol. Both describe the record at the far end of the relationship, seen from the record that was asked about, so reading a relationship list no longer needs a second call per related record just to show who it points at.

`imageId` is null when the related record has no image, and names an image readable through the existing record-image surface under the same permission that returned the relationship. `recordTypeSymbol` is null only for a record type that has no symbol set. Neither field is a filter and neither can be written here.

The addition is additive: a client written against 1.19 ignores both fields and behaves exactly as before. The change exists because the browser now shows a record the same way everywhere — its picture beside its name — and a caller assembling the same view deserves the same single call.

## Universal tags in 1.21

Contract 1.21 adds no tools and no grants. Every record carries universal tags unless an
administrator has removed them from its record type, and they are ordinary record content, so they
sit under the existing `records.read` and `records.write` authority and are withheld with the
record by backstage policy. They are distinct from the `tags` field type, whose values stay on the
field value where they already were.

- `get_record` returns the record's `tags`.
- `query_records` accepts `tags`: at most ten, AND-combined, matched case-insensitively. A record
  must carry every tag listed, so adding one narrows the result.
- `create_record` and `validate_record` accept an optional `tags` list, normalized by the same
  rules the browser applies: trimmed, at most 100 entries of at most 200 characters, with
  case-insensitive duplicates removed and the caller's order preserved. Supplying tags for a record
  type whose tags an administrator removed is refused rather than stored where nothing would show
  them.
- `patch_record` gains `replace_tags`, alongside `replace_aliases` and with the same shape. A patch
  that does not carry it leaves the record's tags untouched, which is what lets a client written
  against 1.20 keep patching records without destroying data it does not know about.
  `replace_tags` with an empty `values` clears them.
- `preview_record_batch` accepts `tags` on a `create` operation. A batched `patch` carries them in
  a `replace_tags` change, as a single patch does.

**One compatibility note.** The canonical request hash for `create_record` and for batch previews
now includes tags, and its shape marker moves from 1 to 2. Two creates differing only by their tags
must not look like the same command to idempotent replay. A retry issued before an upgrade and
replayed after it reports `retry_conflict` rather than replaying, because the payload no longer
hashes the same. That is the safe direction — a refusal, not a second write — and it applies only
within the 24-hour retry window that spans the upgrade.

## Operations, backups and remote administration in 1.35 to 1.37

Contracts 1.35 to 1.37 have 115 tools, adding eleven, and five new grants. Together they are MCP
milestone M6: what an operator needs to know the deployment is healthy, to take and fetch a backup,
and to administer the remote surface itself.

- 1.35: `get_operational_status`, `list_backups`, `validate_backup`, `create_backup`.
- 1.36: `read_backup`.
- 1.37: `get_remote_access_state`, `list_remote_activity`, `set_remote_activation`,
  `rotate_remote_credential`, `revoke_remote_credential`, `rotate_remote_endpoint`.

### Five grants, because these are five different powers

`backups.read` lists packages and opens one to report its format, schema and contents.
`backups.write` takes a new package. `backups.export` reads a package's bytes out of the deployment.
`admin.read` sees the remote surfaces and the redacted activity log. `admin.manage` changes them.

They are deliberately separable in every direction, and the tests assert each direction rather than
just the happy path. A credential that can take a backup cannot inspect one. One that can read a
package out cannot discover what packages exist. None of them is a way into the records. And reading
the remote-access state is not administering it.

`backups.export` deserves its own sentence, because it is the most consequential permission on the
surface: a package contains every domain's records, their original images and the remote-access
state, so granting it is equivalent to granting a copy of everything.

### A remote rotation can never widen itself

This is the control that keeps `admin.manage` from being a privilege-escalation path rather than an
administration feature.

`rotate_remote_credential` refuses any permission the **calling credential does not already hold**. A
rotation over MCP narrows or preserves; it never adds. A credential holding nothing but `admin.read`
and `admin.manage` therefore cannot mint itself one that reads records, downloads backups or sees
backstage records — it is refused with `permission_denied` naming the permissions it tried to add,
and nothing is rotated.

Widening remains an operator action at the Remote access page in the browser. That is not an
oversight but the recovery route the refusal depends on: the browser path is deliberately unchanged,
and a test asserts an operator can still restore a grant a remote call had narrowed away.

### Acting on your own connection is allowed, and disclosed

Deactivating the surface a call arrived on, rotating its credential, revoking it, or moving its
endpoint all break the connection making the call. These are permitted rather than refused, because
refusing them would make a credential or endpoint believed to be compromised impossible to stop from
the client that noticed.

Every administrative result therefore carries `affectsThisConnection` and a `disclosure` saying what
just happened and how to recover. A rotation returns the new secret **once**, in the result; it is
never written to logs or to the audit record.

### Deployment policy wins

A surface the deployment does not permit to be activated, rotated or moved at runtime stays refused
however these tools are called. The scope says what the caller may ask for; deployment policy says
what the deployment will do, and the second wins. A test runs against a host configured to forbid
endpoint rotation and proves the denial holds for a credential that holds `admin.manage`, that the
surface is still reachable afterwards, and that what policy does permit still works.

### What these tools deliberately cannot do

- **Restore a backup.** Restore replaces every domain's data and must happen while the application is
  not serving, so it is an offline operator action: stop the deployment, run it against the package
  with the documented restore path, and start it again. No remote tool performs or schedules a
  restore, and `validate_backup` exists precisely so a package can be checked before an operator
  commits to that.
- **Delete or prune a package.** Retention applies to scheduled backups. Removing a package on demand
  needs a design that states what happens to an in-flight download and to the deployment's recovery
  position, and that design does not exist.
- **Change the backup schedule.** `get_operational_status` reports the configured schedule read-only.
  Runtime editing needs a persistence and configuration-precedence design; until it exists, the
  schedule is changed where it is configured, in deployment settings.
- **Widen a credential**, as above, or enable anonymous access, which no tool offers at all.
- **Report the host.** Operational status carries no paths, disk figures or process detail: those
  describe the machine rather than the deployment.

### Backups are deployment-wide

No backup tool takes a `domainId`. One package covers the registry, every domain's database and
original media, and remote-access state, and is validated and restored as one unit. This follows the
existing isolation invariant rather than softening it.

`create_backup` takes no idempotency key. A backup is not a domain command, and a second one is a new
timestamped package rather than a repeated write, so there is no receipt to replay. A successful call
returns the package's identifier, name, size and creation time, which is unambiguous; if a response is
lost, call `list_backups` and compare creation times rather than retrying blind. Large deployments make
creation slow, because it reads everything.

`read_backup` pages bounded byte ranges, offset 0, count 1-65536, following `nextOffset` until null.
Offsets are 64-bit: a package can exceed two gigabytes. Unlike `export_contacts`, the digest is
**opt-in** via `includeDigest`, and the difference is worth stating: that export regenerates its
document per call, so a stable digest is what proves two chunks came from one document, while a backup
package is written once and never rewritten. Computing the digest reads the whole package, so asking
on every chunk would make a download quadratic. A package pruned mid-read fails with `not_found`
rather than returning a short document.

Validating a package that does not exist answers `not_found`, not `temporarily_unavailable`. The
latter had been telling callers to retry something that can never succeed.

### Auditing off is not the same as nothing happening

`list_remote_activity` fails with a validation error when remote-access auditing is disabled for the
deployment, rather than returning an empty page. An operator reading an empty list should not have to
guess whether the deployment was quiet or simply was not writing anything down. Limit is 1-200, and
the records are redacted by DnaX: no credential, no request body, no record content.

## Relationship-type lifecycle in 1.34

Contract 1.34 has 104 tools, adding two, and no new grant. It closes MCP milestone M4.

- `rename_relationship_type` and `retire_relationship_type`, both on `structure.write`, joining
  `create_relationship_type` there.

They sit on `structure.write` rather than `relationships.write` because recording that two people
correspond and changing what "corresponds with" means are different powers. A credential that can link
records cannot relabel or retire the definition those links are recorded under.

Both take `expectedRevision` from `list_relationship_types` and return a receipt with the usual 24-hour
identical-retry replay. A stale revision is `stale_revision`, from the check the store already had.

`directionality` cannot be changed. Every existing relationship was recorded under it, and a symmetric
type stores its endpoints in a canonical order that a directional one does not, so flipping it would
silently reinterpret data rather than change a label. Retire and recreate if that is really what is
wanted. Labels follow the rule `create_relationship_type` already follows: a directional type requires
an `inverseName`, and one supplied for a symmetric type is ignored rather than refused, because it would
never be read.

Retiring keeps every relationship already recorded, labels included; the type stops being offered, and
`create_relationship` against it is refused. A retired type can still be relabelled, because its
relationships are still shown somewhere.

### What is deliberately absent

There is no tool for editing a relationship's note or moving its endpoints. Those are not currently a
shared application command — the browser reaches them through page-local code — so advertising a tool
would mean writing a second implementation of rules that have only ever had one. That extension is
tracked separately rather than smuggled in behind M4.

## Reusable-field lifecycle in 1.33

Contract 1.33 has 102 tools, adding seven, and no new grant. With 1.32 it completes MCP milestone M4's
structure surface: a reusable field can now be inspected, renamed, retired, merged and converted from a
remote client, all on `structure.write`.

- `get_field_usage`, `preview_field_merge`, `preview_field_conversion`.
- `rename_field`, `retire_field`, `merge_fields`, `convert_field`.

`list_field_definitions` already covered listing and has since 1.x; it stays on `records.read`.

### structure.write is not a licence to read records

This is the one place where the field lifecycle could have leaked record content, so it is worth being
explicit about what these tools do and do not say.

`get_field_usage` reports counts and the definition. How many record types carry the field, how many
values exist, how many saved views name it — never the values, and never the records holding them.
Anybody wanting those asks `query_records` under `records.read`.

`preview_field_conversion` has to report which values cannot be represented in the new type, because
otherwise a caller cannot fix them and the conversion stays refused forever. It reports the count
always, and names the records **only when the credential also holds `records.read`**;
`issueRecordsWithheld` says which happened, and withheld entries carry a null `recordId` and
`"(withheld)"` as the name. That is the same shape the browser already uses for a record withheld by
backstage policy. Values marked backstage are withheld from the names regardless, as they are in the
browser: a conversion still rewrites them, and only who holds them is concealed.

### Which fingerprint goes where

Three different revisions appear, and sending the wrong one gets `stale_revision`:

- `rename_field` and `retire_field` take `expectedFieldRevision`, the field's own revision from
  `list_field_definitions`. A record gaining a value is no reason to refuse a rename.
- `convert_field` takes `expectedUsageRevision` over the **one** field, which both
  `preview_field_conversion` and `get_field_usage` return.
- `merge_fields` takes `expectedUsageRevision` over **both** fields, which only
  `preview_field_merge` returns. Either side moving is a reason to look again.

### Merging and converting

Two fields can merge only if they share a type **and** configuration. A field's recurrence is part of
its configuration, so a repeating date cannot be merged into a one-off one: doing so would quietly
change what every moved value means. An incompatible pair comes back from the preview as
`isCompatible: false` with the reason, not as an error, because asking is a reasonable thing to do.

`conflictResolution` is `reject`, `keepTarget` or `keepSource`. `reject` refuses when any record holds
both values; the other two say which value survives, and the reminder on the surviving value survives
with it. Where either side required the field, the merged attachment is required — the stricter rule is
the one that was being relied on. Values, attachments, reminders, import provenance rows and every
saved-view reference (columns, filters, grouping and sort) move to the target, and the source retires.

A conversion creates a new field, rewrites every value into it, moves everything that pointed at the old
one, and retires the original. If **any** value cannot be represented in the new type the whole
conversion is refused rather than carrying what it can: a silent partial conversion loses data that
looked like it had been converted. `preview_field_conversion` is how to find those values first. Its
receipt names the created field before the retired original, because the new identifier is the one a
caller has no other way of learning.

### A defect this surfaced

Migration 39 made a reminder come round again, which required the uniqueness rule over a value and its
lead time to stop excluding dismissed rows. The field merge's own reminder de-duplication had not been
updated with it: it collapsed a colliding pair only when neither had been dismissed. Merging two date
fields on a record where one reminder had been dismissed therefore failed on the index — from the
Structures page as much as from here. A reminder now follows its value, collapsed by which value the
conflict policy keeps and nothing else. Found by asking what a merge does to a reminder while wrapping
these tools, not by running into it.

## Record-type lifecycle in 1.32

Contract 1.32 has 95 tools, adding five, and no new grant. It opens MCP milestone M4: a remote caller
could create a record type and never tidy it up, so structure accumulated and only a person at the
browser could rename, retire or merge it.

- `preview_record_type_retirement` and `retire_record_type`.
- `preview_record_type_merge` and `merge_record_types`.
- `update_record_type`, which covers the name, the symbol and whether the type carries universal tags.

All five sit on `structure.write`, the previews included. A preview counts nothing a reader could not
count for themselves, but the revision it returns is only usable by somebody who can apply it, and
keeping the pair behind one grant means a credential cannot be given half of a workflow.

### Two different revisions, because there are two different questions

`update_record_type` takes `expectedRevision`, the type's own revision from `get_record_type`, exactly
as `attach_field` does. Renaming cares only whether somebody else renamed it first.

`retire_record_type` and `merge_record_types` take `expectedUsageRevision` from their preview instead.
That is a fingerprint over the types **and** their records, field attachments and saved views: what a
retirement or a merge needs to know is whether what it is about to move is still what was counted. A
record added between the preview and the call invalidates it, and rightly — the numbers the caller
decided on are no longer true.

A stale one fails with `stale_revision`, not `validation_failed`. The distinction is load-bearing:
`validation_failed` invites a corrected retry, while a `stale_revision` request will fail identically
forever. Preview again and send the new fingerprint. The browser refuses the same case with the same
fingerprint and its own wording; both call the same code.

### What a merge preserves

The tools wrap the application commands the Structures page already uses, so a merge does exactly what
the page does rather than something similar:

- Records and saved views move to the target. A view keeps pointing at its records instead of at a
  retired type.
- Reminders survive, because a reminder names a field value, and the value's record changing type is
  not a reason to lose it.
- Import provenance survives. A field created by a contact import keeps its canonical key, so the next
  import recognises it instead of making a second one.
- Values of field types this build does not recognise keep their exact text. Nothing about a merge
  decides it now understands them.
- Fields the source has and the target lacks are appended **optional**. A field required on one side
  and not the other is relaxed, as is a target field required while the source has records. Records
  that were valid before the merge are still valid after it, which is why `requiredDowngradeCount`
  appears in the preview: it is the one number that describes a change to structure rather than a
  move.
- Merging a type into itself is refused at the preview, so there is no fingerprint to carry to an
  apply.

Retirement keeps everything. A retired type stops being offered for new work; its records, their
values and the views over them are untouched, and `query_records` still returns them.

Turning `tagsEnabled` off keeps the tags already recorded, so turning it back on restores exactly what
the type had. Omitting the argument leaves the setting alone rather than defaulting it off.

Each of the three writes is a receipt-bearing command with 24-hour identical-retry replay, like every
other write on this surface. A merge's receipt names the retired source first and the updated target
second, because the retirement is the half a caller is least likely to have expected.

## A reminder comes round with its date in 1.31

Contract 1.31 adds no tools and no grant. `list_reminders` reports one more field, `storedDate`, and
both reminder tools mean something different — because the behaviour they described was wrong.

1.28 documented this plainly rather than hiding it, and it is now fixed. `dueDate` counted back from the
day a value **stores**, so a reminder on a birthday recorded in 1990 fell due in 1990; dismissal was
permanent, so the reminder was useful exactly once, immediately, for an occurrence thirty-odd years
past. Reminders were therefore useless on precisely the dates people set them for.

- `entry.date` is now the **next occurrence** of the value under its field's recurrence, and `dueDate`
  counts back from that. Comparing `dueDate` with today gives a real answer.
- `storedDate` is the day the value actually names. Both travel together because they differ for
  anything that repeats and a reader given one cannot recover the other — `entry.yearsSince` narrows it
  but a rolled leap day breaks the arithmetic.
- `dismiss_reminder` deals with **one occurrence**. A repeating value comes round again and the
  reminder returns armed for its next; a one-off has only that occurrence, so dismissing it is
  permanent without needing a second rule. It is no longer marked destructive, because it no longer
  destroys anything.
- Dismissing the same occurrence twice still reports `not_found`, so a caller can tell a completed
  dismissal from a mistaken identifier.
- `create_reminder` still refuses a duplicate, and now refuses one even while the existing reminder is
  dismissed for the occurrence currently passing — it is still scheduled, for the next.

The projection is not new code. The calendar has shown repeats since contract 1.27 and
`list_upcoming_dates` has reported next occurrences since 1.29; reminders were the one surface that
disagreed, and they now use the same recurrence machinery. A reminder and a calendar entry for the same
value answer the same question the same way, which a test asserts directly.

Application migration 39 adds `Reminders.DismissedForDate` and translates each existing dismissal into a
dismissal of the stored-date occurrence, which is exactly what the old projection was showing. A
non-repeating date therefore stays silenced, while a repeating one re-arms — the right outcome, since
those were only silenced because they were permanently and wrongly due.

The migration also has to move uniqueness off dismissal. The index that stopped the same value and lead
time being scheduled twice applied only to undismissed rows, because a dismissal used to end a reminder
for good; now that a dismissed reminder comes back, two rows differing only in having been dismissed
would both re-arm and fire together. Any such pair is collapsed first — keeping the undismissed row, or
else the most recently dismissed — and the index is recreated without the condition.

## A new record type reaches the dashboard in 1.30

Contract 1.30 adds no tools and no grant. `get_dashboard_settings` reports one more bound,
`maximumCategories`, and both dashboard tools mean something slightly different.

`recordTypeIds` is now **what the dashboard shows** rather than only what was saved. A record type
created after the configuration was written appears in it, because a type nobody has taken off the
dashboard belongs on it. Previously the stored list was read literally, and since a type that did not
exist when it was written is absent from it exactly as a removed one is, every new type was treated as
unwanted and never appeared.

`set_dashboard_settings` records every other active type as taken off the dashboard. That is what makes
a choice stick: without it the next read would see an unlisted type as newly created and put it back.
The tool still merges, so a caller changing only `upcomingDays` keeps the categories it did not mention.

The list is bounded at `maximumCategories`, which is new and is not arbitrary. Each category costs a
search and a read per row it shows, so once types arrive on their own the page's work is a function of
how many types a deployment has rather than how many somebody chose. Saved categories keep their places;
the automatically-appended ones fill what is left.

A deployment that has never saved a configuration now gets every active type with people first, where
it used to get people alone — which meant an operator who never opened the settings page never saw
anything they created afterwards either.

Application migration 38 adds `DashboardDismissedCategories` and, for a deployment that had saved a
configuration, seeds it with every active type that was not chosen. So an existing curated dashboard
looks the same after the upgrade and only types created from then on arrive by themselves. A deployment
that never arranged its dashboard is seeded with nothing, because it has curated nothing.

## Map, spatial queries and the dashboard in 1.29

Contract 1.29 has 90 tools, adding six, and no new grant. It closes MCP milestone M5's tool surface.

### Enabling an external request has to be said out loud

`set_map_settings` is the only tool in this contract that changes what a viewer's browser contacts. The
settings page puts a privacy notice in front of the operator above the checkbox; a tool call carries no
page, so the acknowledgement takes its place. Enabling external tiles requires
`acknowledgeExternalRequests` true and is otherwise refused **with the disclosure quoted in the error**,
because a caller that has not acknowledged the notice has by definition not been shown it.

Three details follow from treating it as consent rather than as a flag:

- Disabling needs no acknowledgement. Turning a disclosure off discloses nothing, and requiring one
  would make the safer direction the harder one.
- Acknowledging while leaving tiles off is not a way to bank consent for later: the next enabling call
  must acknowledge again. The test pins that.
- `get_map_settings` returns the disclosure whether tiles are on or off, so a caller can tell an
  operator what a change would cost *before* asking for it rather than only after a refusal.

Enabling is per domain, and the deployment's content security policy names the tile host only while the
setting is on, so a domain with tiles off cannot reach the provider even if something tried.

### An approximate location stays approximate

`query_map` returns pins with `accuracyMetres` and `approximationRadiusKilometres` alongside the
coordinate. The radius is the operator's own statement that a location is only good to within that
distance -- "somewhere in Yorkshire" rather than a street address -- and a consumer handed only latitude
and longitude would plot a forty-kilometre claim as a point and report someone's home far more precisely
than was ever said. Carrying it is M5's geographic-approximation requirement.

Nothing in `query_map` contacts a tile provider. It returns coordinates; drawing them is the client's
business, and the external setting governs only what a browser fetches to draw *under* them.

### The dashboard looks forward

`list_upcoming_dates` reports each date at its **next** occurrence, which is what separates it from
both of its neighbours in this contract: `query_calendar` reports a range including the stored day, and
a reminder's `dueDate` counts back from the stored date. Comparing an upcoming date with today gives a
real answer, so this is the tool to reach for when the question is "what is coming up".

`set_dashboard_settings` **merges** onto what is stored, matching `set_graph_settings`: changing the
look-ahead does not clear the categories. That is the opposite of `update_saved_view`, deliberately -- a
view's lists *are* the view, while these are independent settings that happen to share a row. An
explicitly empty list still clears one, so an empty list is a decision and an omitted one is silence.

### Bounds

`get_capabilities` reports `mapLimits`: pages 1-10000, `pageSize` 1-500 defaulting to 100, and at most 20
location fields per query. Latitude bounds must be ordered south to north and within -90 to 90;
longitudes within -180 to 180. `get_dashboard_settings` returns its own bounds inline with the values --
the shipped default, the 366-day maximum look-ahead, 50 recurring fields and 100 projected items -- so a
caller can tell a chosen look-ahead from the default without knowing this build's numbers. A deployment
that has never saved a dashboard configuration gets one derived from its own structure rather than an
empty one.

### M5's tool surface is complete

Saved record views (1.25), graph views and bounded graph queries (1.26), calendar reads and iCalendar
export (1.27), reminders (1.28), and now spatial queries, map settings and the dashboard. What remains of
M5 is its exit evidence rather than its tools: the live-client and interactive-browser gates for this
contract revision.

## Reminders in 1.28

Contract 1.28 has 84 tools, adding three, and no new grant. It continues M5.

A reminder was the one thing on the calendar page an operator had to be present to set. The loop a
client walks is: `query_calendar` for a value's `fieldValueId`, `create_reminder` on it with a lead
time, `list_reminders` to see it, `dismiss_reminder` to be done with it. Nothing else exposes a
`fieldValueId`, which is deliberate -- a reminder has nothing else to attach to.

- `list_reminders` takes `records.read` and returns the undismissed reminders, each carrying the
  calendar entry it watches and its due date. The entry travels with the reminder because one
  identified by its own id says nothing about what it is for, and a caller would otherwise have to
  read the whole calendar back to find out.
- `create_reminder` and `dismiss_reminder` take `records.write`.

### Why not a reminders grant

One was considered and rejected. It would never be useful alone -- the `fieldValueId` can only come
from reading the calendar -- and it would confer strictly less than `records.write` already implies: a
credential that may rewrite the date itself is not escalated by being able to set a reminder about it.
A permission checkbox that adds no control is worse than no checkbox. Listing still takes
`records.read`, so a read-only credential can see reminders and cannot create them.

### Eligibility is the calendar's rule, not a second one

Only a day-precision, non-approximate value can carry a reminder. A birth recorded as "some time in
1815" has no day to count back from, and one somebody has guessed at would fire on a day nobody
claimed. The calendar excludes both, and so does this: a client cannot set a reminder on something the
calendar would never have shown it. That agreement is what the test pins, alongside a value id that
does not exist at all.

### No idempotency key, because a repeat cannot happen

The same value and lead time cannot be scheduled twice: a retry reports `validation_failed` saying it
is already scheduled rather than creating a second reminder. A different lead time on the same value
*is* a different reminder, since somebody may want a month's warning and a day's. The cost of this
shape is that a client which lost the response cannot tell its own successful retry from a conflict;
`list_reminders` answers that, and a receipt could be added later without changing the tool.

### A known limitation, stated rather than papered over — fixed in 1.31

`dueDate` was the **stored** date less the lead time. For a value that repeats every year that is a day
long past, so such a reminder read as due immediately, and dismissal was permanent rather than
per-occurrence. It was documented here rather than quietly shipped, recorded as a gap on the roadmap,
and fixed in 1.31 once the behaviour change could be made on both surfaces at once — correcting it for
MCP alone would have made the page and the tool disagree about the same reminder.

### What stays out

Spatial queries, the dashboard projection and configuration, and map settings remain M5 scope and are
not in this contract.

## Calendar reads and iCalendar export in 1.27

Contract 1.27 has 81 tools, adding two, and no new grant. It continues M5.

The calendar's whole point is that a date recorded decades ago still comes round, and its whole
discipline is that a date nobody is sure of does not. Both are properties of the application rather
than of the page, and both hold here.

- `query_calendar` returns the dated values falling in a range, each naming the record it belongs to
  and the field it came from. A stored date appears on the day it names and again on each repeat its
  field declares, so a birthday recorded in 1990 appears this year with `isRepeat` true and
  `yearsSince` saying how many whole years have passed. A client cannot work that out from the date
  alone and would be wrong every leap year if it tried. A 29 February moved to a year without one
  says `rolledFromLeapDay`, since the field rather than the application decides which way it rolls.
- `export_calendar` returns the same range as the iCalendar document the browser downloads.

### Only dates that are actually dates

Only day-precision, non-approximate values appear. A birth recorded as "some time in 1815" has no day
to put it on and inventing one would be a lie with a date on it; a day somebody has guessed at has a
day but not a reliable one. The store already excludes both, and the tests pin that the exclusion
survives the trip out — this is M5's date-precision requirement and it needed no new code, only
evidence.

### One grant, whatever the format

Both tools take `records.read` and nothing else. A calendar entry names a record and one of its
dates, which is record content in any format, so an export permission of its own would look like a
control and be none: the document says exactly what `query_calendar` already returns. This is the
opposite case to `contacts.export`, which exists precisely so a credential can export contacts
*without* holding a general read.

The document itself carries less than the query does: each event is a whole day with the record name
and field name as its summary and the record type as its category. No field value, tag, note or image
goes into it, because a calendar handed to another application should disclose the occasion rather
than the record.

### Paging, and proving it

The document is returned base64 in byte ranges, the same shape `export_contacts` and
`read_contact_import_evidence` use, so a client written against one works against the others:
`offset` defaults to 0, `count` to 16384 and is bounded there, `nextOffset` is the offset to ask for
next or null at the end, and the end offset yields an empty final range while anything past it fails.
`contentDigest` is the SHA-256 of the **whole** document in hex, so a client can prove its reassembly
lost and duplicated nothing. The test does exactly that, in 64-byte pages, and checks that a name
containing a comma and a semicolon survives iCalendar escaping intact.

**Pass `generatedAtUtc` from the first response back on every later page.** iCalendar stamps each event
with when the file was created, so a document generated a second later is a byte-for-byte different
document with a different digest — a defect this contract's own test caught, having passed by luck while
every call landed inside the same second. The stamp is part of the document rather than a label on the
response, which is why it is echoed and accepted rather than hidden: a paged export nobody can reproduce
is worth nothing. It is truncated to the second the format carries, so the value a client sends back is
exactly the one the document was built with. A digest that changes even with the stamp pinned means the
underlying records changed; start again at offset 0.

### Bounds

`get_capabilities` reports `calendarLimits`: a range of at most 367 days counting both ends — a year
and a day, so that "this date next year" is one call rather than two — `limit` 1-1000 defaulting to
500, at most 100 record types, and the 16384-byte export chunk. `limitReached` is stated rather than
left to be inferred from the count matching the limit, which is the one case where a complete answer
and a truncated one look identical. The widest accepted range is tested at the boundary itself rather
than comfortably inside it.

### What stays out

Spatial queries, reminders, the dashboard projection and configuration, and map settings remain M5
scope and are not in this contract.

## Graph views and bounded graph queries in 1.26

Contract 1.26 has 79 tools, adding six, and no new grant: the graph views sit under the
`views.manage` introduced in 1.25. It continues M5.

A graph view is the one saved thing in this application that cannot be reconstructed from its
inputs. Everything else a view stores is a rule -- a filter, a sort, a tag -- but a layout somebody
arranged by hand has no rule behind it, so a caller able to filter the graph and not read the
arrangement could never reopen the view that was saved. `get_graph_view` therefore returns the node
positions and viewport exactly as stored, fractions and negatives included.

Reads take `records.read` or `views.manage`:

- `list_graph_views` returns each view's display mode and the records and record types it draws, and
  omits the arrangement, which is the bulk of a view and of no use when listing rather than
  reopening.
- `get_graph_view` returns one view in full, or null when it is absent from the selected domain.

Drawing takes `records.read` alone:

- `query_graph` returns nodes and edges, each edge saying whether the relationship has ended.
  `graphViewId` draws a saved view's selection; alternatively `displayMode` with
  `selectedRecordIds` and `recordTypeIds` draws one composed in the call. Sending both is refused
  rather than letting one quietly win, because a caller that sent two selections has two different
  pictures in mind and no answer would be the one it expected.

Writes take `views.manage`:

- `create_graph_view` and `update_graph_view` store a view; update replaces rather than merges, so an
  arrangement left out is forgotten, which is the only way a caller can reset one. There is no
  `duplicate_graph_view`, because the application has no duplicate command for a graph view and
  inventing one for the remote surface alone would make the two disagree.
- `delete_graph_view` removes the arrangement. **Destructive** of the layout only: no record,
  relationship or record type is touched.

### Two defaults that were wrong until the tests said so

Both were found by the tests in this change rather than by review, and both had the same shape -- a
request that looked reasonable and came back with an empty graph.

- **Omitting `recordTypeIds` draws every active type.** The store distinguishes no filter from a
  filter matching nothing, and passing an empty list for an absent one meant the plainest possible
  call, `query_graph` with just a domain, drew nothing at all. An *explicitly* empty list is now
  refused and says what to do instead, because such a list is nearly always a selection that came
  out empty rather than a deliberate request for an empty picture.
- **`connected` and `isolated` need something to be relative to.** Without a `selectedRecordIds` or
  a `focusRecordId` the seed set is empty and the graph comes back empty. This is the rule
  `GraphViewService` already applies when saving a view, so it is applied to the query too rather
  than invented: the two surfaces now agree about the same request.

A saved view is reproduced verbatim, an empty record-type list included, because that is what the
operator chose and a view that draws nothing should not be silently widened into one that draws
everything.

### The operator's boundary, not this build's ceiling

`nodeLimit` and `edgeLimit` default to the domain's configured limits from `get_graph_settings` and
are **capped** by them. Asking for more does not get more; the way to draw more is to raise the
setting, which is a different grant. Without that cap the settings page would be advice rather than a
boundary. Asking for less is honoured, since that narrows. The result reports `appliedNodeLimit` and
`appliedEdgeLimit` alongside `nodesTruncated` and `edgesTruncated`, because a graph that stopped early
looks exactly like a complete one and a caller given only the nodes would conclude it had everything.

### Bounds

`get_capabilities` reports `graphViewLimits`: a 200-character name, at most 100 selected records, 100
record types, 2,000 node positions, coordinates within a million of the origin, zoom between 0.15 and
3, neighbour depth 0-3 and a 200-character search. Every record type must be active, and every
selected or positioned record must belong to one of the types the view lists, so a view cannot
remember a node it would never draw.

### What stays out

Spatial queries, calendar and upcoming-date queries with iCalendar export, reminders, the dashboard
projection and map settings remain M5 scope and are not in this contract.

## Saved views in 1.25

Contract 1.25 has 73 tools, adding seven, and one grant, `views.manage`. It begins MCP milestone
M5.

A saved view is a stored question: a record type, optional search text, up to ten AND-combined field
filters, up to ten universal tags every record must carry, a grouping, a sort, the fields to show as
columns, and whether to show a tags column. Until now it existed only in the browser, so a remote
caller wanting the same answer had to rebuild all of that and hope it had rebuilt it identically.

Reads take `records.read` or `views.manage`:

- `list_saved_views` returns one domain's views with their record type, search text, grouping and
  sort. It deliberately omits columns and filters, because assembling those for every view is a
  read per view for detail a caller listing them has not asked for.
- `get_saved_view` returns one view's full definition, or null when it is absent from the selected
  domain — the shape `get_record` and `get_record_type` already use.

Running a view takes `records.read` and nothing else:

- `run_saved_view` returns the matching records with the values of the fields the view lists as
  columns, plus its group-by field, and the record's universal tags when the view shows them. Rows
  are the same projection `get_record` returns, so a column and a value are the same thing seen
  twice rather than two shapes that agree today.

Writes take `views.manage` alone:

- `create_saved_view` stores a new view. Not idempotent and taking no `idempotencyKey`: a view has
  no natural identity beyond its name, and two views may legitimately share one, so calling twice
  creates two rather than resolving to the first.
- `update_saved_view` **replaces** the definition rather than merging into it. A list left out is
  stored empty, because a caller clearing a view's filters has no other way to say so. Read the
  view first and send back what you intend to keep.
- `duplicate_saved_view` copies a view under a new name, original untouched.
- `delete_saved_view` removes the definition. **Destructive**, but only of the question: no record,
  value or tag is touched. Deleting a view that is already gone reports `not_found`, so a caller can
  tell a completed delete from a mistaken identifier.

### Why its own grant

`views.manage` is separate from `structure.write`, which creates record types, creates and attaches
reusable fields and installs presets — changes that alter what every record in a domain can hold. A
saved view alters nothing about the records, so requiring the schema grant to tidy a list of views
would hand out far more authority than the task needs.

It is equally separate from reading. `run_saved_view` returns record content and therefore demands
`records.read`; a credential holding only `views.manage` can compose and store a question and cannot
read its answer. That separation is the one this contract is most careful about, because a grant that
looked like configuration and quietly read records would be the worst kind of mistake to make here.
Existing credentials never acquire `views.manage` automatically.

### No revision, and why that is not an omission

A saved view carries no revision and no `expectedRevision` input: the last write wins. Unlike a tag
or a record type, there is no application command behind a view and the browser's own editor has no
concurrency check either. Introducing a remote-only revision rule would make the two surfaces
disagree about the same operation, which the management plan explicitly rules out. The exposure is
small and bounded — two callers editing one view means one of them loses an edit to a stored
question, with no record content at stake.

### Bounds

`get_capabilities` reports `savedViewLimits`, so a client plans within the bounds rather than
discovering them by being refused: at most 25 columns, 10 filters, 10 tags, a 200-character name and
a 500-character query. A field named as a column, filter, grouping or sort must be attached to the
view's record type; one belonging to another type is refused rather than dropped, because a view
quietly missing the column somebody asked for is worse than one that failed to save.

`run_saved_view` bounds a page of rows carrying values at 50 rather than the 100 the plain record
query allows, because each such row costs a record read exactly as the browser's grid does. Pass
`includeValues` false for a page of up to 100 records without them.

### What stays out

Graph view lifecycle, saved coordinates, bounded graph and spatial queries, calendar and reminder
tools, the dashboard projection and map settings are all M5 scope and are not in this contract.

## Packaged relationships gain a description in 1.24

Contract 1.24 adds no tools and no grant.

`list_relationship_presets` returns a `description` on each entry, and the catalogue holds five
more of them: employed by, studied at, member of, parent of and founded. Both are additive — a
1.23 client ignores the field and sees the longer list as a longer list.

The catalogue revision is computed from the catalogue's contents, so it has changed. That is the
mechanism working as designed: a caller holding the old revision is refused by `install_preset`
until it reads the catalogue again, rather than installing against a list it has not seen.

Every packaged relationship reads in the present tense. A relationship that is over is said with
its expiry, added in 1.23, rather than with a second past-tense definition — there is no "lived at"
beside "lives at", because two types meaning one thing is two things for the graph to draw.

### Installing one

Installing a single relationship definition is a browser action, reached from the relationship
types page. It is **not** on this surface, and the nearest equivalent is `create_relationship_type`
with the preset's own labels, which produces the same working definition under `structure.write`.

The difference is bookkeeping rather than capability: a type created that way carries no preset key,
so the browser will still offer to install the packaged one beside it. A caller that wants the two
to agree should install from the browser. Worth revisiting if a remote caller needs to reproduce a
domain's structure exactly.

## Relationship editing and graph settings in 1.23

Contract 1.23 has 66 tools, adding three, and no new grant.

### A relationship can end without being deleted

A relationship that is over is not one that never happened. Somebody worked somewhere until last
year, and deleting that link would lose the fact that they worked there at all. The store has
recorded an ending two ways since migration 36 — a flag for "this is over" and a date that becomes
true on its own once it passes, the flag winning where both are set — but nothing on this surface
could read or write it.

- `get_record_relationships` now returns `expired` and `expiresAtUtc` on each link.
  `query_record_relationships` already returned them, having always projected the Core view.
- `create_relationship` accepts both, for a link already known to have ended when it is recorded.
- `update_relationship` is new: it changes one link's type, note and ending in place.

Both new inputs are optional and default to a link with no ending, so a 1.22 client calling
`create_relationship` behaves exactly as before, and one reading relationships ignores two fields.

### Why update rather than delete and recreate

Recreating gives a different relationship with a new ID, and anything already holding the old one
silently loses it. `update_relationship` keeps the ID, which is the whole point of having it.

It deliberately cannot move a link to different records. Relating a different pair is a different
relationship, and letting one ID come to mean something else would make every reference to it a
question about when it was read. That change is a `delete_relationship` and a `create_relationship`,
which is exactly as much ceremony as it deserves.

It requires `expectedRevision` for the link and `expectedTypeRevision` for the type being set, the
same pairing `create_relationship` already demands. Omitting `note` clears it, because the tool
writes the whole link rather than patching fields; `patch_record` is the surface where omission
means "leave alone", and the two should not be confused.

### Graph settings

How much of the graph a domain draws stopped being a constant when the limits became configurable,
so a caller has no way to know this deployment's numbers without asking. `get_graph_settings`
returns the current values along with the bounds a limit must lie within and the shipped defaults,
so a raised limit can be told from the measured one without the caller knowing this build.
`set_graph_settings` merges: an omitted value is left as it stands, so raising the node limit does
not reset the other three.

A limit outside the bounds is refused rather than clamped. The value came from somebody who can be
told, which is the same reason the browser's settings page refuses it; storage clamps instead,
because a row written by an older build must still leave the graph able to draw something.

Reading takes `records.read` or `structure.write`; writing takes `structure.write`, the grant that
already covers changing how a domain is set up. No new grant was added, because a credential that
can reshape a domain's record types is not meaningfully restrained by being kept from its graph
bounds.

### What stays out

Whether the graph saves a view automatically is a per-browser preference held in that browser's
own storage, scoped to a domain. There is no server-side state to read or write, so there is
nothing for a tool to do. Bulk tagging and bulk relationship assignment are browser interactions
over a canvas selection rather than new capabilities: a remote caller reaches the same ends with
`patch_record` and `create_relationship` per record, under the grants those already require.

## Tag management in 1.22

Contract 1.22 has 63 tools, adding seven, and one grant, `tags.manage`.

Universal tags are catalogued for the whole deployment rather than per domain: the same label is
the same tag everywhere, which is what lets an administrator curate them in one place and lets a
label typed in a second domain join the existing tag instead of creating a duplicate.

Reads take `records.read` or `tags.manage`:

- `list_tags` returns every tag with `colour`, `icon`, `domainIds` and `revision`. An optional
  `domainId` narrows it to the tags offered there.
- `count_tag_usage` returns the record count per domain for one tag.

Writes take `tags.manage` alone:

- `create_tag` takes a `domainId` and a `name`. It is naturally idempotent: a name already in the
  catalogue resolves to that tag and gains the domain, so the returned `id` may be one that
  already existed, and the stored spelling replaces the one supplied.
- `set_tag_appearance` sets `colour` (`#rrggbb`) and `icon` (one or two characters, an emoji
  counting as one).
- `rename_tag` renames the tag everywhere it is used.
- `set_tag_domains` replaces the membership list. **Destructive**: a domain dropped from the list
  loses the tag from every record in it.
- `delete_tag` removes the tag from the deployment and from every record holding it.
  **Destructive.**

### Why its own grant

`tags.manage` is separate from `structure.write` for the reason `domains.manage` is: it writes to
the deployment registry rather than to one domain, and `set_tag_domains` and `delete_tag` delete
record content — in every domain that holds the tag, including domains the credential never
selected. `structure.write` authorizes no record deletion today, and folding these into it would
widen that grant without saying so. Existing credentials never acquire `tags.manage`
automatically; an administrator selects it when rotating one.

### Revision checks rather than receipts

Unlike the record and domain write tools, the tag tools take no `idempotencyKey` and issue no
durable receipt. That is deliberate rather than an omission. Receipts exist because record
creation is not naturally idempotent — issuing it twice makes two records. The tag operations do
not have that shape: `create_tag` is idempotent by construction, and every mutation is guarded by
`expectedRevision` from `list_tags`, so a retry after a successful call fails closed with
`stale_revision` rather than applying twice. A repeated `delete_tag` reports `not_found`.

The cost is that a client cannot distinguish "my retry already succeeded" from "someone else
changed this"; both surface as a refusal. If that distinction is needed later, receipts can be
added without changing the tool shapes.

### One correction carried by this contract

`ConcurrencyConflictException` previously escaped the shared read-tool error mapping and was
reported as `temporarily_unavailable` with advice to retry. A stale revision is not transient and
the retry would fail identically, so it now maps to `stale_revision`. Note that the deployment
still uses two codes for this condition — `stale_revision` in record and tag writes,
`concurrency_conflict` in the contact-preview and image tools — which predates this contract.

### Record reads are unchanged

`get_record` still returns `tags` as an array of names. Colour and icon are catalogue properties
rather than record content, so a client that wants them calls `list_tags`; this keeps 1.21 clients
working unchanged.