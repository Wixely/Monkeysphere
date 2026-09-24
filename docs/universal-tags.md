# Universal tags

Status: Implemented, and complete from 2026-09-24. Application migrations 32-33 and 37, registry
migration 7, MCP contract 1.21.

A saved view can now narrow by tags and show them as a column, which was the last outstanding
piece. Both are their own thing on the view rather than entries among its field columns and
filters, because everything a view stored was keyed to a field definition and a universal tag has
none. The tags it lists carry the meaning an ad-hoc search already gave them: every one must be on
the record, so each narrows the view further, and matching ignores case as tag storage does.

Tags are a property of a record, not of its type. Every record carries tags unless an
administrator has deliberately removed tags from that record type. This replaces the current
arrangement, in which `tags` is only a *field type* that an administrator may or may not have
attached to a given record type, so that whether a record can be tagged at all depends on
choices made during setup.

## Why this is not the existing tags field

`FieldTypes.Tags` describes a field whose value is a list of short strings — "Likes", "Genres",
"Platforms". It is attached per record type, stored per field value in `FieldValueTags`, and is
part of a record's structured data. Those fields stay exactly as they are; they mean specific
things and a Person's "Likes" is not interchangeable with a Book's "Genres".

Universal tags are the opposite: one unnamed, freeform, cross-cutting vocabulary that exists on
every record regardless of type, for the labels that do not deserve a field. The two coexist.

## Invariants

- Every record has tags. A newly created record type is tagged-enabled without the administrator
  choosing anything.
- An administrator can remove tags from a record type. This is the only way tags become absent.
- Removing tags from a record type is non-destructive. Existing tag values are retained, hidden
  from ordinary reads and editing, and restored intact if tags are re-enabled. Monkeysphere does
  not discard data to satisfy a display preference.
- Tag values are trimmed, case-insensitively unique within a record, and ordered, matching the
  existing rules for `FieldValueTags` so that the two behave alike wherever both appear.
- **The tag catalogue is deployment-wide.** This is the one place a domain boundary is deliberately crossed, and it reverses the original per-domain design; the reasoning is in "One label, one tag" below.
- Tags are ordinary record content: changing them changes the record's revision, and they are
  withheld by backstage exactly as the rest of the record is.

## One label, one tag

Tags began per-domain: each domain's database held its own strings and no domain knew what another
called anything. That could not satisfy two requirements at once - an administrator curating tags
in a single place, and typing a label already used elsewhere joining that tag rather than minting a
second one with the same name. Both need a tag to be recognisable from outside the domain it was
typed in, so the catalogue moved to the domain registry, which is the only deployment-wide store.

What crosses the boundary is deliberately narrow:

- **The catalogue holds the tag**: name, colour, icon, and which domains offer it. Records still
  store the tag's *text* in their own domain database, so a domain still reads correctly on its own
  and losing the registry costs appearance and membership rather than data.
- **`RecordTags.TagId`** records which catalogue entry the text came from. It is not a foreign key
  and nothing resolves through it at read time; it exists so a rename can find the rows without
  having to know what the tag used to be called.
- **Suggestions never cross.** A tag belonging only to another domain is not offered here, so the
  editor cannot be used to learn what another sphere contains. Typing that label in full still
  joins the existing tag - the identity is shared, the discovery is not.
- **Membership is curation, not access control.** It decides where a tag is offered. A record's
  tags are withheld with the record by backstage policy, exactly as before.

## Appearance

A new tag gets a random colour and the `#` icon. The colour is deliberately not a uniform random
RGB triple, which is frequently unreadable against one of the two themes: the hue is free while
saturation and lightness are held in a band that stays legible on both the light and the dark
ground. An icon is one or two text elements, so an emoji counts as one.

## Renaming, and why it is not atomic

The catalogue accepts a rename immediately; the domain databases holding the old text are brought
across by a durable queue, drained at startup and every five minutes. Between the two, a domain
still shows the old label. That is inherent rather than a shortcut - the text lives in other
database files and no transaction spans them - so the page says so rather than implying otherwise.

A rename onto a name another tag already holds is refused. Merging two tags has different
consequences for records than renaming one, and doing it silently would be the wrong default.
Where the drain finds a record already carrying the target label, the losing row is dropped and the
two become one on that record, because the alternative is violating the record's own uniqueness
rule.

## Removing a domain from a tag

Un-ticking a domain removes the tag from every record in it. This is destructive and cannot be
undone, so the page counts the affected records first and states the number before the save.

Like a rename, the removal is queued rather than performed inline, for the same reason: the
records live in another database file. Queuing it inside the same registry transaction as the
membership change is what stops an interruption from leaving records carrying a tag the
catalogue has already forgotten, with nothing left to retry it. Deleting a tag works the same
way across every domain that held it.

## Storage

Migration 32 adds one table and one record-type column:

```sql
CREATE TABLE RecordTags (
    RecordId TEXT NOT NULL,
    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
    Value TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY (RecordId, Ordinal),
    UNIQUE (RecordId, Value),
    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE
);
CREATE INDEX IX_RecordTags_Value ON RecordTags(Value, RecordId);

ALTER TABLE RecordTypes ADD COLUMN TagsEnabled INTEGER NOT NULL DEFAULT 1
    CHECK (TagsEnabled IN (0, 1));
```

The table deliberately mirrors `FieldValueTags` — same key shape, same `COLLATE NOCASE`
uniqueness, same cascade — so that one set of normalization rules covers both.

`TagsEnabled` defaults to 1, which is what makes tags universal: every record type that already
exists, and every one installed later from a preset, is tagged unless someone says otherwise.

The same migration extends the revision triggers, so that a tag edit bumps `Records.Revision`
the way an alias or field edit does:

```sql
CREATE TRIGGER RecordTags_Revision_INSERT AFTER INSERT ON RecordTags BEGIN
    UPDATE Records SET Revision = lower(hex(randomblob(16))) WHERE Id = NEW.RecordId;
END;
-- and UPDATE / DELETE equivalents
```

Without these, a tag edit would be invisible to stale-edit detection, to contact-import preview
invalidation, and to twin synchronization, all of which decide whether they are looking at
current data by comparing that revision.

## Backstage

A hidden record's tags are part of that record and are withheld with it. The subtle case is the
**tag vocabulary**: the list of every tag in use in the domain, which the editor offers as
suggestions and which filtering uses to populate its choices. Built naively, that list is a
disclosure channel — a hidden record tagged `severance-negotiation` would advertise its own
existence to an ordinary reader through an autocomplete dropdown, without ever being readable.

Every vocabulary query therefore joins `Records` and carries `BackstageFilter.AndVisible`, the
same predicate every other read applies. The exhaustive backstage leak test gains the tag
surfaces — record read, editor suggestions, vocabulary listing, tag filtering, saved views, and
the remote reads — so a tag surface cannot pass merely because the fixture never reached it.

## Surfaces

- **Record editor** — a tag input on every record whose type has tags enabled, with suggestions
  drawn from the visible vocabulary.
- **Record type settings** — a "Tags" toggle, stating plainly that turning it off retains
  existing values and hides them, and showing how many records currently carry tags.
- **Search** — record search matches tags alongside primary names, aliases, and field values.
- **Saved views** — a tag filter, and tags as a selectable column.
- **Remote** — `records.read` returns tags; `records.write` sets them; `query_records` filters on
  them. All three inherit the existing grants; no new scope is introduced, because tags are record
  content rather than a separate authority.

## MCP disposition

**Included**, as contracts 1.21 and 1.22. No new tool and no new grant: tags are ordinary record content, so
they sit under the existing `records.read` and `records.write` authority and are withheld with the
record by backstage policy. `get_record` returns them, `query_records` filters on up to ten
AND-combined tags, `create_record`/`validate_record` accept them, `patch_record` gains
`replace_tags`, and a batched `create` carries them. A patch that omits `replace_tags` preserves
them. See the [MCP contract](mcp-contract.md).

## Interaction with domain mobility

Tags are plain strings with no identifier, so they survive a move or a twin link without any
mapping — unlike fields, which must be matched by canonical key across domains. A record moved
into a domain that has never seen a given tag simply introduces it to that domain's vocabulary.
See [record mobility](record-mobility.md).
