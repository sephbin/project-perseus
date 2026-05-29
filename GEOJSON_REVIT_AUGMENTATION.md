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
| `Point` | *(unused — see RevitTransform)* | Standard GeoJSON point. Not currently emitted for location_point; all placed elements use RevitTransform. |
| `LineString` | `location_curve` | Location curve of a linear element (wall, beam, pipe, etc.). Arcs and splines are tessellated to a polyline. |
| `Polygon` | `room_boundary` | Room boundary loops. First ring is the exterior; subsequent rings are holes. Arcs tessellated to line segments. |

---

## Non-Standard Extension Types

These use a custom `type` string that is not in RFC 7946. A consuming application must detect and handle them explicitly.

### `RevitTransform`

Used for all point-placed elements (doors, windows, equipment, columns, etc.). Encodes the full placement as an origin point plus three orthogonal basis vectors — equivalent to a 4×4 affine transformation matrix with no scale.

A simple point + Z-rotation is insufficient for elements hosted on non-horizontal faces (e.g. a window in a sloped wall, a light fixture on a raked ceiling). `RevitTransform` is derived from `FamilyInstance.GetTotalTransform()` for family instances, and constructed from `LocationPoint` + rotation for non-family elements.

```json
{
  "type": "RevitTransform",
  "origin":  [x, y, z],
  "basis_x": [dx, dy, dz],
  "basis_y": [dx, dy, dz],
  "basis_z": [dx, dy, dz]
}
```

| field | type | description |
|---|---|---|
| `origin` | `[float, float, float]` | Position in Revit internal feet, project CRS |
| `basis_x` | `[float, float, float]` | Element's local X axis (facing/hand direction), unit vector |
| `basis_y` | `[float, float, float]` | Element's local Y axis, unit vector |
| `basis_z` | `[float, float, float]` | Element's local Z axis / face normal, unit vector |

The three basis vectors are mutually orthogonal unit vectors forming a right-handed coordinate system. To reconstruct a 4×4 column-major matrix (e.g. for WebGL / Three.js):

```
| basis_x[0]  basis_y[0]  basis_z[0]  origin[0] |
| basis_x[1]  basis_y[1]  basis_z[1]  origin[1] |
| basis_x[2]  basis_y[2]  basis_z[2]  origin[2] |
| 0           0           0           1         |
```

**GeoDjango handling:** Deserialise `origin` as a `PointField` (3D). Store `basis_x`, `basis_y`, `basis_z` as three `ArrayField(FloatField(), size=3)` columns on the model. Do not pass the full object to PostGIS directly — extract `origin` first.

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
        "type": "RevitTransform",
        "origin":  [10.5, 22.3, 0.0],
        "basis_x": [0.707, 0.707, 0.0],
        "basis_y": [-0.707, 0.707, 0.0],
        "basis_z": [0.0, 0.0, 1.0]
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
| `location_point` | no | Element's placed point and orientation. Always `type=RevitTransform`. |
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
