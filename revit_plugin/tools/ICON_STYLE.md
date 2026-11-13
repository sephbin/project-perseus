# Perseus ribbon icon style guide

Applies to every icon in `revit_plugin/src/ProjectPerseus/resources/`, whether drawn as SVG in
`generate_icons.py` or generated with Gemini and processed by `process_ai_icons.py`.

## Format
- 128x128 PNG, transparent background, `<name>.png` (light theme) + `<name>_dark.png` (dark theme).
- Revit shows them at **32px** (stacked/small) and **64px** (large). Design for 32px first.
- Content fills the square inside an **8px margin** (at 128px); centred.

## Drawing
- **Flat line icons**, front or plan view. No isometric, perspective, 3D, shading, gradients,
  shadows, textures, or outer tile/frame.
- **One metaphor** (the noun), plus at most **one accent modifier** (the verb: arrow, magnifier,
  check, plus...), usually bottom-right or overlapping the noun.
- Strokes: uniform, rounded caps and joins, equivalent to **2px on a 32px grid** (8px at 128px).
  Outlines, not filled shapes; small solid dots are fine.
- At most ~6 distinct strokes. If a detail disappears at 32px, remove it.
- No text, letters or numbers (exception: a single digit where the meaning is a number, e.g. renumber).

## Colour (roles, never decoration)
| Role | Light | Dark | Use |
|---|---|---|---|
| Ink | `#2f3d57` | `#e3e9f5` | The noun / main object |
| Accent | `#00a0e0` | `#3cc8ff` | The action / what the button does |
| Warning | `#e6007e` | `#ff4fae` | Only violations, safety, destructive |
| Blue | `#1f4fd8` | `#6f8cff` | Rare secondary accent |

Two colours per icon is normal: ink + one of accent/warning. Never accent and warning together.
`process_ai_icons.py` snaps every pixel to this palette and writes the dark variant by role.

## Generating with Gemini (Claude in Chrome)
1. `python process_ai_icons.py prompt <name> "<glyph description>" [--warn]` prints the prompt
   (it carries these rules; background is flat #00FF00 for keying).
2. Generate at gemini.google.com; Claude downloads it as `~/Downloads/perseus-icon-<name>.png`.
3. `python process_ai_icons.py ingest` then `build <name>`; check `icons_ai/review/review_sheet.png`
   (old vs new, 128/64/32, light/dark). Regenerate if it doesn't read at 32px.
4. `python process_ai_icons.py approve <name>` copies into `resources/`, then remove `<name>` from
   `ICONS` in `generate_icons.py` so a re-run there doesn't overwrite it. Raw masters stay in
   `icons_ai/raw/`.
5. New icon names also need `<Resource Include="resources\<name>.png" />` (+ `_dark`) in
   `ProjectPerseus.csproj`.
