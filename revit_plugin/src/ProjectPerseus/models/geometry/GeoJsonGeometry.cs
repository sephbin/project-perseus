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
    // Encodes a full Revit placement transform (origin + 3 orthogonal basis vectors).
    // Handles face-hosted elements on non-horizontal planes correctly, unlike a point+rotation.
    public class RevitTransform : GeoJsonGeometry
    {
        public override string Type => "RevitTransform";

        [JsonProperty("origin")]
        public double[] Origin { get; }

        [JsonProperty("basis_x")]
        public double[] BasisX { get; }

        [JsonProperty("basis_y")]
        public double[] BasisY { get; }

        [JsonProperty("basis_z")]
        public double[] BasisZ { get; }

        public RevitTransform(double[] origin, double[] basisX, double[] basisY, double[] basisZ)
        {
            Origin = origin;
            BasisX = basisX;
            BasisY = basisY;
            BasisZ = basisZ;
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
