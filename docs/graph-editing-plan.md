# Graph editing surface implementation plan

- Status: M1 delivered 2026-09-22; D3 and D6 settled and their groundwork delivered 2026-09-23; M0 tagging work and M2-M5 remain
- Created and last reviewed: 2026-09-22
- Plan owner: TBD
- Next review: 2026-10-05
- Source: user request captured 2026-09-21 in the Monkeysphere ideas list
- Related: [performance boundaries](performance-boundaries.md), [accessibility review](accessibility.md), [universal tags](universal-tags.md), [roadmap](roadmap.md)

## Outcome and scope

The relationship graph should become a place records are managed, not only explored. Today it answers "what is connected to what"; it should also answer "and change it" for the things the canvas is already showing — tags, membership, and the relationships themselves — without a trip to a record form for every small edit.

Four capabilities are in scope:

1. A fullscreen graph.
2. Tag editing from a record node, including bulk tag changes across a multi-selection.
3. Creating a record from empty canvas.
4. Creating, editing and deleting relationships between displayed records.

Record forms remain the complete editing surface and the only place field values, images, aliases and record identity change. The graph gains the subset of editing that is *about* what the canvas already draws. Explicitly out of scope: field values, images, record deletion, record-type and relationship-type management, domain management, and any change to the layout algorithm or the accepted rendering boundary.

## Verified starting point

Source inspection on 2026-09-22. Rather more of this already exists than the captured idea assumed, which changes the shape of the work from "build interactions" to "extend interactions".

**A context menu already exists.** [RelationshipGraphCanvas.razor](../src/Monkeysphere.Web/Components/RelationshipGraphCanvas.razor) renders `.graph-record-menu` with `role="menu"`, currently holding exactly one item — a "View record" link. [relationship-graph.js](../src/Monkeysphere.Web/wwwroot/relationship-graph.js) wires it to cytoscape's `cxttap`, positions it within the element bounds, moves focus into it on open, dismisses it on `Escape`, on `tap`/`drag`/`pan`/`zoom`, and on a `pointerdown` outside the shell, and opens it from the keyboard via `ContextMenu` or `Shift+F10` against the selected node. Adding menu items is therefore an extension of a working, focus-managed, keyboard-reachable menu, not a new mechanism.

**Multi-selection already exists.** The cytoscape instance uses `selectionType: 'additive'`; shift-drag box-selects, `Ctrl`/`Cmd`/`Shift`-click toggles one node, dragging any selected node moves the group, and `.graph-multi-selection` is a `role="status"` live region that announces the count. Bulk operations have a selection model to build on.

**Cytoscape 3.34.0 is vendored** (`vendor/cytoscape/3.34.0/cytoscape.min.js`, referenced from [App.razor](../src/Monkeysphere.Web/Components/App.razor)) and the roadmap records a no-CDN build check. Every asset this work adds stays local.

**The tag pill editor already exists.** [TagInput.razor](../src/Monkeysphere.Web/Components/TagInput.razor) is a reusable combobox-semantics pill editor with catalogue-backed suggestions, per-tag colour and icon, and keyboard commit and removal. The graph should reuse it rather than grow a second tag control.

**The tag catalogue is deployment-wide.** `ITagCatalogue.EnsureAsync` returns the canonical spelling and the caller must store that rather than what was typed; renames propagate to domain databases through the `TagOutbox` queue and are eventually consistent. Anything the graph writes must go through the catalogue, satisfying the captured constraint that bulk tagging reuse the universal catalogue.

**Bounds are enforced in Core.** `RelationshipGraphService` caps 500 nodes, 2,000 edges, depth 0-3, a 200-character search, 100 selected records and 100 record types, and the result carries `NodesTruncated` and `EdgesTruncated`, which the page surfaces as a notice.

**The accessible alternative sits outside the canvas shell.** [RelationshipGraph.razor](../src/Monkeysphere.Web/Components/Pages/RelationshipGraph.razor) renders the "Centre a displayed record" `SearchableSelect` as a sibling *after* `<RelationshipGraphCanvas>`, so it is not inside `.relationship-graph-shell`. [accessibility.md](accessibility.md) requires every displayed node to be reachable from that combobox and asserts its presence in rendering tests. This directly constrains the fullscreen design; see D5.

**Saved views hold layout, and layout is dirty-tracked.** `GraphView` persists display mode, record IDs, record type IDs, node positions and viewport. `OnGraphChanged` marks the page dirty and a `NavigationLock` confirms before navigating away. `runLayout` already preserves positions across an update, which matters because every edit below forces a re-query.

**Hidden records are an absolute boundary.** The roadmap records `BackstageLeakTests` enumerating nineteen read surfaces — graph nodes and edges, tag search and the universal-tag vocabulary among them — and asserts an ordinary reader reaches none of them while a backstage reader reaches all nineteen. Graph traversal already refuses to enter a hidden node. Every new write surface here inherits that requirement.

### Gaps in the service layer

Three things the plan needs do not exist yet, and each is a deliberate decision rather than a mechanical addition:

- **There is no tag-only record mutation.** `IMonkeysphereService.UpdateRecordAsync(id, displayName, values, aliases, expectedRevision, tags)` takes the record's full value set. Null tags leave tags unchanged, but there is no inverse — no way to change tags while leaving values alone without first reading and resupplying every value. Driving bulk tagging through it would mean N reads and N full rewrites, with a clobbering risk on every one.
- **Graph nodes carry neither tags nor a revision.** `RelationshipGraphNode` has record ID, type, name, distance, image and symbol. `RecordSummary` has no tags either; only `RecordDetails` carries `Tags` and `Revision`. So the canvas cannot currently show or safely edit a node's tags from what it already holds.
- **There is no relationship instance update.** `IRelationshipService` offers `CreateAsync` and `DeleteAsync` for relationships plus rename and retire for *types*, but nothing that edits an existing relationship's note or type. "Edit a relationship" as requested needs a new Core method, or must be scoped down to create and delete.

## Design decisions to settle in M0

Each has a recommendation; none should be implemented before it is agreed, because all four UI milestones depend on them.

**D1 — Add a bounded tag-only mutation.** Recommend a new Core operation that adds and removes a set of tags across an explicit list of record IDs, transactionally per record, with a revision check per record and a per-record outcome (applied, stale, not found, or type has tags disabled). Bound the selection at 100 records to match the existing `MaximumSelectedRecords`. This keeps bulk tagging off the full-record update path and gives the UI something honest to report when one record in a selection has moved underneath it.

**D2 — Read tags on demand rather than widening the graph payload.** Recommend *not* adding `Tags` and `Revision` to `RelationshipGraphNode`: at 500 nodes that inflates every graph query for data only needed when a menu opens. Instead read the selected records' tags and revisions when the tag editor is opened, bounded by the same 100-record cap. Revisit only if the extra round trip is measurably worse in use.

**D3 — Decide the relationship editing verb set. Settled 2026-09-23: a proper edit, delivered.** `IRelationshipService.UpdateAsync` changes an existing relationship's type and note in place, with a revision check, so the relationship keeps its identity and anything already holding its ID still refers to the same thing. Moving a relationship onto a symmetric type re-applies the stored end ordering, which is the one case where an edit touches the ends. No UI yet; M5 is where it surfaces.

Original recommendation:  Recommend adding a relationship update for the note and the relationship type, with a revision check, so "edit" means edit rather than delete-and-recreate — recreating changes the relationship's identity, which would silently invalidate anything holding its ID. If that is judged too large for a first pass, ship create and delete only and say so in the UI, rather than implementing edit as delete-plus-create.

**D4 — Decide which record types are creatable from the canvas.** The captured open question. Recommend offering the record types currently selected in the view's type filter first, since that is the context the operator is already working in, with a search over all installed types behind a "more types" affordance. Where exactly one type is selected, default to it and ask only for the name.

**D5 — Fullscreen must include the accessible alternative. Settled and implemented in M1.** Fullscreening `.relationship-graph-shell` would drop the "Centre a displayed record" combobox out of view, because it is a sibling of the canvas component rather than a child. That is an accessibility regression against a documented, test-asserted requirement. Recommend introducing a wrapper element containing the canvas *and* the accessible controls, and requesting fullscreen on that.

**D6 — Decide what happens at the rendering boundary. Settled 2026-09-23, and not with either option offered.** Rather than choosing between refusing and warning, the boundary itself became configurable. It ships at the measured 500 nodes and 2,000 edges, can be raised from graph settings as far as 2,000 and 10,000, and the truncation notice now names the current limit and links to the page that changes it. That turns a hard stop into something an operator can push on and judge on their own hardware, which is a better answer than either refusing or warning about a number nobody could change. What M4 does when creating a record into an already-truncated view is still open, but the question is now smaller: the operator has somewhere to go.

Original recommendation, superseded:  A record or relationship created while the view is already truncated at 500 nodes or 2,000 edges may not appear in the re-query. Recommend that creation from the canvas refuses with an explanation while the view is truncated, rather than appearing to succeed and vanishing. The alternative — create anyway and warn — is acceptable but must be an explicit choice.

**D7 — Decide whether a created record joins the filter or the selection.** A new record may fall outside the active filter and so not appear at all. Recommend adding it to the page's selected-record set, which guarantees it is displayed, and pinning it at the click position so it appears where the operator clicked.

## Milestones

Ordered so each is independently shippable and the riskiest service work is settled first. M1 deliberately comes before the editing milestones because it is self-contained and delivers visible value immediately.

### M0 — Contracts and decisions

Dependencies: none.

- Settle D1-D7 and record the outcome in this document.
- Implement the Core and Data contracts chosen in D1 and D3, with migrations if required, behind no UI.
- Extend `BackstageLeakTests` coverage to the new write surfaces before they have callers.

Exit criteria: the new Core operations exist with unit and store tests, including revision-conflict and per-record-outcome cases; an ordinary reader cannot tag, relate to, or otherwise touch a hidden record through any new operation, and a backstage reader can; no UI references them yet.

### M1 — Fullscreen graph

Dependencies: D5.

- Add a fullscreen control to the graph panel, using the Fullscreen API on a wrapper that includes the canvas and the accessible controls.
- Keep the existing `ResizeObserver` path working so cytoscape resizes and badges reposition on entering and leaving fullscreen.
- Reflect state in the control (`aria-pressed` or an explicit label change), exit on `Escape`, and handle fullscreen being refused or exited by the browser rather than assuming the request succeeded.

Exit criteria: the graph fills the viewport and returns cleanly; the record-centring combobox and the selection live region remain present and operable in fullscreen; node positions and viewport are unchanged by the transition; a rendering test asserts the accessible combobox is still present while the wrapper is the fullscreen element.

**Delivered 2026-09-22.** A `.graph-stage` wrapper is the fullscreen element and holds the canvas, the record-centring combobox, the graph description the canvas points at with `aria-describedby`, and the controls that act on the graph. The controls sit *over* the canvas in a `.graph-canvas-frame` rather than in a row above it, because a row costs layout height everywhere and would be exactly the chrome fullscreen is meant to be free of. "Reset viewport" joined them there, since a control that cannot be reached in fullscreen is one the operator has to leave fullscreen to use. The stage renders whether or not the view has nodes, so the element registered with the browser stays valid when a filter empties the graph.

Fullscreen shows the graph and nothing else: no page chrome, no description, no combobox, and the canvas edge to edge without the border and rounding that only make sense inside a panel. The description and the combobox stay in the document rather than being removed, because they are the graph's non-visual alternative; they are taken out of sight, and the combobox returns as a corner panel the moment it takes focus, so a keyboard user is never typing into something invisible.

Entering fullscreen refits the graph, because a layout framed for a panel leaves most of a screen empty. Where the operator had it is captured first and put back on the way out — unless they moved the graph themselves while fullscreen, in which case that is the arrangement they meant to keep. Neither the fit nor the restore marks the view dirty, since programmatic viewport changes carry no originating event.

`graph-preferences.js` gained `initFullscreen`, `toggleFullscreen` and `disposeFullscreen`; state is only ever taken from the `fullscreenchange` event, never assumed from a request that appeared to succeed, so Escape and the browser's own chrome are seen the same way as the button, and a refused request says so instead of leaving the control stuck. Support is feature-detected unprefixed and the control is withheld where it is missing. Leaving the page exits fullscreen rather than stranding the browser there. `relationship-graph.js` gained `fitToElement` and `restoreViewport`, both of which wait two animation frames before measuring, because a fullscreen transition resizes the element after its event has already fired.

Verified in Chromium against a published build: the button appears, the fullscreen element is the stage, the label and `aria-pressed` follow the browser's own state, the graph refits to the screen, exiting restores the previous framing exactly, and the combobox measures 1×1 while fullscreen and 448×91 once focused. `GraphFullscreenTests` pins the containment requirement server-side; the button itself cannot be asserted there, because it is rendered only once interop has reported support and so is absent from prerendered HTML.

**Revised 2026-09-22 after use.** The controls became single icon buttons, because two labelled buttons over the canvas covered more graph than they were worth; the label each would have shown lives in `title` and `aria-label`, and the fullscreen control keeps one glyph in both states so it does not jump under the pointer.

Fullscreen also changes what a click does. It no longer opens the image gallery, which is a page-level overlay the fullscreen element does not contain: opening it showed nothing at the time and then presented a modal the moment the operator left fullscreen. A click now selects the record and stops there, and the canvas description stops promising images, since `aria-describedby` is read whether or not the paragraph is on screen.

Making that selection visible turned up a bug older than any of this work. The tap handler selected the node itself, but cytoscape finishes its own tap bookkeeping after user handlers have run and clears the selection on the way through, so a plain click never highlighted anything while a modified click, which returns early and lets cytoscape do the selecting, always did. Selecting on the next animation frame is what makes the two agree. It was invisible before only because the gallery opening was the feedback; fullscreen removed the gallery and left nothing.

One gap remains: the node/edge truncation notice sits above the panel rather than inside the stage, so it is not visible in fullscreen. Moving it in is a small follow-up, and matters more once M4 can create records against that boundary.

### M2 — Tag editing from a record node

Dependencies: M0 (D1, D2).

- Add a tag item to the existing record menu that opens `TagInput` for that record, seeded from an on-demand read of its current tags.
- Write through `ITagCatalogue`, storing the catalogue's canonical spelling.
- Respect a record type with tags disabled by saying so, rather than offering an editor that cannot apply.

Exit criteria: tags applied from the graph appear on the record form and in tag search without a reload; a tag typed with different casing or spacing resolves to the catalogue's spelling; a stale revision reports a conflict instead of overwriting; the menu item is reachable by `ContextMenu` and `Shift+F10`, and the editor is fully keyboard-operable.

### M3 — Bulk tag editing across a selection

Dependencies: M2.

- Offer add-tags and remove-tags across the current multi-selection, bounded at 100 records.
- Report per-record outcomes, naming the records that did not change and why, rather than a single success message.
- Provide a non-canvas route to the same operation so the capability is not mouse-only, consistent with the existing accessible-alternative requirement.

Exit criteria: applying a tag to a mixed selection adds it only where it is absent and leaves existing tags intact; removing affects only the named tags; a selection containing a record changed underneath the operator reports that record as stale while the rest apply; the announced result reaches assistive technology through a live region.

### M4 — Creating a record from the canvas

Dependencies: M0 (D4, D6, D7).

- Right-click on empty canvas offers record creation: choose a type per D4, set a display name, create.
- Place the new node at the click position and add it to the selected-record set per D7.
- Refuse per D6 while the view is truncated.

Exit criteria: the created record exists with the chosen type and name and is visible in the graph without a manual re-query; it opens in the record form with no field values and no aliases; unsaved node positions elsewhere in the view survive the re-query; creation while truncated is refused with an explanation.

### M5 — Relationship editing

Dependencies: M0 (D3), and M4 for the shared creation interaction.

- Create a relationship between two displayed records, choosing the type and optionally a note.
- Edit an existing relationship per D3, and delete one, both from the canvas.
- Keep edge labels and directionality consistent with the relationship type's configured inverse name.

Exit criteria: a relationship created from the graph appears on both records' forms with the correct direction and inverse label; editing changes the relationship in place and its ID is unchanged; deletion removes exactly one edge; a stale revision on edit or delete reports a conflict; every operation is reachable without a pointer.

## Cross-cutting requirements

- **Bounds are not relaxed.** 500 nodes, 2,000 edges, depth 3, a 200-character search, 100 selected records and 100 record types stay exactly as they are. No milestone raises a cap to make an interaction feel better.
- **Every canvas action has a non-canvas route.** The existing requirement is that every displayed node is reachable from the combobox; the equivalent for editing is that no capability is available only by right-clicking a node.
- **Hidden records stay hidden.** New write paths are read paths too — a suggestion list, a relationship target picker, or a per-record outcome message must not disclose a record the caller cannot see.
- **Optimistic concurrency throughout.** Every mutation carries an expected revision and reports staleness rather than overwriting, matching the record and relationship paths already in place.
- **Dirty-state honesty.** Edits force a re-query; unsaved positions must survive it, and the `NavigationLock` prompt must not start claiming unsaved changes that were in fact persisted.
- **Assets stay vendored.** No new CDN reference; the no-CDN build check must keep passing.

## Open questions

- D6 revisited: with the limit now raisable, what should M4 do when creating a record into a view that is already truncated?
- Should relationship *type* creation be reachable from the canvas when no suitable type exists, or should that remain a trip to Structures? Recommend the latter, to keep structure changes deliberate.
- The truncation notice is outside the stage and so is hidden in fullscreen. Move it in before M4, which has to report that boundary.
- Touch parity is unverified. The canvas `aria-label` tells the operator they can "long-press a record for actions", but the menu is wired only to cytoscape's `cxttap` and no `taphold` handler exists. Whether long-press reaches the menu on a real touch device needs checking before the menu carries destructive actions, and the label corrected if it does not.

## Next action

- [x] D5 settled and delivered as M1.
- [x] D3 settled and delivered: relationships can be edited in place.
- [x] D6 settled and delivered: the rendering boundary is configurable from graph settings.
- [ ] Settle D1, D2, D4 and D7 and record the decisions here, then implement the M0 tagging groundwork. Owner: TBD; review by 2026-10-05.
