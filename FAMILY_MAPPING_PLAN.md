# Family mapping — our families at the consultants' locations

Started 2026-11-12 (v1.3). Builds on the federator (FEDERATOR_PLAN.md): comparisons, triage, replacements.

## Goal

Place our own Revit families at the same locations as consultant families (e.g. our smoke detector symbol at each
of the services consultant's detectors), or link families already in our model to them, and keep each of ours tied
to the consultant element it stands for, so later versions (moves, deletes, delete-and-replace) can be followed.

## Requirements (from the user, 2026-11-12)

| # | Requirement | Where |
|---|---|---|
| R1 | Use triage data to infer which consultant types to map | `mapping/TypeTriage` (reviewed / open changes = suggested; all ignored / noise / noise category = not wanted) |
| R2 | 1:1 mapping with XYZ offset and XYZ rotation | `models/FamilyMapRule`, `mapping/MappingMath` (offset in the consultant element's axes; rotation X then Y then Z) |
| R3 | Test one type at a time | Family mapping window: **Test on one** (Revit-selected consultant element or n-th sample), live re-place while tuning, **Next sample** |
| R4 | Control the workset placed families go to | Rule `Workset` (by name) |
| R5 | Placed families track the consultant element that created them | `mapping/MappedFamilyStore` — plugin data on OUR instance: link instance uid, consultant uid / id, rule, placed / linked, who, when |
| R6 | Tie into replacements when new versions load | `MappedFamilyStore.Current()` follows `ReplacementLinks.Map` (no double placing); **Follow replacements** rewrites records |
| R7 | Place from scratch AND link existing families | `FamilyPlacer.PlaceAll` / `FamilyPlacer.LinkExisting` (nearest expected position within a tolerance, one-to-one) |
| R8 | Prefer floating placement over hosting | Level-based: nearest level below + offset (plan rotation only). Work-plane / face-based: own SketchPlane through the point (full rotation). Wall / ceiling / curve-hosted: not placed (reported) |

## Stages

1. **Mapping + placement (DONE, v1.3.x)** — window (Federator → Family mapping…), triage suggestions, rule editor,
   test on one, save (shared, `FederatorSettingsStore` "familymap:<consultant model key>"), place all, link existing,
   follow replacements, tracking records. Unit tests: `MappingMathTest`.
2+3. **Link Sync (agreed 2026-11-13, building)** — its own panel (ribbon Federator → Link Sync). The mapper sets the
   rules (and now only tests, with temporary families); Link Sync applies them and keeps our families matching the
   links. Agreed with the user:
   - **State-based**: compares the LIVE link with our tracked families; comparisons only explain the delta. Replacements
     (federator Replacements links) must be caught: ours follow the replacement.
   - **Every change is flagged, then approved or rejected, per change**: one of ours can have a move AND a type change;
     approve one, reject the other. Approve / reject selected, approve all shown.
     | Change | Approve (take the consultant's state) | Reject |
     |---|---|---|
     | New (mapped consultant element, none of ours) | place ours | don't propose it again |
     | Match (one of ours, unrecorded, at the expected spot) | link it | don't propose it again |
     | Moved (consultant moved beyond tolerance, or the rule's offset / rotation changed) | move ours | keep ours (until the expected position changes again) |
     | Consultant type changed | change ours to the rule of the new type | keep |
     | Mapping type changed (rule now says another of our types) | change type | keep |
     | Replaced (consultant deleted, replacement linked) | point ours at the replacement | keep the old link (then orphaned) |
     | Orphaned (consultant element gone, no replacement) | DELETE ours | keep |
     | Kept (edited by hand) | move back to the consultant position | — (it already rejects the consultant position) |
   - **Hand edits** = ours moved since Perseus last placed / accepted it while the consultant and rule didn't change:
     shown as "Kept (edited by hand)" = rejecting the consultant's position.
   - **Tolerances per rule** (mapper): XY mm and Z mm (default 25 / 25), plus 1° rotation, so small nudges don't flag.
   - **Checking tools**: zoom / 3D / select using the review views (ribbon gets a Review Views button too, so they can be
     edited without opening the panels).
   - **Perseus shared parameters**, bound as project (instance) parameters to the categories of our mapped types:
     Perseus_LinkSource (link · consultant element id) and Perseus_LinkStatus (sync status), for schedules / filters / tags.
   - **Scope**: all mapped links, one, or a choice; option **only elements in the active view** (ours or the consultant's
     visible) so each person can work their own zone.
   - Tracking record v2 (JSON) stores the consultant frame + type and our frame at the last accept, and rejections.
4. **Nice to have** — mirror handling, hosted fallback (nearest face) when a family can't float, parameter copy
   (consultant parameter → our parameter per rule), mapping import / export between projects, ribbon entry.

## Open questions

- (Answered 2026-11-13) Moves are flagged and approved / rejected, never automatic.
- (Answered 2026-11-13) Yes: Perseus shared parameters as project parameters on the mapped categories.


### Orphans and markers (2026-11-13)
- Existing families of ours (mapped target type, not linked): linked to a consultant element when within 300 mm (or the rule tolerance)
  of where the mapping puts ours (Match; beyond tolerance it's then listed as moved), else **Orphaned (no consultant element)**:
  approve deletes ours, reject keeps it (model-wide list `linksync-kept-unlinked`).
- Ours deleted after being linked: **Orphaned (ours deleted)**. Links are remembered as `connect` events in the Link Sync log (existing
  records registered when the panel first reads them). Approve places ours again, reject leaves it deleted (`linksync-rejected:<link>`).
- Model markers: moves = magenta ring on ours (this model) + cyan ring where the linked model puts it + double-headed arrow; orphans =
  red rings (solid on ours / dashed at the consultant element whose ours was deleted).
- (2026-11-14) Markers are boxes again (magenta ours / cyan linked position, a badge on each). Right-click a badge or a row: disconnect
  ours from its consultant element (neither proposed again), connect ours to a consultant element / a consultant element to ours (picked
  in Revit, one-to-one). A copy of one of ours (Revit copies the record) is unlinked on the next refresh: records keep the UniqueId they
  were written on. Undo / redo refreshes the panel.
- (2026-11-14) "Ours" is "local family" in the UI. Link Sync rows follow the Revit selection both ways (clicking elsewhere clears them and
  their emphasis); move arrows shade magenta -> cyan. Family mapping: Tolerance XY + Z column, multi-select edits.
- (2026-11-14) Rejecting = the modeller stands by the local position: the change keeps its kind, flagged rejected, and moves to a
  "Set aside" list under the open changes (lighter text, thinner markers) until the linked model changes again. Approve still takes the
  consultant's state; Reconsider puts it back for a decision.
- (2026-11-14) A hand edit after Link Sync placed / accepted a family is a rejection: listed as Kept (edited by hand) in Set aside, 'Rejected by hand edit · <editor>'. Reconsider reopens it as Moved until it's moved again.

## Stage 5: parameter mapping (planned 2026-11-14)

Map consultant parameter values onto our mapped families, per rule (consultant type -> local family type).

### Decisions (user, 2026-11-14)
- **Apply mode per mapping**: through Link Sync approval (default) or "always apply" (opt in, written on each Link Sync run without asking).
- **Adding parameters = project parameters only** (instance or type, bound to the category; shared definitions). No editing inside
  families. Existing family parameters can still be mapping targets (if writable). "Add to family" (EditFamily + FamilyManager +
  LoadFamily) only later, as its own opt-in, if needed for parameters that must drive geometry / formulas.
- **Shared parameter file**: per user, the Perseus file in %AppData% as today. GUIDs are deterministic, so every user generates the same
  definition; the file is only a local copy (the definition lives in the model once bound). New mapped parameters hash
  **name + data type** (existing Perseus_* parameters keep their name-only GUIDs: changing them would orphan the bound ones). One group
  per model inside the file (tidiness only). Optional machine-wide setting (IT, like archive.json) to use a company shared parameter file.

### Parameters on each side
- Consultant (source): instance + type parameters of the consultant element (marked which), built-ins (Mark, Comments, Level...), plus
  computed fields (consultant element id, link name, IFC GUID).
- Local (target): parameters on the target family type / its instances (family parameters) + project parameters bound to its category.
  Writable only: read-only, formula-driven and reporting parameters are flagged / filtered out.
- Identity: shared parameters by GUID, built-ins by BuiltInParameter, others by name + storage type (names collide).

### Compatibility check + preview
- Spec-aware: number -> number needs the same spec (length -> length ok, length -> area not; internal units are feet). Number -> text
  formats with the project units. Text -> number parses and reports failures. Yes/No -> text "Yes"/"No"; ElementId (material, level)
  -> its name as text; integer <-> number.
- Preview on real consultant values: a few before/after samples + counts (empty / converted / failing) before saving.
- Type-parameter target: one value per local type, so feed it from a consultant type parameter; warn when the consultant instances of
  that type disagree.

### Adding a parameter (project parameter)
- Name, data type (spec), instance vs type (default = the source parameter's), group Other, categories (the rule's target category;
  check Category.AllowsBoundParameters). Instance <-> type can't be switched later without losing values: chosen up front, and an
  existing parameter of the wrong kind is warned about.
- If the model already has a parameter of that name with a compatible type (e.g. a company standard), map to it instead of adding.
  Same name with another type: warn (Revit allows it, people get confused).
- Binding changes the model's parameter bindings: own clearly named transaction, like the Link Sync parameters.

### Sync (Link Sync)
- New change kind "Parameter changed" (one per parameter, so each can be approved / rejected), baseline = the consultant value at the
  last accept, stored in the mapped-family record. Local edit of a mapped value = rejected (set aside), as for hand moves. Always-apply
  mappings write straight away (logged), still skipping elements checked out by others.
- New / re-placed families get mapped values on placement.
- Several links writing one local parameter: warn in the mapper; the rule order decides.
- Removing a mapping leaves written values in place (option to clear).

### Phases
1. [DONE 2026-11-14] Detect + map (read only): parameter discovery both sides, per-rule mapping UI, compatibility check, preview. Copy mappings to
   other selected rules (mapper multi-select).
2. [DONE 2026-11-15] Add parameters: Perseus shared parameter file (name + type GUID, group per model), project parameter binding (instance / type),
   reuse of existing same-name parameters, optional company shared parameter file.
3. [DONE 2026-11-15] Apply: Link Sync "Parameter changed" (approve / reject, baseline, hand edits = rejected), always-apply mode, values on placement.
4. [DONE 2026-11-15] Transforms: constants, prefix / suffix, lookup table ("FR60" -> "60/60/60"), default for empty, expressions (util/FilterEvaluator).
   Order: constant | default -> lookup -> expression -> prefix / suffix (models/ParamTransform, mapping/ParamTransforms). A step that changes the value
   makes it text (then converted like a text parameter); unchanged values keep their kind. Baselines use the transformed value, so editing a lookup
   table re-opens rejected decisions. Mapper: Transform… (ui/TransformDialog, live preview, table filled from consultant values); a mapping without a
   consultant parameter = constant. Not yet: other consultant parameters inside an expression (row["Mark"]), clearing values when a mapping is removed.
