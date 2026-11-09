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
| F4 | `ui/FederatorWindow` review panel (see *Review panel* below) + `ChangeDescriber`, `ReplacementMatcher`, `ChangeFilter`, `ReviewNavigator`, `ViewExtent`, `FederatorController`; status carry-over and actions in `ReviewLog` | built, unit-tested; not yet tried in Revit |
| F5 | See the changes: `ChangeHighlightServer` (DirectContext3D, all model views incl. 3D, on screen only) driven by the panel's "Show in view" (shown or selected rows). Bake: `AnnotationBaker` + `ui/BakeDialog` — printable outlines / revision clouds + labels in views picked by regex and ticks, settings shared per link | built, unit-tested; not yet tried in Revit |
| F6 | `commands/ShowFederatorCommand` + "Federator" ribbon panel ("Link Changes") + `federator` icon | done |
| F7 | Later: export review (Excel / BCF), optional Django upload of comparison summaries | |

## Review panel (decided 2026-11-08)

* Modeless WPF window (a dockable pane can come later). Grid grouped by Category, with *Group by* Level / Kind /
  Status / Assignee / Type / None; columns Kind, Element, Id, Level, Change, Status, **Assignee**, comments.
* **Statuses**: Unreviewed, Accepted, Query, Resolved, Ignored. Comments can always be added, whatever the status.
* **Carry-over**: every change carries a fingerprint of the element's current state (`ElementChange.Signature`).
  A status set in an earlier comparison applies to a later one when the fingerprint is the same (the element hasn't
  changed since it was reviewed); shown as "Accepted ↺". If it changed again it's Unreviewed.
* **How it changed**: `ChangeDescriber` writes sentences ("Moved 130 mm (120 mm horizontally, 50 mm up)",
  "Length 4.2 m → 4.8 m (+600 mm)", "Type changed: …", "3 parameters changed: …") plus a before/after table.
* **Orphans**: each deleted element lists the added elements that probably replaced it (same category; overlap /
  proximity, then same type / family / level), and added elements show what they probably replace. "Show old and
  replacement" zooms to both.
* **Actions**: a `ReviewAction` (free-text type — Review, Approve, Respond, Coordinate, Update model or anything else —
  plus an assignee, a Revit user or a discipline) is opened by a review event and closed (Done / Cancelled) by a later
  one. Open actions drive the Assignee column and the Assignee filter, including "(Me)" and "(Unassigned)".
* **Navigation**: double-click / Select in Revit selects the linked elements and zooms; when they aren't visible in
  the active view, the user's own "Perseus Federator - <user>" 3D view opens with a section box around them.

## Version pickers (decided 2026-11-10)

Links change often, there can be many of them, and on ACC / BIM 360 / Forma most people don't use the website's
version history. So the panel compares **any two versions**: Link, then **From** and **To** pickers, then Compare.

* **Stage 1 (built, 1.2.359)**: the pickers list the live (loaded) link, every version with a cached snapshot
  (✓ = nothing to open or download), files used in earlier comparisons of that link, files picked this session, and
  "Browse for a file…". Versions are identified by the model's save GUID (`Document.GetDocumentVersion` /
  `BasicFileInfo.GetDocumentVersion`), so the same version reached through the live link or a downloaded copy is one
  entry and is never read twice. The live link's snapshot is now cached under its save GUID (it can't change), so
  after Reload Latest the previous version stays comparable. Snapshots are only taken when a version is compared
  (decision: no automatic snapshot on reload; it freezes Revit for minutes on big models).
  Code: federator/VersionCatalog (per-model list in AppData), SnapshotCache (file + version keys),
  ComparisonRunner.Source, ui/FederatorWindow version pickers.
* **Stage 2 (built 2026-11-10, untested against a real hub until an APS app is set up)**: the full cloud version list
  (including versions nobody has opened) from the Autodesk Platform
  Services Data Management API. The Revit API has no version history (2026: only `GetCloudModelUrn`, `GetProjectId`,
  `GetHubId`; Revit only opens the latest version) and gives add-ins no Autodesk login token. Needs: an APS app
  (client ID) registered at aps.autodesk.com; a one-time browser sign-in per user (3-legged PKCE, no secret, e.g. via
  the bundled WebView2), or the token flow on the Django server; possibly the ACC account admin adding the app as a
  custom integration. Then GET `projects/b.{projectId}/items/{urn}/versions` lists versions and their storage
  object is downloaded to a temp .rvt, which OldModelOpener reads like any archived file (cached ✓ afterwards).
  Download: since 15 Feb 2026 a published cloud workshared model downloads as the host .rvt only, via
  `construction/rcm/v1/projects/{id}/published-versions/{versionId}/linked-files?includeHost=true` (versions published
  since Feb 2025); older versions fall back to the storage object (a ZIP with the host inside); non-workshared cloud
  files use the storage object (OSS signeds3download). Code: aps/ApsConfig (client ID + callback port, per user),
  aps/ApsAuth (PKCE, browser + localhost listener, DPAPI refresh token), aps/ApsClient (versions, download), panel:
  ☁ entries listed in the background (Set up / Sign in / retry entries when not available), download on Compare,
  snapshot cached (✓, VersionCatalog keeps the cloud version id), download deleted.

## Later ideas

* Geometry clash test of changed elements against a view's range (and other links) using their boxes, to rank
  changes that matter for a given sheet / view.
* Move a review thread from a deleted element to its confirmed replacement.
* Dockable pane version of the panel; overlay markers (F5); export (F7).

## Open points

* Very large consultant models: geometry signatures need `get_Geometry` per element. Expect minutes on
  100k elements. Archived files are cached by path + size + mtime, and every version (live link included)
  by its save GUID, so re-comparing a version is cheap (see Version pickers).
* Nested links are ignored (same as markers).
* Revit 2023 has no `FilteredElementCollector(host, view, linkInstance)`; "in active view" filtering falls
  back to `ViewDepth` as `MarkerEngine` does.
