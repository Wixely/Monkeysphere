# Roadmap

- Last reviewed: 2026-09-07
- Owner: Wixely / Agent unless otherwise noted
- Current release: `0.1.0-alpha.2` prerelease; active branches `main` and `feature/domains`

## How to read this roadmap

This roadmap records intended direction and honest status. It is not a delivery commitment.

- **Status vocabulary.** *Complete* means implemented and covered by recorded evidence. *In progress* means partially delivered with named remaining gates. *Planned* means accepted and specified enough to start. *Later* means accepted in principle but needing design or research first. *Deliberately excluded* means a decision was taken not to build it, with a reason.
- **Dates are review checkpoints**, not delivery dates. A passed checkpoint means the item is re-read and its status corrected, not that work shipped.
- **Owners.** `Agent` covers implementation work. `Wixely` covers approval, release, and anything requiring credentials, host changes, or the separate DnaX repository. `TBD` means the item is not yet assigned and must not be assumed to be moving.
- **Evidence rule.** Local test-host evidence never establishes deployed availability. [Verification status](verification.md) is the authoritative record of what has actually been observed, including its *Not yet verified* list; this roadmap must not contradict it.
- **MCP disposition.** Every item below carries an MCP disposition under the policy in the next section.

## Delivery track

The product is pre-alpha in maturity even though alpha packages are published. These are the intended gates between phases.

| Phase | Theme | Exit criteria | Review |
| --- | --- | --- | --- |
| Alpha (`0.1.x`) | Prove the record/relationship model and the self-hosted deployment shape | MCP management milestones M0-M3 closed. Every deployment shape now has live lifecycle evidence except upgrade. The replacement prerelease clause is met by `v0.1.0-alpha.3` | 2026-10-01 |
| Beta (`0.2.x`) | Complete the management surfaces and remove documented residual risks | MCP milestones M4-M7 closed; complete script/style CSP enforced; accessibility conformance verification completed; preset upgrade workflow shipped; DnaX consumed as a stable version | 2026-12-01 |
| 1.0 | Operational confidence for a self-hosting user who is not the author | Interaction timeline shipped; upgrade path across at least two prior minor versions verified; documented backup/restore drill repeated on a clean host; no open High residual risk in the [threat model](threat-model.md) | 2027-02-01 |

Phase membership is a planning aid. An item may be pulled forward or dropped without renaming the phase.

## Priority overview

| Item | Section | Status | Owner | Review |
| --- | --- | --- | --- | --- |
| Publish a replacement prerelease; both alphas are unupgradable | [Migration ledger compatibility](#migration-ledger-compatibility) | Complete; `v0.1.0-alpha.3` published and its artifacts verified | Wixely | 2026-09-14 |
| MCP record image transfer (contact export done) | [MCP instance management](#mcp-instance-management) | In progress (M3) | Agent | 2026-09-21 |
| Standard MCP clients cannot connect; DnaX headers required | [MCP client interoperability](#mcp-client-interoperability) | Open defect | Agent | 2026-09-14 |
| Live MCP client and interactive browser gates | [MCP instance management](#mcp-instance-management) | Complete for contract 1.15 against the published package | Agent | 2026-09-14 |
| Upgrade across a schema-changing release | [Upgrade path verification](#upgrade-path-verification) | Mechanism verified alpha.3 to alpha.4 on both platforms; a schema-changing upgrade is still untested | TBD | 2026-12-01 |
| Format 1 backup compatibility fixture | [Backup and restore follow-up](#backup-and-restore-follow-up) | Complete | Agent | 2026-09-28 |
| Clean DnaX package release without local build paths | [Release follow-up](#release-follow-up) | Planned | Wixely / Agent | 2026-09-28 |
| Complete script/style CSP | [Content Security Policy completion](#content-security-policy-completion) | Planned | Agent | 2026-10-01 |
| Preset upgrade workflow | [Preset upgrade workflow](#preset-upgrade-workflow) | Planned | Agent | 2026-10-01 |
| Accessibility conformance verification | [Accessibility conformance](#accessibility-conformance) | Planned | TBD | 2026-10-01 |
| DnaX stable dependency | [Dependency and supply-chain maintenance](#dependency-and-supply-chain-maintenance) | Later | Wixely | 2026-11-01 |
| Interaction timeline | [Interaction timeline](#interaction-timeline) | Later; design required | TBD | 2026-11-01 |
| Android contact importer | [Android contact importer](#android-contact-importer) | Later; blocked on M3 | TBD | 2026-10-01 |
| Configurable map tile provider | [Map tile provider configuration](#map-tile-provider-configuration) | Later | TBD | 2026-11-01 |
| Domain deletion/archive and record transfer | [Domain-separated spheres](#domain-separated-spheres) | Later; design required | TBD | 2026-10-01 |
| Upgrade-path verification across versions | [Upgrade path verification](#upgrade-path-verification) | Later | TBD | 2026-12-01 |

## MCP coverage for new features

Status: Adopted planning and completion requirement from 2026-09-07.

Basic application functionality should be usable through MCP as well as the browser. Every new feature or material extension must consider MCP during design, including its reads, mutations, configuration, and import/export workflows. Deliver applicable MCP support with the feature by default, using the same application services and validation.

- Record an MCP disposition in the feature plan and PR: **Included**, **Deferred**, or **Not applicable**.
- Included work specifies tools, permissions, domain selection, input/output limits, and relevant verification, and ships with the feature.
- Deferred work requires a concrete reason, a linked roadmap item, an owner (or TBD), and an ISO review date. Deferral must remain visible as incomplete coverage.
- Not applicable work requires a reason, such as a purely visual browser interaction or an operation that must run while the application is offline. Consider exposing its underlying data or configuration even when the interaction itself is browser-only.
- Review existing tools when extending a feature: new fields, filters, settings, and lifecycle actions must not silently leave the MCP contract behind.
- This requirement does not automatically enable remote access, expand existing credentials, or require a matching HTTP API endpoint for every tool.

Owner: Wixely / Agent. Next action: apply this checklist to every new feature plan and PR; review coverage on 2026-10-01.

## Evidence and support claims

Status: Adopted practice; recorded here so it survives contributor turnover.

A capability is supported only when it has been observed working in the environment being claimed. Local test-host passes, compiled-but-unrun code, static unit checks, and green CI legs are all evidence of construction rather than of platform support.

- Record every verification run in [verification status](verification.md) with its date, exact command, counts, and the gates that remain open. Supersede earlier entries explicitly instead of deleting them.
- Keep the *Not yet verified* list current. Items there must appear in this roadmap with an owner and a review date, or be withdrawn as a support claim from the README and release notes.
- Do not convert an unexecuted workflow, a `systemd-analyze verify` pass, or a successful build into a lifecycle or platform claim.
- Vulnerability audits are time-sensitive. Re-run `eng\VerifySupplyChain.ps1 -AuditVulnerabilities` before each release candidate rather than citing an old result.

MCP disposition: **Not applicable.** This is a documentation and process rule with no runtime surface.

Owner: Wixely / Agent. Next action: reconcile the *Not yet verified* list against this roadmap at every checkpoint; review 2026-10-01.

## MCP instance management

Status: M0 in progress. Baseline reviewed: 2026-09-07.

Build out authenticated MCP management of application data and runtime administration, preserving domain isolation and the offline restore boundary. The [MCP implementation plan](mcp-management-plan.md) defines contracts, dependencies, delivery phases, acceptance criteria, and remaining decisions.

The [MCP options plan](mcp-options-plan.md) expands this into tool families, access profiles, settings, file-transfer choices, and client workflows. Pinned dependency inspection and a local chunk-transfer probe are documented; deployed-client verification and final contracts remain open. Local implementation status does not establish deployed availability.

| Milestone | Scope | Status | Owner | Review date |
| --- | --- | --- | --- | --- |
| M0 | Source/deployment inventory, capability discovery, contract and permission design | In progress; local discovery/schema tools and chunk probe pass | Agent | 2026-09-14 |
| M1 | Write authorization, concurrency, retry protection, previews, and audit foundations | In progress; revisions/receipts, failure audit, batch/deletion previews and media cleanup tested; other workflows pending | Agent | 2026-09-14 |
| M2 | Records, relationships, domain setup, and minimum structure creation | Implemented locally; full two-domain MCP setup, typed editing, relationships, structured search and reviewed deletion pass; live client/browser gates remain | Agent | 2026-09-21 |
| M3 | File transfer, vCard preview/apply/export, and record images | In progress; MCP upload/validation/preview/apply and bounded selected-contact export verified locally; record images remain | Agent | 2026-09-21 |
| M4 | Complete field and record-type lifecycle management | Planned; depends on M2 | Agent | 2026-09-28 |
| M5 | Saved views, graph/map queries, calendar, reminders, and settings | Planned; depends on M2 | Agent | 2026-09-28 |
| M6 | Backup operations, operational status, and separately scoped remote administration | Planned; depends on M1 and M3 | Agent | 2026-10-01 |
| M7 | Coverage review, end-to-end verification, documentation, and staged release | Planned; depends on M3-M6 | Wixely / Agent | 2026-10-01 |

Dates are review checkpoints, not delivery commitments. Recommended next action: Agent implements record image transfer to complete M3 while closing the remaining M0-M1 gates. Bounded selected-contact export shipped in contract 1.15 under a separate `contacts.export` grant.

### Cross-cutting gates

These apply to every milestone above and are the most common reason a milestone stays open after its local tests pass.

- **Live client verification.** Done for contract 1.15 on 2026-09-08 against the published `v0.1.0-alpha.3` win-x64 package over its randomized endpoint: 52 tools, a full create-then-export round trip with digest verification, and live grant separation. It found two defects the test host could not, recorded under [MCP client interoperability](#mcp-client-interoperability). Repeat for each future contract revision. Owner: Agent; review 2026-09-14.
- **Interactive browser verification.** Done on 2026-09-08 against the same published package: login, the setup wizard and its transactional install, Settings, and the Remote access page through permission selection, credential rotation, activation and the redacted audit table. Owner: Agent; review 2026-09-14.
- **Permission user interface.** Verified interactively on 2026-09-08: every defined grant renders, and selecting four of them produced a credential with exactly those scopes. Owner: Agent; review 2026-09-21.
- **Contract documentation.** [The MCP contract](mcp-contract.md) must be regenerated or corrected in the same change that alters tool count, inputs, limits, or grants.

## Android contact importer

Status: Planned; depends on the M3 contact-import contract and deployed-client verification.

Build an Android application that lets the user select contacts from the device address book, choose a Monkeysphere domain, review the proposed create/skip/merge/replace decisions, and import them through Monkeysphere's authenticated MCP contact workflow. The app must request Android contact access only when the user starts selection, keep the chosen data on-device until upload, show transfer and per-contact outcomes, and support safe retry without duplicate records.

MCP disposition: **Included**. Reuse capability discovery plus the existing `contacts.import` upload, validation, preview, inspection, and apply tools. Do not add a separate privileged mobile ingestion path. Before implementation, verify the target Android MCP client transport, TLS trust and credential-storage model against a disposable Monkeysphere instance. Contact export to the device is outside this initial item and requires a separate privacy and conflict-resolution design.

Owner: TBD. Next action: Agent completes M3 contact export and deployed MCP transfer verification; then owner TBD writes the Android client architecture and permission-flow plan. Review: 2026-10-01.

## Domain-separated spheres

Status: Complete and merged to `main`.

Domains let one deployment hold independent spheres such as Personal friends, Online friends, or TV show characters. They are an organizational and data-isolation boundary for the single administrator, not a multi-user authorization system.

### Invariants

- The existing `monkeysphere.db` and `media/records` data remain in a stable, built-in Default domain. Its display name can be changed without moving data.
- Every additional domain has a separate, fully migrated SQLite database and media root under an opaque UUID directory. Record IDs cannot be resolved, related, searched, exported, graphed, mapped, or rendered through another domain.
- Record types, fields, presets, setup state, records, aliases, images, relationships, saved views, dashboard configuration, calendar data, reminders, imports, and map settings belong to exactly one domain.
- The selected domain is held in a protected, HTTP-only, SameSite cookie. Invalid or obsolete browser selections return to Default. Explicit invalid remote selectors fail closed.
- New domains start with their own first-run setup wizard. Duplicate structure and record names are valid in different domains.
- API callers select a non-default domain with `X-Monkeysphere-Domain`; MCP tools accept an optional `domainId`. Omission retains backwards-compatible Default behavior.
- Backups are deliberately deployment-wide: the domain registry, every domain database and original-media tree, and remote-access state are validated and restored as one unit.
- Cross-domain links are not supported. A future transfer/copy workflow must use an explicit preview and create independent destination records rather than weakening isolation.

### Delivery plan

| Work item | Status | Owner | Review date |
| --- | --- | --- | --- |
| Domain registry, renameable Default domain, and isolated database/media paths | Complete | Agent | 2026-09-03 |
| Header switcher and Settings / Domains management | Complete | Agent | 2026-09-03 |
| Per-domain setup, structures, records, relationships, views, dashboard, calendar, reminders, vCard, map, graph, and media | Complete | Agent | 2026-09-03 |
| Explicit API header and MCP domain selection | Complete | Agent | 2026-09-03 |
| Deployment-wide backup format 2 with format 1 compatibility and atomic restore | Complete | Agent | 2026-09-03 |
| Isolation, cookie, remote-surface, backup/restore, and browser regression tests | Complete | Agent | 2026-09-03 |
| Review and merge `feature/domains` into `main` | Complete | Wixely | 2026-09-14 |
| Domain deletion/archive and previewed record transfer/copy | Later; design required | TBD | 2026-10-01 |
| Per-domain visual identity and optional structure-template duplication | Later; user research | TBD | 2026-10-01 |

Domain deletion and record transfer both weaken assumptions the current isolation tests rely on and require a new threat review under TM-16 before implementation. Deletion must state what happens to media, backups already taken, and MCP credentials scoped to that domain. Transfer must create independent destination records behind an explicit preview rather than introducing a cross-domain reference.

MCP disposition for the remaining items: **Deferred** until their designs exist. Deletion and transfer are destructive and must not reach the MCP surface before the browser workflow and its previews are settled. Linked item: this section. Owner: TBD. Review: 2026-10-01.

## MCP client interoperability

Status: Open defect, found 2026-09-08 by the first live client run against a deployed package.

A standard MCP client cannot use the deployed MCP surface. Every request must carry a `Mcp-Method` header naming the JSON-RPC method, and `tools/call` must additionally carry `Mcp-Name` naming the tool. Without them the request fails with HTTP 400 (`-32020`) before reaching any tool:

| Request | Result |
| --- | --- |
| Standard client, no extra headers | HTTP 400, "Missing required Mcp-Method header." |
| `Mcp-Method` only | HTTP 400, "Missing required Mcp-Name header." |
| `Mcp-Method` + `Mcp-Name` | HTTP 200 |

Requests must also carry `_meta/io.modelcontextprotocol/protocolVersion` and `_meta/io.modelcontextprotocol/clientCapabilities`, and `initialize` is rejected as unavailable on protocol version `2026-07-28`.

This went unnoticed because `RemoteDiscoveryTests` builds every request through a helper that always sets both headers, so the in-process suite can never fail this way. It is the concrete reason the live-client gate was worth keeping open.

### What must be decided

First establish whether `Mcp-Method` and `Mcp-Name` are part of the MCP `2026-07-28` revision or a DnaX addition. That is a question about the specification and the DnaX implementation, and it is not answered by this repository.

- If they are DnaX-specific, the surface is not interoperable with standard clients and the requirement should be relaxed to optional, with the routing and audit information derived from the JSON-RPC body instead. That is a DnaX change and needs approval.
- If the revision does define them, then the requirement is correct and the gap is documentation: [the MCP contract](mcp-contract.md) and the README should state the exact transport requirements a client must satisfy, because a reader today would reasonably expect a stock client to work.

Either way, add at least one test that exercises the surface without the headers so the answer is pinned.

MCP disposition: **Not applicable.** This is the transport contract itself rather than a tool.

Owner: Agent. Next action: check the revision, then either raise a DnaX issue or document the requirement. Review: 2026-09-14.

## Structured errors on read tools

Status: Open defect, found 2026-09-08 during the same live run. Low severity.

An invalid `domainId` makes the older `records.read` tools return an unstructured `An error occurred invoking '<tool>'` message instead of a structured error. `list_record_types`, `search_records`, `list_field_definitions`, `get_record` and `query_record_types` all behave this way, while `query_records` and every write and export tool return a structured `validation_failed`.

Nothing leaks: the tools fail closed and disclose no data from any domain, so the isolation invariant holds and this is not a security defect. It is a contract defect. Clients are told errors are structured, so they cannot distinguish a bad selector from a server fault, and cannot act on the difference.

The fix is to wrap the read adapters in the same error mapping the write adapters already use. Add a test covering an invalid selector on every read tool rather than the one that happens to be correct.

MCP disposition: **Included**, since the fix is the error contract of the tools themselves.

Owner: Agent. Next action: apply the shared error mapping to the read adapters. Review: 2026-09-21.

## Preset upgrade workflow

Status: Planned. Not blocked; scheduled after the current MCP milestones.

Home, Workplace, Favourite Place, and Event are catalogue version 2 presets using the structured location field. Deployments that installed version 1 keep their descriptive-location and separate-radius fields, and are deliberately never changed silently. There is currently no supported path from an installed version 1 structure to version 2, so early alpha data will drift further from the catalogue with every preset revision.

The workflow must:

- detect installed preset versions per domain and report exactly which record types and fields differ from the current catalogue;
- preview the effect on records, values, saved views, dashboard categories, calendar and reminder eligibility, and MCP field identifiers before anything is written;
- reuse the existing field merge and conversion machinery, including its fail-closed validation of every stored value and its revision fingerprint, rather than adding a second schema-change path;
- keep locally made edits, preserving a renamed or reconfigured field instead of overwriting administrator intent;
- apply in one transaction per domain, and leave the preset provenance and version recorded afterwards;
- remain optional, so a deployment can decline an upgrade indefinitely without breaking.

MCP disposition: **Included**. Version inspection and the preview belong with the existing structure discovery tools; the apply command belongs under `structure.write` with the usual revision checks and durable receipts.

Owner: Agent. Next action: write the upgrade design against the existing merge/conversion services and the catalogue version records. Review: 2026-10-01.

## Migration ledger compatibility

Status: Diagnosed and closed by decision on 2026-09-07. The published `v0.1.0-alpha.1` and `v0.1.0-alpha.2` packages are superseded rather than repaired. Prevention is in place; no code fix is planned.

A deployment created by either published alpha package cannot be started by current `main`. DnaX reports the database in migration state `Drifted` and refuses to migrate, because the recorded checksums of 18 applied migrations differ from the same-named migrations in current code. Applied migrations are immutable by design, so there is no automatic recovery.

### Cause, established

- The migration SQL lives in C# raw string literals, which preserve the source file's line endings verbatim. Until commit `4eddd54`, `.gitattributes` carried no `eol=lf` rule for those files, so a Windows checkout produced CRLF and a Linux checkout produced LF. The same commit therefore built into binaries that write different ledgers: a build reproducibility defect.
- DnaX hashes the supplied migration text verbatim and does not normalize newlines. Confirmed directly: identical SQL differing only in line endings yields two different checksums from `DnaXMigration.Sql`.
- Line endings are the sole cause. Building current `main` with only the migration sources converted to CRLF reproduces the archived fixture's stored ledger checksums exactly (v1 `246563bc`, v2 `ddc4b39f`, v3 `f61d4f27`). The migration text itself never changed, so the divergence carries no schema meaning.
- Restoring the archived format 1 package succeeds and its data is intact. Neither the backup format nor the restore path is at fault.

Responsibility is shared and the ordering matters. Monkeysphere created the variance by letting source bytes depend on the checkout. DnaX turned an invisible whitespace difference into an unrecoverable startup failure by hashing verbatim and offering no repair, override or drift-tolerance API. Either side alone would have prevented it.

### Blast radius, measured

`v0.1.0-alpha.2` is commit `30e8bea`. The migration SQL compiled into `Monkeysphere.Data.dll` was read directly out of both published ZIPs of that tag and contains CRLF.

| Artifact | Built on | Line endings | Upgrades to current `main` |
| --- | --- | --- | --- |
| alpha.1 / alpha.2 `win-x64` ZIP | `windows-latest` | CRLF, verified from the published binary | No; startup fails |
| alpha.1 / alpha.2 `linux-x64` ZIP | `windows-latest` | CRLF, verified from the published binary | No; startup fails |
| alpha.1 / alpha.2 Docker image | `ubuntu-latest` | LF, verified from the published image | Yes |

Both ZIPs are produced by the same Windows release job, so they agree with each other. The container is built on Linux and is LF. This was verified by pulling both published images straight from the GHCR blob API and reading the compiled SQL out of `app/Monkeysphere.Data.dll`, without a container runtime.

So the two artifacts of the same tag genuinely disagree: a data root created by the alpha container cannot be opened by the alpha ZIPs, and the reverse. That is a defect in the shipped alphas independent of any upgrade. The container ledgers match current builds, so container deployments upgrade normally; only the ZIP deployments are stranded.

Every Monkeysphere-owned manifest is affected: the per-domain application schema, the domain registry, and the upload staging database. DnaX's own remote-access schema is compiled inside the DnaX package and is unaffected.

Recorded exposure is one download of each release asset, consistent with the maintainer's own release verification, and no data root exists on the development machine.

### Decision

Supersede, do not repair. A one-time ledger repair is technically clean, and would not need a DnaX change: `DnaXMigration.ComputeChecksum` is public, `IDnaXMigrationAdapter` exposes `ReadLedgerAsync` and `EnsureLedgerAsync`, and `DnaXAppliedMigration.Checksum` is settable, so the expected pre-fix checksum could be recomputed exactly by CRLF-converting the current source and rewritten only on an exact match. It is rejected anyway: it buys nothing for a defect with no known victims, and its real cost is a permanent checksum-mutation path and a permanently weakened immutability guarantee. Both alphas are labelled unupgradable in their release notes instead.

### Prevention, in place

- `.gitattributes` now applies `* text=auto eol=lf` repository-wide rather than an allowlist of four paths. The previous rule covered the migration files that existed at the time; a future manifest in a file outside those globs would have silently reintroduced the defect. Byte-exact vendored assets and the archived backup fixture keep an explicit `-text`.
- `MigrationTests.ReleasedMigrationChecksumsRemainStable` pins every released checksum. This was verified to fail on a deliberately CRLF-converted build, and `eng/Build.ps1` runs it, so the release workflow now blocks a recurrence rather than shipping it.
- Newline normalization before hashing has been raised with DnaX as the upstream fix that would remove the class for every consumer.

MCP disposition: **Not applicable.** A startup and storage-lifecycle defect with no tool surface. `get_instance_info` must keep reporting the true schema version once the database opens.

Owner: Wixely / Agent. Remaining: nothing; `v0.1.0-alpha.3` supersedes both affected releases. Review: 2026-09-14. This supersedes part of [upgrade path verification](#upgrade-path-verification), which now has its first concrete evidence, and it is negative.

## Content Security Policy completion

Status: Planned. Currently an accepted residual risk (TM-10) blocking any claim of a hardened public-browser deployment.

Responses already deny framing and object embedding, restrict cross-site referrers to the site origin, prevent MIME sniffing, restrict powerful browser features, and constrain base URI. A complete script and style policy is not enforced because Blazor emits a generated import map and framework boot resources that need a nonce or hash design.

The work is to determine what the current Blazor host actually emits, choose nonce or hash enforcement per resource kind, apply it to first-party scripts, vendored assets, and inline style, and add response-header tests alongside the existing header assertions. The optional OpenStreetMap tile path must be covered by an explicit connect and image source rule that stays closed while tiles are disabled.

MCP disposition: **Not applicable.** This is a browser-transport control with no MCP surface. The remote surfaces are unaffected.

Owner: Agent. Next action: inventory the emitted script and style resources in a Release publish, then choose the enforcement design. Review: 2026-10-01. Update [security](security.md) and [threat model](threat-model.md) TM-10 in the same change.

## Accessibility conformance

Status: Planned. A focused review exists; conformance verification does not.

[The accessibility review](accessibility.md) records native-element use, the skip link, combobox semantics, and bounded non-visual alternatives for the graph and map, verified through the Chromium accessibility tree at desktop and 390 by 844 sizes. It is explicitly not a WCAG conformance claim.

Remaining verification, all requiring environments that are not currently available:

- at least one dedicated screen reader per supported platform, covering login, setup, the record editor, saved views, the graph alternative, and the map list;
- Windows high-contrast mode across both themes;
- browser zoom to 200 percent and 400 percent reflow without loss of content or function;
- an automated accessibility scanner run over every primary authenticated route, with findings triaged rather than suppressed.

Until these are done, describe the state as a focused review and do not publish a conformance level.

MCP disposition: **Not applicable.** Browser presentation only.

Owner: TBD. Next action: identify which of the four checks can be run on available hardware and schedule those first. Review: 2026-10-01.

## Platform support verification

Status: Planned. These are the largest gaps between what the README describes and what has been observed.

| Gap | Current evidence | Required | Owner | Review |
| --- | --- | --- | --- | --- |
| Windows Service install, start, stop, restart, recovery | Complete 2026-09-09: `eng/VerifyWindowsService.ps1` passed elevated against the published alpha.3 win-x64 package, with the temporary service and its files confirmed removed | Upgrade across released versions only | Wixely / Agent | 2026-09-28 |
| Framework-dependent Linux package on an independently installed ASP.NET Core runtime | Complete 2026-09-09: the published alpha.3 linux-x64 package passed `eng/VerifyLinuxPackage.sh` on clean Ubuntu 24.04 against `aspnetcore-runtime-10.0` 10.0.11 | Done | Wixely / Agent | 2026-09-28 |
| systemd system-unit installation and boot enablement | Complete 2026-09-09: the packaged unit was installed as a real system unit with a dedicated account, reached `Type=notify` active, survived SIGKILL under `Restart=on-failure`, and auto-started after a cold boot with data intact | Upgrade across released versions only | Wixely / Agent | 2026-09-28 |
| Reverse proxy and HTTPS boundary | Documented as an operator responsibility | One documented working configuration, including forwarded-header trust, verified end to end | TBD | 2026-11-01 |

Docker and Compose are the best-evidenced deployment shapes and are verified through rootless Podman and an ephemeral nested daemon. Host storage, networking, and boot policy remain operator concerns in every shape.

MCP disposition: **Not applicable** for the verification work itself. Operational status reporting is separately covered by MCP milestone M6.

Owner: Wixely / Agent. Next action: nothing outstanding except upgrade across released versions, tracked under [upgrade path verification](#upgrade-path-verification). Windows Service, the packaged Linux run and the systemd system unit were all verified on 2026-09-09 against the published alpha.3 artifacts. Review: 2026-09-28.

## Backup and restore follow-up

Status: In progress. Format 2 creation, validation, and offline restore are complete and tested; compatibility and drill evidence are not.

- **Format 1 compatibility fixture.** Complete. A genuine single-domain format 1 package produced by the last format 1 commit (`30e8bea`, application schema 18) is committed at `tests/Monkeysphere.Web.Tests/Fixtures/format-1-schema-18.monkeysphere-backup` and restored by `ArchivedBackupCompatibilityTests`. The tests assert the legacy layout, the absence of a domain catalogue, the retained original image, the absence of derivatives, and the archived record, alias and field value. They stop at the restored data root: booting the application against it fails for an unrelated reason recorded under [migration ledger compatibility](#migration-ledger-compatibility). Owner: Agent; review 2026-09-28.
- **Restore drill on a clean host.** The offline `--restore-backup` procedure has been exercised in tests and locally. Repeat it once on a clean host following only the published instructions, and correct whatever the instructions get wrong. Owner: Wixely / Agent; review 2026-11-01.
- **Scheduled backup evidence.** Recurrence calculation is tested and scheduling is off by default. A long-running deployment has not yet produced and retained a real scheduled series, including retention cleanup after a verified package. Owner: TBD; review 2026-11-01.
- **Restore remains deliberately offline.** Restoring through the browser or MCP is excluded, not deferred: it would require the running process to replace the data it is serving. See [deliberately excluded](#deliberately-excluded).

MCP disposition: **Deferred** for backup creation, listing, validation, and download, which are scoped into MCP milestone M6. Restore stays outside the MCP surface by design.

Owner: Agent. Next action: produce and commit the format 1 fixture, then restore it in the integration suite. Review: 2026-09-28.

## Dependency and supply-chain maintenance

Status: Later; steady-state maintenance with one specific blocker.

- **DnaX is pinned to `10.0.0-alpha.3`.** Every remote-access, migration, and hosting behavior therefore depends on a prerelease of a separate repository. Consuming a stable DnaX version is a beta gate. It also requires the clean package described under [release follow-up](#release-follow-up). Owner: Wixely; review 2026-11-01.
- **Vendored browser assets.** OpenLayers 10.10.0 and Cytoscape.js 3.34.0 are vendored with hash verification and a no-CDN build check. Upgrades need a fresh hash, a notices update, and a visual sweep of the graph and map. Owner: Agent; review 2026-11-01.
- **Native image decoding.** SkiaSharp remains a native-code dependency (TM-08). Keep it patched; do not add image formats without fuzz and boundary review. Owner: Agent; ongoing.
- **Audit cadence.** Re-run `eng\VerifySupplyChain.ps1 -AuditVulnerabilities` before every release candidate. A past clean audit is not evidence for a later build. Owner: Wixely / Agent; ongoing.

MCP disposition: **Not applicable** for maintenance itself. Reporting effective dependency versions is part of the existing `get_instance_info` surface and must stay accurate when versions change.

Owner: Wixely / Agent. Review: 2026-11-01.

## Map tile provider configuration

Status: Later. Accepted residual risk TM-11.

External basemap tiles are off by default. When an administrator explicitly enables them, attributed OpenStreetMap tiles are requested and the provider learns the client IP address, browser metadata, site origin, and viewed area. Record names are never sent, and no geocoding service is used.

An operator who needs stricter isolation currently has only two options: leave tiles off, or accept that disclosure. A configurable tile-source setting would let them point at an approved internal or self-hosted tile service instead. The design must keep the default off, keep the disclosure text accurate for whichever provider is configured, validate the URL template without permitting arbitrary outbound requests derived from record data, and preserve the build check that rejects undeclared public HTTP dependencies in first-party browser entry points.

MCP disposition: **Deferred** to MCP milestone M5, which covers settings. The underlying setting should be readable and writable there once the browser design is settled, since it is deployment configuration rather than a visual interaction. Linked item: this section. Owner: TBD. Review: 2026-11-01.

## Interaction timeline

Status: Later; design required. Identified in [the Monica comparison](../comparisons/monica.md) as the largest functional gap for relationship use.

Records and relationships can already represent an event, but there is no fast way to capture what happened with a person, or to read it back chronologically. This is the one personal-CRM capability worth adopting before any peripheral module.

Intended shape:

- quick capture of a call, message, visit, shared activity, or free-form note;
- one interaction connecting several records of any type, not only people;
- a chronological view on every involved record;
- approximate and coarse timestamps through the existing temporal precision model, rather than a second date representation;
- implemented as a record type and relationship pattern wherever possible, with dedicated interface only where it materially improves capture and browsing.

Design must decide whether interactions are an ordinary preset, a preset with special dashboard and timeline projections, or a separate relational concept, and must state the consequence for saved views, the graph, the calendar, search bounds, backups, and MCP. Nothing here should privilege people over other record types.

Independent design and implementation only. Monica is AGPL-3.0-or-later and its source must not be copied into this MIT codebase; its behavior may be used as a reference.

MCP disposition: **Included** once designed. Capture and chronological read are basic functionality and must not be browser-only. Expect reuse of the existing record write grants rather than a new one, if interactions are modelled as records.

Owner: TBD. Next action: write the design note choosing the data model and stating its effect on existing projections. Review: 2026-11-01.

## Upgrade path verification

Status: Partly done. The upgrade mechanism was verified alpha.3 to alpha.4 on 2026-09-09 on both Windows and Linux: replacing only the binaries left the data root and ledger byte-identical and the service started without drift. Because those releases share application schema 28, no migration ran, so a schema-changing upgrade remains untested. The earlier negative evidence stands separately under [migration ledger compatibility](#migration-ledger-compatibility).

DnaX historical verification constructs and upgrades every application schema version to the same canonical schema, and the registry and staging manifests have their own historical checks. That covers the database. It does not cover a real deployment moving between released versions with its data, media, configuration, remote-access state, and container or service definition in place.

Required before 1.0: install a released version, populate it, upgrade in place across at least two subsequent releases in each supported deployment shape, and confirm data, media derivatives, saved views, remote credentials, and scheduled backups all survive. Record what an operator must do manually.

MCP disposition: **Not applicable.** Upgrade is an operator procedure. The schema version already reported by `get_instance_info` must remain correct across it.

Owner: TBD. Next action: define the minimum upgrade matrix once `0.2.0` exists. Review: 2026-12-01.

## Release follow-up

- Produce a clean DnaX package release whose compiled assemblies do not contain local build paths, then update Monkeysphere before the next public prerelease. Owner: Wixely / Agent; approval required for the separate DnaX repository. Review: 2026-09-28.
- Complete live privileged Windows Service and installed-systemd lifecycle verification. Owner: Wixely / Agent. Tracked in detail under [platform support verification](#platform-support-verification).
- Confirm the GitHub Container Registry package visibility matches the documented anonymous `docker pull` instruction before the next release is announced. Owner: Wixely; review 2026-09-28.
- Publish a replacement prerelease. Both published alphas are labelled unupgradable and should not be the newest thing a visitor finds. Owner: Wixely; review 2026-09-28. See [migration ledger compatibility](#migration-ledger-compatibility).
- Keep the README capability description synchronized with [verification status](verification.md) at each tag. Owner: Wixely / Agent; ongoing.

## Saved graph layouts

Status: Complete and merged to `main`.

- Saved graph views persist bounded coordinates for every displayed record through DnaX migration 19.
- Existing nodes retain their saved or current coordinates when filters refresh or new matching records appear.
- Initial layout, newly introduced nodes, and drag completion enforce deterministic minimum spacing so nodes do not overlap.
- Later consideration: user-configurable spacing and an explicit automatic relayout action. Owner: TBD; review date: 2026-10-01.

MCP disposition for saved views and graph queries: **Deferred** to MCP milestone M5.

## Deliberately excluded

These are decisions, not backlog. Reopening one requires a new design and, where marked, a new threat review.

| Item | Reason |
| --- | --- |
| Multi-user accounts, tenancy, and sharing | Equivalent support would change authorization, database ownership, media paths, backups, the HTTP API, MCP, and migrations at once. Monkeysphere remains explicitly single-administrator until a complete tenancy and sharing model is designed. Domains are an organizational boundary and must never be described as a tenancy control. Requires a new threat review. |
| Cross-domain relationship links | Would weaken the isolation invariant that every other domain guarantee rests on. A future transfer or copy workflow must create independent destination records behind an explicit preview instead. |
| Browser or MCP restore | Restore replaces the data the running process is serving. It stays an offline command with the data-root instance lock held. |
| Outbound notification delivery (email, push, chat) | Reminders are deliberately local and are never sent to an outside service. Adding delivery introduces transport, credential, and privacy obligations that must be evaluated on their own merits, not adopted as parity work. |
| Journal, mood, gifts, and debt tracking | Outside the private knowledge-graph identity. An administrator can already model these as record types if they want them. |
| Geocoding and address lookup | Would send private place data to a provider. Coordinate entry stays provider-free. |
| Analytics, telemetry, and crash reporting | Normal operation makes no request to an external service. |
| Direct migration from another application's database | Migration is built on open formats. vCard import exists; any later importer must consume a documented export format and preserve source identifiers as provenance, never read another application's internal tables. |
| Arbitrary SQL, shell access, and filesystem browsing over remote surfaces | Excluded from the API and MCP by design, at every scope. |

Localization is not on this list. It is simply not started, has no owner, and would need translated content plus a review of date, name, and address presentation. Raise it as a roadmap item when someone intends to do it.
