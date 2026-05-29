using System.Collections.Generic;
using Newtonsoft.Json;

namespace ProjectPerseus.models.geometry
{
    public abstract class GeoJsonGeometry
    {
        [JsonProperty("type", Order = -2)]
        public abstract string Type { get; }
    }

    public class GeoJsonPoint : GeoJsonGeometry
    {
        public override string Type => "Point";

        [JsonProperty("coordinates")]
        public double[] Coordinates { get; }

        public GeoJsonPoint(double x, double y, double z) =>
            Coordinates = new[] { x, y, z };
    }

    public class GeoJsonLineString : GeoJsonGeometry
    {
        public override string Type => "LineString";

        [JsonProperty("coordinates")]
        public double[][] Coordinates { get; }

        public GeoJsonLineString(double[][] coords) => Coordinates = coords;
    }

    public class GeoJsonPolygon : GeoJsonGeometry
    {
        public override string Type => "Polygon";

        [JsonProperty("coordinates")]
        public double[][][] Coordinates { get; }

        public GeoJsonPolygon(double[][][] rings) => Coordinates = rings;
    }

    // Non-standard extension — see GEOJSON_REVIT_AUGMENTATION.md
    public class RevitLocationPoint : GeoJsonGeometry
    {
        public override string Type => "RevitLocationPoint";

        [JsonProperty("coordinates")]
        public double[] Coordinates { get; }

        // Rotation in radians, counter-clockwise from the project X-axis in the horizontal plane.
        [JsonProperty("rotation")]
        public double Rotation { get; }

        public RevitLocationPoint(double x, double y, double z, double rotation)
        {
            Coordinates = new[] { x, y, z };
            Rotation = rotation;
        }
    }

    public class NamedGeometry
    {
        [JsonProperty("geometry_type")]
        public string GeometryType { get; }

        [JsonProperty("geometry")]
        public GeoJsonGeometry Geometry { get; }

        public NamedGeometry(string geometryType, GeoJsonGeometry geometry)
        {
            GeometryType = geometryType;
            Geometry = geometry;
        }
    }
}
