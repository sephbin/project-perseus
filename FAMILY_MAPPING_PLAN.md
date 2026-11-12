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
2. **Status + upkeep** — per-rule health in the window: ours placed / linked / missing / orphaned (consultant
   element gone, no replacement), moved since placement (compare the consultant frame against the one used); select /
   zoom lists; delete or re-place orphans.
3. **Follow new versions** — after a comparison: consultant element moved → move ours (proposal list, apply in bulk);
   deleted + replacement linked → repoint (automatic); deleted without replacement → flag ours; new consultant
   elements of a mapped type → place. Hook into the guided review (after Replacements).
4. **Nice to have** — mirror handling, hosted fallback (nearest face) when a family can't float, parameter copy
   (consultant parameter → our parameter per rule), mapping import / export between projects, ribbon entry.

## Open questions

- When a consultant element moves in a new version, should ours follow automatically or be proposed first? (Stage 3; default plan: proposed, applied in bulk.)
- Should our placed families carry a visible parameter with the consultant id (for schedules / tags), as well as the hidden record?
