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
