# Reference collections + parameter naming conventions

Started 2026-11-23. Two features, agreed together because the first needs the second.

1. **Reference collections**: an element (first use: a wayfinding sign pack type) holds an ordered list of references
   to other elements (sign types). Templates loop over them; instances can override member values.
2. **Parameter naming conventions**: every parameter Perseus creates gets its name from layered settings
   (server-wide -> project -> model), so an office or a BIM execution plan (e.g. "COX_" prefix) controls them.

## Requirements (from the user, 2026-11-23)

| # | Requirement | Where |
|---|---|---|
| R1 | A reference stores the target's **ElementId and name**; elements are queried by id. Neither key is trusted alone (wayfinding ids like "ST07" are as fragile as ElementIds) | `collections/ReferenceResolver` |
| R2 | Members are sign types for now; build it generic (any element, any category) | collection config: targets filter |
| R3 | Both storage forms: one list parameter, or numbered slot parameters (`COX_Sign_{n}`) | `collections/ReferenceCodec` |
| R4 | Reference parameters are Perseus parameters, **not editable by hand** (UserModifiable = false); only the Perseus UI writes them | `ids/SharedParameterManager` (locked path, already exists) |
| R5 | Instance overrides of member values, stored as JSON in a configurable parameter (Cox: "Messaging Override") | `collections/OverrideCodec` |
| R6 | Templates loop over collection members (combined message, per-slot values, or both: the user writes the templates) | `util/JinjaTemplate` (RelatedMany) |
| R7 | Its own ribbon panel, not part of Auto Parameters | ribbon panel "Collections" |
| R8 | No hard-coded parameter names: names are configurable; if a parameter doesn't exist Perseus creates it as a Perseus shared parameter | `naming/` |
| R9 | Naming conventions for every Perseus parameter (Perseus_ID, Link Sync, collections, ...), editable in Revit and on the web, resolved by specificity: server-wide -> project -> model | django-perseus + `naming/ParameterNames` |

## Part A: parameter naming conventions (build first)

### Logical keys

Code stops using literal names. Each Perseus parameter gets a stable **logical key**, and the name comes from the
convention:

| Key | Default name | Today |
|---|---|---|
| `perseus.id` | `Perseus_ID` | 3 literals |
| `linksync.source` / `.status` / `.decision` / `.comment` / `.assignee` | `Perseus_LinkSource` ... | literals in `SharedParameterManager.EnsureLinkSyncParameters` |
| `collection.<name>.members` | set per collection | new |
| `collection.<name>.overrides` | set per collection | new |

Auto Parameters rule parameters keep the names typed in the rule (the rule IS their configuration): the prefix is NOT
added to them (as built 2026-11-23: renaming rule parameters silently would break claims, Audit and schedules; users
type the BIM execution plan's prefix into the rule name). The Auto Parameters editor refuses a rule named like one of
Perseus's own parameters.

### Settings (one object per layer; a field left blank = inherit)

```json
{
  "prefix": "COX_",                 // added to every Perseus parameter name (keyed + collections), never twice
  "names": { "perseus.id": "Perseus_ID", "linksync.status": "LinkStatus" },   // per-key overrides (before prefix)
  "parameter_group": "other"        // parameter group Perseus binds to (ID_PARAMETER_GROUPS key)
}
```

Resolution (as built): model layer > project layer > built-in default, and for models outside any project model layer >
server-wide layer > built-in default, **per field**. A project never reads the live server-wide row: it is a fork (a project can set the
prefix while the server sets the names). "Server-wide" = the whole Django instance (agreed 2026-11-23; a more general
specificity system can come later).

**Fork on creation (agreed 2026-11-23):** a new Project copies the effective server-wide convention into its own layer
when it is created, so later office changes never rename parameters in running projects. Projects with no layer yet
(existing ones) were forked by migration 0071 (and are forked on first read if one is ever missing). The project page shows where its convention now
differs from the server-wide one, with "Adopt server-wide" (followed by a migration offer in Revit).

### Server (django-perseus)

- `ParameterNaming` model: one row for the server-wide layer, one per Project, one per Source (nullable fields).
- `GET /api/source/<guid>/parameter-naming/` -> effective names + each layer (`?layer=server|project|source`, same
  shape as id-rules). `PUT` with `?layer=` via PerseusSession (auth/PairingSession) and edit rights per layer
  (server layer = staff only).
- Web page under the source hub + project page + admin-level page for the server layer. Help guide in /help/.

### Plugin

- `naming/ParameterNames.cs`: convention per model GUID (loaded on DocumentOpened). `For(doc, key)` = the name IN USE:
  the model's record (`naming/ParameterNameRecords`, plugin data written when Perseus creates the parameter) -> the
  built-in name when that Perseus parameter is already bound (models from before conventions) -> `Wanted(doc, key)`
  (the convention's name). So a convention change only affects parameters not created yet.
- `ui/ParameterNamingWindow.cs`: edit one layer at a time (This model / Project / Server), preview of every resolved
  name. Setup panel button.
- **Changing a convention on a model that already has the parameters** = migration, only on request (never on open,
  never automatic; agreed 2026-11-23):
  create the new parameter, copy values, remove the old (the `SharedParameterManager.SwitchLock` pattern). Shared
  parameter GUIDs stay derived from the **resolved name** (two names must never share a GUID, or copying elements
  between models with different conventions clashes). Built: Parameter Naming > Rename parameters (and an offer after
  a save) -> `SharedParameterManager.Rename(key)`; refuses non-Perseus parameters, taken names, case-only changes,
  elements checked out by others / out of date.

## Part B: reference collections

### Collection config (per model / project layer, own endpoint, not in id-rules)

| Field | Cox example |
|---|---|
| Name (template object name) | `Signs` |
| Lives on | Type (or Instance) |
| Owner filter (which elements have it) | category Signage, family name matches `Sign Pack.*` |
| Storage | slots `Sign_{n}` (created as needed) **or** list `Sign Members` |
| Targets allowed | category Signage, types only |
| Override parameter (instance) | `Messaging Override` (blank = no overrides) |
| Overridable fields | blank = any parameter of the member |

Parameter names here go through the naming convention (prefix etc.).

### Storage formats

- Reference text: `12345|ST07 Exit` (ElementId, `|`, element name). Name escaping: `|` in a name -> `\|`.
- Slot parameter: one reference per parameter; empty = empty slot (order kept, holes allowed). Slots grow on demand
  (agreed 2026-11-23): adding a member past the last slot creates the next parameter (`Sign_9`) and binds it.
- List parameter: JSON array `[{"id":12345,"name":"ST07 Exit"}, ...]`.
- Override parameter (on the instance): JSON keyed by **slot** (members repeat, so the slot is the key):
  `{"1": {"Message": "Plant Room 3"}, "3": {"Arrow": "Left"}}`. Documented in the help guide for Grasshopper.

### Resolving a reference (`ReferenceResolver`, pure + Revit adapter)

1. `doc.GetElement(id)`: exists, allowed target, and its name equals the stored name -> **ok**.
2. Id missing / not a target / name differs -> search allowed targets by name: one match -> **stale (re-pointable)**;
   none or several -> **broken**. The template still gets the id's element when it exists (marked `stale`).
3. Audit + the Collections window list stale / broken references with fixes: re-point to the name match, or accept a
   rename (rewrite the stored name).

### Templates (`util/JinjaTemplate`)

- `IObjectSource.RelatedMany(from, kind)` -> list; collection names become template objects next to Element / Type.
- Each member = the target's parameters with the owner instance's overrides merged on top, plus `slot`, `base`
  (values before override), `overridden` (bool), `stale`, `id`.
- Collections are reachable from the element (`Signs`) and its type (`Type.Signs`, when the collection lives on the type).

```jinja
{% for s in Signs %}{{ s.Message }}{% unless forloop.last %} / {% endunless %}{% endfor %}
{{ Signs[0].Message }}
```

- Dependency index (`IdAssigner` DepsOf / DependentsOf): an owner depends on each member and on its own override
  parameter, so editing ST07 re-renders every pack holding it, on every instance.

### Collections window (`ui/CollectionsWindow.cs`, modeless; ribbon panel "Collections")

- Collection picker (from config) -> owners list (types with instance counts, search, stale badge).
- Members: ordered slots; add from a target list (search) or Pick in model, reorder, remove, duplicate; empty slots.
- Preview: rendered values of the value rules that read this collection, for the selected owner / instance.
- Instance tab (follows Revit selection): members x overridable fields grid, overridden cells highlighted, Reset cell /
  row; multi-select applies to all selected instances.
- Writes via `queue/OwnershipUiEvent` (the parameters are locked, so this is the only edit path); type checked out by
  someone else -> read-only with their name.

## Stages

1. **Naming conventions (DONE 2026-11-23)** — Django model + API + web pages; plugin `naming/ParameterNames`, existing literals moved to
   keys, Revit editor, migration offer. Tests: layer resolution (Django + plugin).
2. **Collections core (DONE 2026-11-24)** — config endpoint, `ReferenceCodec`, `ReferenceResolver`, `OverrideCodec` (pure, unit-tested),
   locked parameters created via `SharedParameterManager`. As built: django-perseus `ReferenceCollectionSet` (JSON list per
   project / source layer, `perseus/reference_collections.py`, `GET/PUT api/source/<guid>/collections/?layer=`, web editors,
   help guide); plugin `collections/` (ReferenceCodec, OverrideCodec, ReferenceResolver, CollectionsCache, CollectionStore),
   `SharedParameterManager.EnsureParameters` with type bindings, tests in `ReferenceCodecTest.cs`. Details below.
3. **Templates (DONE 2026-11-24)** — `RelatedMany`, member objects with overrides, dependency index. Tests in
   `IdPatternTest.JinjaTemplates_LoopOverCollections`. Details below.
4. **Collections window (DONE 2026-11-24)** — members, overrides, preview, stale fixes. Details below.
5. **Audit + help** — stale / broken references in Audit; help guides for both features.
6. **Lists on the owner (DONE 2026-11-26)** — per collection `lists`: member parameter -> owner text parameter (as typed, no prefix;
   existing shared parameter used, else a locked Perseus one, type-bound on type collections), delimiter (presets / custom, `\n`
   escapes), skip empty, unique. Server: `reference_collections._clean_lists` + web editor table + help. Plugin: collections/ListRollup
   (pure), CollectionLists (Refresh), CollectionListController (DocumentChanged -> OwnershipUiEvent), Audit. Type owners list the type's
   members as they are; instance owners use their overrides. Other users' edits arriving by sync are left to their Perseus / Audit.

### Stage 4 as built (2026-11-24)

- `ui/CollectionsWindow` (modeless, ribbon panel "Collections", `commands/ShowCollectionsCommand`, icon `collections`). Edits are a
  local draft until **Apply** (one transaction, "Perseus: Edit collection"); Revert / switching owner or collection asks first.
- Members: slots with status (ok / stale / renamed / broken, reason in the tooltip); add from the allowed targets (search,
  double-click) or Pick in model (an instance -> its type when members are types), empty slot, duplicate, move (Alt+Up/Down), remove
  (Delete), clear, replace; Fix selected / Fix all stale use ReferenceResolver's fix (Repoint / AcceptRename). Broken = replace by hand.
- Each draft slot remembers the slot it was read from (`Origin`), so Apply remaps every placed instance's overrides with its members
  (`Overrides.Remap`); a removed member's overrides go; only appends -> `PruneAbove`. Instances with overrides checked out by someone
  else: asked (they keep old slot numbers); instances you edited that are checked out elsewhere block Apply.
- Instance overrides follow Revit's selection (SelectionChanged subscribed while the window is open): one owner type at a time, several
  instances together (cells that differ shown orange, typing sets all). Columns = overridable fields, or (none listed) fields already
  overridden + Add field (member parameter names). Grid is read only while member edits are pending (overrides follow slot numbers).
- Preview: `IdAssigner.PreviewCollectionValues` (value rules on the element whose Jinja template mentions a collection) with the draft
  seeded through `RelatedValueResolver.DraftMembers / DraftOverrides`.
- New slot parameters / a missing override parameter are created at Apply (`CollectionStore.EnsureParameters`, outside the transaction).

### Stage 3 as built (2026-11-24)

- `JinjaTemplate.IObjectSource.RelatedMany(from, name)` returns `Member`s (handle, slot, id, stale, overrides) or null (no such
  collection / not an owner). Top-level names fall back to the rendered element's collections; any object's member access
  tries relation -> collection -> parameter (`Element.Signs`, `Type.Signs`).
- Lists skip empty slots and broken references (so `Signs[0]` = first filled slot; `s.slot` keeps the real slot). On its own
  a list prints the member names comma separated; `| size` counts.
- Seen from an instance of an owner type: the type's members with that instance's overrides (filtered to the overridable
  fields when listed); seen from the type (`Type.Signs`, or a rule rendering on the type): no overrides.
- Overrides replace every spelling of the field (case-insensitive); an override of a number stays a number when it parses.
- Dependencies: a Jinja rule mentioning a collection name (whole word) depends on the owner (type) and every resolved member.
  The instance's own override parameter is on the element itself. The index is rebuilt when the collections config changes.
  Not tracked: a new element taking a broken reference's name (Audit / window fixes those).

### Stage 2 as built (2026-11-24)

- Collection names are template identifiers (letter first, letters / digits / `_`, max 40) and can't shadow Element, Type,
  Level, Host, Room, Space, ToRoom, FromRoom, Group, Project, value, row, forloop, loop.
- A model's collection replaces the project's one of the same name; `enabled: false` there turns it off for that model.
- Server checks: `{n}` exactly once for slots; Revit-safe names; members != override parameter; overridable fields need an
  override parameter; parameter names unique across the merged set (incl. `Sign_{n}` vs a plain `Sign_1`) and not a
  Perseus keyed name; the owner family regex compiles.
- Plugin names in use: recorded under `collection.<name>.members:<base name>` (slots record the pattern) when Perseus creates
  the parameter. A prefix change keeps existing parameters; changing the base name in the config switches parameter (like
  an Auto Parameters rule).
- Existing parameters of the configured name and kind (type / instance) are used as they are (not locked); created ones are
  locked Perseus parameters. The override parameter is always an instance parameter on the owner categories.
- A slot / list value without an id (`ST07`, `|ST07`) is a name-only reference: resolved by name (stale, re-pointable), so
  packs typed by hand before collections can be adopted.
- A renamed member whose id is still valid is **stale** (templates keep using it): AcceptRename, or Repoint when exactly one
  other allowed element now has the stored name. An id that exists but is no longer an allowed member stays in templates
  but is **broken** unless one name match exists.

## Decisions (2026-11-23)

- Layers: server-wide -> project -> model (no organisation layer for now).
- Projects fork the convention on creation; migrations only on request.
- Slot parameters are created on demand.
