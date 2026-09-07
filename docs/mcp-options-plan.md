# MCP tools and options plan

- Status: Plan ready for incremental delivery; discovery and credential selection implemented locally; basic record writes locally tested; transfers planned
- Created and last reviewed: 2026-09-07
- Owner: Wixely / Agent
- Next review: 2026-09-14
- Delivery dependencies and exit criteria: [implementation plan](mcp-management-plan.md)
- Standing feature-coverage requirement: [roadmap](roadmap.md)

## Purpose

Define the MCP operations a client can discover and invoke, the options an administrator can grant, and the settings that bound those operations. This document owns the option catalogue; the implementation plan owns milestone dependencies and exit criteria. Proposed names below are provisional until their contracts and client transport are verified. Existing read tools keep their names and compatible inputs.

## Delivery priorities

1. **Basic management:** finish capability discovery and permission handling, then validate/create/patch/delete records, manage relationships, and create domains and schemas. Complete authorization, concurrency and retry tests before treating any write tool as ready.
2. **Contact files and media:** upload a vCard, inspect its preview and duplicates, explicitly apply decisions, and export selected contacts. Reuse the bounded transfer mechanism for record images. Contact-file upload is a required deliverable, not an optional extension.
3. **Complete application workflows:** schema lifecycle, saved views, graph/map/calendar queries, reminders and application settings.
4. **Instance operations:** deployment-wide backups and separately authorized runtime administration, followed by client and release verification.

The browser's existing application services remain the source of validation and business rules. Basic new features should include MCP support at delivery; every feature must record Included, Deferred, or Not applicable under the roadmap requirement.

## Implementation status boundary

The local working tree contains fifteen read/discovery tools and registered `validate_record`, `create_record`, `patch_record`, `preview_record_batch`, `get_record_batch_preview`, and `apply_record_batch` adapters, with a selectable `records.write` grant. Real MCP integration tests verify these write adapters, failure audit, permissions, retries, revision checks and isolation. This is local evidence, not deployment verification. Version 1.4 also provides deletion preview/inspection/apply/status under the separate records.delete grant. No contact-file upload tools are implemented. Deployed capability discovery must be checked separately before describing what a connected client can actually do.

## Administrator access options

Provide editable scope presets in Remote access settings. Presets are conveniences for selecting explicit grants, not new user roles or cumulative privilege levels. Existing credentials retain their exact grants; selecting a preset never changes another credential. [Pinned dependency inspection](mcp-dependency-findings.md) establishes one effective credential per API/MCP surface. Presets therefore configure the active surface credential; simultaneous independent client credentials require separate dependency work.

| Proposed preset | Intended use | Grants and exclusions | Delivery |
| --- | --- | --- | --- |
| Reader | Search and inspect application data | Existing reads plus selected view/calendar/settings reads; no writes, original-file downloads, or backups | M0-M1; later reads added only by explicit selection |
| Record editor | Maintain records and links | Selected reader grants plus record and relationship writes; no structure or remote administration | M2 |
| Contact transfer | Import/export contacts | Contact preview/apply/export and prerequisite schema discovery; no unrelated editing tools | M3 |
| Media editor | Manage record images | Selected record reads and media reads/writes; original download is an explicit grant | M3 |
| Application manager | Set up and configure spheres | Explicitly selected domain, structure, view, reminder, and settings grants; no automatic backup or remote-admin access | M2-M5 |
| Backup operator | Create and retrieve backups | Backup status/list/create/validate/download; no record writes or credential management | M6 |
| Remote administrator | Manage remote surfaces | Explicit remote-administration grant within deployment gates; no implied access to all record contents | M6 |
| Custom | Narrow integrations | Individually selected supported scopes and clear prerequisite descriptions | M1 onward |

The settings page should display the effective surface state, granted actions, disabled actions and their reasons, and the connection details needed by a client. Credential creation/rotation reveals the secret once. Updating grants uses the supported credential lifecycle and clearly identifies any reconnection requirement. Changing a preset definition must not silently upgrade existing grants.

Retain the current single-administrator model: `domainId` selects an isolated data store, not a per-client authorization grant. Per-credential domain allowlists are deferred unless separately designed; the UI must not imply such restrictions exist. Owner: Wixely / Agent; review: 2026-10-01.

## Tool coverage matrix

All rows are planned Included coverage unless marked otherwise. Scope families refer to the implementation plan; names such as `media.original.read` below are additional proposals. Domain means explicit `domainId` on new mutations; deployment operations have no domain selector. Lists are representative tool names, with each named action a separate bounded tool rather than an arbitrary command dispatcher.

| Workflow | Proposed tools/options | Permission family | Boundary / milestone |
| --- | --- | --- | --- |
| Discovery | `get_instance_info`, `get_capabilities`; expose contract version, effective scopes, limits and feature availability | `instance.read`; preserve existing read discovery | Deployment / M0 |
| Domains | Existing `list_domains`; `create_domain`, `rename_domain` | Existing `records.read`; `domains.manage` | Deployment; rename targets a domain ID / M2 |
| Setup and presets | `get_setup_status`, `list_presets`, `list_installed_presets`, `install_preset`, `complete_setup`; selected preset keys and blank setup | `structure.read/write` | Domain / M2 |
| Typed schema discovery | Existing type reads; `list_field_definitions`, `get_field_type_schema`; configuration, required rules, lifecycle and typed input shapes | Existing `records.read`; `structure.read` | Domain or static type catalog / M0-M2 |
| Record editing | `validate_record`, `create_record`, `patch_record`, `preview_record_deletion`, `delete_record`; explicit field changes and revisions | `records.read/write`; separate `records.delete` for deletion | Domain / M2 |
| Search | Extend `search_records` compatibly or add `query_records` for typed filters/sort; expose deterministic paging | `records.read` | Domain / M2 |
| Bulk records | `preview_record_batch`, `apply_record_batch`; explicit IDs, bounded one-domain transaction | `records.write` | Domain / M2 |
| Relationships | Existing record relationships; `list_relationship_types`, `create_relationship_type`, `create_relationship`, `delete_relationship`; directional endpoints and note | `relationships.read/write`; type creation uses `structure.write` | Domain / M2 |
| Upload lifecycle | `begin_upload`, `get_upload_status`, `complete_upload`, `cancel_upload`; purpose, length, media type and checksum | Permission for intended contact/media operation | Domain / M3 |
| Contact import | `preview_contact_import`, `get_contact_import_preview`, `apply_contact_import`; upload ID, paged contacts and explicit per-contact decisions | `contacts.import` plus schema discovery | Domain / M3 |
| Contact export | `export_contacts`; explicit Person IDs; return download descriptor | `contacts.export` | Domain / M3 |
| Images | `list_record_images`, `add_record_image`, `get_record_image_download`, `update_record_image`, `reorder_record_images`, `delete_record_image`; preview/thumbnail/original, caption, cover and correction | `media.read/write`; proposed `media.original.read` for originals | Domain / M3 |
| Record-type lifecycle | `create_record_type`, `update_record_type`, `preview_record_type_retirement`, `retire_record_type`, `preview_record_type_merge`, `merge_record_types` | `structure.write` | Domain / M2 creation, M4 lifecycle |
| Field lifecycle | `create_and_attach_field`, `attach_field`, `rename_field`, `retire_field`, `get_field_usage`, `preview_field_merge`, `merge_fields`, `preview_field_conversion`, `convert_field` | `structure.read/write` | Domain / M2 creation, M4 lifecycle |
| Record views | `list_record_views`, `get_record_view`, `create_record_view`, `update_record_view`, `duplicate_record_view`, `delete_record_view`, `query_record_view` | `views.read/write`; execution also requires `records.read` | Domain / M5 |
| Graph views | `list_graph_views`, `get_graph_view`, `save_graph_view`, `delete_graph_view`; mode, selections, coordinates and viewport | `views.read/write` | Domain / M5 |
| Graph and map | `query_relationship_graph`, `query_spatial_records`; bounds, layers, types, focus records and truncation | `records.read`, relationship reads where needed | Domain / M5 |
| Dates | `query_calendar`, `get_upcoming_dates`, `export_calendar`; date range, timezone where applicable, type/field filters | `calendar.read` | Domain / M5 |
| Reminders | `list_reminders`, `create_reminder`, `dismiss_reminder`; logical field reference and lead time | `reminders.read/write` | Domain / M5 |
| Dashboard | `get_dashboard`, `get_dashboard_settings`, `update_dashboard_settings`; ordered categories and recurring-date selections | `records.read` for projection; `settings.read/write` | Domain / M5 |
| Graph/map settings | `get_graph_settings`, `update_graph_settings`, `get_map_settings`, `update_map_settings`; bounded current application settings | `settings.read/write` | Domain / M5 |
| Backups | `list_backups`, `create_backup`, `validate_backup`, `get_backup_download`, `get_backup_schedule_status` | `backups.read/create/download` | Deployment-wide / M6 |
| Runtime administration | `get_remote_access_status`, `list_remote_audit`, `set_remote_surface_activation`, `rotate_remote_credential`, `revoke_remote_credential`, `rotate_remote_endpoint` | `remote.admin` | Deployment-wide / M6 |
| Long work | `get_operation`, `cancel_operation` for cancellable phases only; terminal result or committed outcome | Original operation grant; credential-bound ID | Domain or deployment, matching original action / M1, M3, M6 |

An operation-specific grant authorizes only the workflow described: contact import may create/merge/replace selected contacts, but does not implicitly authorize general record-edit tools. Require all documented grants for composed queries. Review grants again when serving staged downloads or completing an operation.

Relationship editing/retirement beyond current Core support is Deferred pending an application lifecycle design. Domain deletion/transfer, preset upgrades, runtime backup-schedule editing, and manual backup pruning remain Deferred to their own designs. Owner: TBD; review: 2026-10-01. Browser gestures and theme preference are Not applicable as direct tools; saved graph state and application settings remain Included. Offline restore, host changes, and debug reset are Not applicable to the production MCP endpoint for the reasons in the implementation plan.

## Request and result options

- New domain writes require `domainId`, and retryable writes require `idempotencyKey`. Updates/deletes require `expectedRevision`; a previewed action supplies an opaque `previewId` bound to its request and source revision. Creates return IDs and revisions. No mutable global selected-domain tool is introduced.
- Patch requests use named operations such as set primary name, replace aliases, replace one field's values, and clear one field. Omission means unchanged. Empty alias/value arrays mean clear that collection, subject to validation. Reject ambiguous null values; nullable members must explicitly document whether null clears them. Unmentioned fields survive.
- Typed values use scalar, tags, temporal, or location shapes advertised by schema discovery. Return value ordering, temporal precision/approximation and notes, and location accuracy/radius. Unknown types retain a lossless value; schema inspection precedes validation and saving.
- Import actions are `create`, `skip`, `merge_non_conflicting`, and `replace_mapped_values`; merge/replace require the chosen destination ID. Apply requires a decision for every preview contact, including skipped rows; do not infer approval from paging through a preview.
- Reads use bounded pagination and deterministic ordering with stable ID tie-breakers. Existing tools retain response compatibility; use an additive field or a new tool when a paged shape would otherwise break existing callers. No hidden truncation.
- Results include authoritative data, revision where relevant, correlation ID, and explicit warnings/truncation. Long work returns an operation ID and state: queued, running, succeeded, failed, or cancelled. If commit already happened, cancellation returns the committed outcome; disconnect does not imply rollback.

## File-transfer choices

| Option | Benefits / limits | Proposed decision |
| --- | --- | --- |
| Bounded inline UTF-8 contact text | Simple for small vCards; tool payload and logging limits make it unsuitable for images/backups | Optional convenience only after transport limits are measured |
| Authenticated streaming HTTP transfer plus MCP handles | Efficient for binary files and backups; client must support authenticated byte transfer independently of browser cookies | Later optimization after an authenticated client path is demonstrated |
| MCP chunk tools with offsets and checksums | Usable when the client can only invoke tools; encoding adds overhead and requires strict chunk/retry/expiry handling | Selected initial path; 256 KiB local transport probe passed |

The [write/transfer contract](mcp-write-contract.md) selects bounded chunks based on the passing local DnaX/SDK transport probe. Deployed MCPHub compatibility remains a live verification gate. Supporting both streaming and chunks is not automatically required. No production file-transfer implementation is claimed yet.

Bind each upload/download handle to credential, purpose and domain (or deployment for backups). Verify declared size/checksum, reject unsupported bytes, clean up expired and abandoned data, and enforce permissions after revocation. Never accept an arbitrary server path or remote URL. Credentials stay out of download URLs. Backups must transfer incrementally rather than loading the archive into tool JSON or application memory as one buffer.

Retain existing ceilings: vCard 5 MiB/1,000 contacts, selected export 100 records, images 10 MiB/24 megapixels/50 per record. Declare separate transport and decoded-content limits to account for encoding overhead. Backup quotas and free-space behavior require explicit testing because existing backup packages have no fixed application byte cap.

## Configuration and operational options

Proposed new settings need names, validation, persistence and precedence finalized in M0. Prefer a small set of operator-controlled resource limits; do not expose every internal implementation constant as an administrator option.

| Setting category | Proposed behavior | Verification |
| --- | --- | --- |
| Remote enablement | Preserve deployment master/surface gates and current explicit activation | Disabled deployment always denies calls |
| Permission selection | Explicit scope checklist with presets and descriptions of reads, writes, exports and administration | Old credentials keep old grants; scope denial enforced server-side |
| Work bounds | Enforce server batch, operation-concurrency and transfer limits; publish effective bounds | Boundary requests, oversized payloads and concurrent jobs |
| Temporary data | Bounded upload/preview lifetime and storage quota; retry retention covers documented retry window | Expiry, restart, disk-full and cleanup tests |
| Audit retention | Reuse DnaX controls where available; redact operation content and credentials | Inspect error and success audit output |
| Remote administration | Explicit grant within operator policy; credential/endpoint changes return reconnection guidance | Rotation, self-revocation and browser/operator recovery |

Effective capabilities should distinguish unsupported functionality, deployment-disabled functionality, and missing permissions without revealing secrets. New feature availability does not change credential grants or automatically activate remote access.

## Delivery slices and acceptance scenarios

| Slice | Work / dependency | Acceptance scenario | Owner / review |
| --- | --- | --- | --- |
| A: settle options | M0; inspect DnaX, finalize scope table and file prototype | Enumerate tools/limits, discover domains, explain denied capabilities; document source/deployment mismatch | Agent / 2026-09-14 |
| B: basic editor | M1-M2 foundations and core tools | Set up a new sphere, create a typed schema and two records, patch values, link records, reject stale/cross-domain writes and replay safely | Agent / 2026-09-21 |
| C: contact and media transfer | M3 after B | Upload fictional vCard, inspect duplicates, apply decisions, export selected people, add/correct/download an image; retry and cleanup checks | Agent / 2026-09-21 |
| D: application management | M4-M5 after B; exports reuse C | Merge/convert fields, reopen saved views/layout, query map/calendar, manage reminders and settings | Agent / 2026-09-28 |
| E: operations | M6 after foundations and C | Create/validate/download a deployment backup; test separately authorized credential rotation and recovery | Wixely / Agent / 2026-10-01 |
| F: release coverage | M7 after C-E | Full repository checks and disposable-instance client scenarios; every workflow has a coverage disposition | Wixely / Agent / 2026-10-01 |

Dates are review checkpoints. Each implementation PR adds actual schemas/examples, required scopes, limits and failure tests to the tool reference. Verify shared service semantics in Core/Data and real discovery/invocation through Web integration tests. Record client/platform checks as passed only after execution. Apply the roadmap's Included/Deferred/Not applicable check to every later feature.

Remaining work: finalize preview/upload quota bounds and live transfer verification, then finish B-F. Recommended next action: Agent adds relationship and setup management, then completes structured search before contact transfer. Version 1.4 documents locally tested single/batch record writes, reviewed deletion, bounded previews, durable media cleanup and advertised limits. No additional integration installation or live configuration change is authorized by this plan.
