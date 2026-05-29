# GeoJSON Revit Augmentation Specification

This document describes how Project Perseus serialises Revit geometry as JSON, including the extensions it adds beyond the [RFC 7946 GeoJSON](https://datatracker.ietf.org/doc/html/rfc7946) standard. Any downstream consumer (e.g. GeoDjango, a web viewer) must implement a translation layer for the non-standard types described here.

---

## Coordinate System

Revit does **not** use geographic coordinates. All coordinate values in this output are in **Revit internal units (decimal feet)** relative to the project's internal origin. They are **not** WGS84 (latitude/longitude).

Conversion to a real-world CRS requires the project's survey point and true-north rotation, which are stored as Revit parameters and not embedded in the geometry output.

For GeoDjango: store coordinates in a projected CRS field (e.g. `PointField(srid=0)` for arbitrary Cartesian) rather than a geographic field.

---

## Standard GeoJSON Types Used

These follow RFC 7946 exactly and can be consumed by any compliant GeoJSON parser.

| type | geometry_type label | Description |
|---|---|---|
| `Point` | `location_point` | Placed location of an element with no rotation (or zero rotation). Coordinates are `[x, y, z]`. |
| `LineString` | `location_curve` | Location curve of a linear element (wall, beam, pipe, etc.). Arcs and splines are tessellated to a polyline. |
| `Polygon` | `room_boundary` | Room boundary loops. First ring is the exterior; subsequent rings are holes. Arcs tessellated to line segments. |

---

## Non-Standard Extension Types

These use a custom `type` string that is not in RFC 7946. A consuming application must detect and handle them explicitly.

### `RevitLocationPoint`

Used for point-placed elements that have a meaningful facing direction (e.g. doors, windows, equipment). Identical to a GeoJSON `Point` with one extra field.

```json
{
  "type": "RevitLocationPoint",
  "coordinates": [x, y, z],
  "rotation": 1.5707963
}
```

| field | type | description |
|---|---|---|
| `coordinates` | `[float, float, float]` | `[x, y, z]` in Revit internal feet, project CRS |
| `rotation` | `float` | Rotation in **radians**, counter-clockwise from the project X-axis in the horizontal plane |

**GeoDjango handling:** Deserialise `coordinates` as a `PointField`. Store `rotation` as a separate `FloatField` on the model. Do not pass to PostGIS directly — strip the non-standard fields first.

---

## Element Geometry Envelope

Each element in the Perseus payload may include a `geometries` array. If the element has no extractable geometry (system elements, view elements, etc.) the key is omitted entirely.

```json
{
  "element_id": 123456,
  "geometries": [
    {
      "geometry_type": "location_point",
      "geometry": {
        "type": "RevitLocationPoint",
        "coordinates": [10.5, 22.3, 0.0],
        "rotation": 0.7854
      }
    },
    {
      "geometry_type": "room_boundary",
      "geometry": {
        "type": "Polygon",
        "coordinates": [
          [[0,0,0],[10,0,0],[10,5,0],[0,5,0],[0,0,0]]
        ]
      }
    }
  ]
}
```

### `geometry_type` vocabulary

| value | standard? | description |
|---|---|---|
| `location_point` | yes (if `type=Point`) / no (if `type=RevitLocationPoint`) | Element's placed point. Standard Point when rotation=0, RevitLocationPoint otherwise. |
| `location_curve` | yes | Element's linear extent. Always a `LineString`. |
| `room_boundary` | yes | Room boundary polygon. Always a `Polygon`. First ring = exterior boundary, additional rings = holes/voids. |

---

## Notes on Polygon Winding Order

RFC 7946 requires exterior rings to be **counter-clockwise** and interior rings (holes) to be **clockwise** when viewed from above.

Revit's `GetBoundarySegments` returns segments in the order Revit internally stores them, which may not match RFC 7946 winding. GeoDjango/PostGIS will normalise winding order automatically when you call `GEOSGeometry(json).valid`. It is recommended to validate and normalise on ingest rather than relying on the winding from the plugin.

---

## Future Geometry Types (Not Yet Implemented)

These are planned but not currently emitted:

| geometry_type | description |
|---|---|
| `solid_faces` | Tessellated solid geometry of a 3D element (mesh of triangles) |
| `bounding_box` | Axis-aligned bounding box of an element's 3D geometry |
| `face_normal` | Normal vector of a face-hosted element's host face |
