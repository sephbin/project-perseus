# Federator — consultant link comparison

Branch: `develop/federator` (off `Element-Violations`). Started 2026-11-08.

## Goal

A federator (the architect coordinating consultant models) loads a new version of a consultant's
link and needs to know **what changed** since the previous version, review those changes in context,
and keep a record of the review (status + comments) **inside the host Revit model** so every team
member sees the same review.

## Decisions (2026-11-08)

| Question | Decision |
|---|---|
| Where does the "old version" come from? | User picks an archived `.rvt` of the consultant model ("Compare against file…"). Perseus opens it detached in the background, snapshots it and closes it. No automatic snapshotting on link reload. |
| Where do snapshots live? | `%AppData%\ProjectPerseus\federator\snapshots\` (gzip JSON), per user. They are only a **cache** so re-running a comparison doesn't re-open the old file. |
| Where do results and comments live? | **In the host model as Extensible Storage (plugin data).** Comparison results and every review comment / status change are DataStorage elements. Linked elements are referenced symbolically (link instance UniqueId + linked element UniqueId + ElementId + stable Reference string). |

## How it works

```
 archived consultant .rvt ──copy to temp (unique name)──► OpenDocumentFile (detached, all worksets)
                                                              │ SnapshotBuilder
                                                              ▼
                                                   LinkSnapshot (old)  ──┐   cached in AppData
 loaded link (RevitLinkInstance.GetLinkDocument) ── SnapshotBuilder ─────┤
                                                   LinkSnapshot (new)  ──┘
                                                              │ LinkDiff.Compare (pure C#, unit-tested)
                                                              ▼
                                         List<ElementChange>  ──► ComparisonStore (DataStorage, gzip bytes)
                                                              │
                         ┌────────────────────────────────────┼─────────────────────────────┐
                         ▼                                    ▼                             ▼
             ui/FederatorWindow (review panel)     overlay (changed / ghost of       ReviewStore (append-only
             filter, select/zoom, comment,         deleted elements, via the         DataStorage per comment /
             status                                existing marker overlay)          status event)
```

* **Why copy the old file to a temp name:** Revit refuses to open a document whose title matches an open
  document — including a loaded link. Copying to `<name>__perseus_base_<hash>.rvt` avoids it.
* **Matching:** by element `UniqueId` (stable across versions of the same model). Added = only in new,
  Deleted = only in old, Modified = in both with any aspect changed.
* **Aspects of a modification:** `Moved` (location / bbox centre moved > tolerance), `Geometry` (solid
  volume / area / face / edge counts or bbox size changed), `Type` (family or type changed), `Level`,
  `Parameters` (instance or type parameter values, with an ignore list e.g. "Edited by").
* **Coordinates:** snapshots are in the link's own internal coordinates. They are moved to host coordinates
  at display time with the *current* `RevitLinkInstance.GetTotalTransform()`, so deleted elements can be
  drawn as ghosts from their old bounding box.

## Storing review data in the model (worksharing-safe)

Extensible Storage schemas are immutable once created, so each schema holds a small fixed header plus a
versioned JSON / gzip payload — new fields go into the payload, never into a new schema.

* **Comparison record** — one DataStorage per comparison run, created once, never edited:
  header `{RecordId, LinkInstanceUid, LinkTitle, CreatedBy, CreatedUtc}`, payload = gzip JSON of
  `{old/new snapshot labels, options, List<ElementChange>}` in an `IList<byte>` field.
* **Review event** — one DataStorage per comment or status change, append-only:
  `{EventId, ComparisonId, LinkInstanceUid, LinkedElementUid, LinkedElementId, StableRef, Author, Utc,
  Status?, Text}`. An element's current status = its latest status event.
  *Append-only means no user ever edits another user's element*, so there are no worksharing ownership
  conflicts; new events are owned by their author until they sync (Revit releases them then). Users may
  delete only their own events.
* Symbolic link to the linked element: `LinkedElementUid` is authoritative; `StableRef`
  (`new Reference(el).CreateLinkReference(linkInstance).ConvertToStableRepresentation(host)`) lets
  `Selection.SetReferences` select the linked element directly.

## Phases

| # | Phase | Status |
|---|---|---|
| F1 | `models/LinkSnapshot`, `models/ElementChange`, `federator/LinkDiff` (pure, tests), `federator/SnapshotBuilder` (Revit), `federator/SnapshotCache` (AppData gzip) | done (untested in Revit) |
| F2 | `federator/OldModelOpener` — copy + unload its links + detached open + snapshot + close; `federator/ComparisonRunner` end to end with `ui/SyncStatusToast` progress | done (untested in Revit) |
| F3 | `federator/ComparisonStore` + `federator/ReviewStore` (Extensible Storage, append-only events), `federator/ReviewLog` fold (tests), `federator/LinkedElementRef` | done (untested in Revit) |
| F4 | `ui/FederatorWindow` — modeless WPF panel: link picker, "Compare against file…", comparisons list, change grid with filters (kind, aspect, category, level, review status, in active view, search), detail pane (parameter diffs old → new), status + comment thread, select / zoom to linked element (`SetReferences`, `ZoomAndCenterRectangle`) | |
| F5 | Overlay layer: changed elements in the active view coloured by kind (added green / modified amber / deleted red ghost), reusing `MarkerEngine` hit projection in `queue/ViolationScreenPositionEvent` | |
| F6 | `commands/ShowFederatorCommand` + "Federator" ribbon panel + icon in `tools/generate_icons.py` | |
| F7 | Later: export review (Excel / BCF), optional Django upload of comparison summaries | |

## Open points

* Very large consultant models: geometry signatures need `get_Geometry` per element. Expect minutes on
  100k elements. Archived files are cached by path + size + mtime, so re-comparing against the same old
  file is cheap. The loaded link's snapshot is *not* cached (its file on disk may already be newer than
  what's loaded); if that proves slow, cache it in memory per link instance until the link reloads.
* Nested links are ignored (same as markers).
* Revit 2023 has no `FilteredElementCollector(host, view, linkInstance)`; "in active view" filtering falls
  back to `ViewDepth` as `MarkerEngine` does.
