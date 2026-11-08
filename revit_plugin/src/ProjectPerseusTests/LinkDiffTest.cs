using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class LinkDiffTest
    {
        private const double Mm = 1.0 / 304.8;

        private static ElementSnapshot El(string uid, double x = 0, double y = 0, double size = 1,
                                          string type = "T1", params string[] kv)
        {
            var e = new ElementSnapshot
            {
                UniqueId = uid, Id = uid.GetHashCode(), Category = "Walls", Family = "Basic Wall", Type = type,
                Level = "L1", Min = new[] { x, y, 0.0 }, Max = new[] { x + size, y + size, size },
                Volume = size * size * size, Area = 6 * size * size, FaceCount = 6, EdgeCount = 12,
            };
            for (int i = 0; i + 1 < kv.Length; i += 2) e.Parameters[kv[i]] = kv[i + 1];
            return e;
        }

        private static LinkSnapshot Snap(params ElementSnapshot[] els) =>
            new LinkSnapshot { Elements = new List<ElementSnapshot>(els) };

        [TestMethod]
        public void AddedDeletedAndUnchanged()
        {
            var changes = LinkDiff.Compare(Snap(El("a"), El("b")), Snap(El("a"), El("c")));
            Assert.AreEqual(2, changes.Count);
            Assert.AreEqual("c", changes.Single(c => c.Kind == ChangeKind.Added).UniqueId);
            var del = changes.Single(c => c.Kind == ChangeKind.Deleted);
            Assert.AreEqual("b", del.UniqueId);
            Assert.IsNotNull(del.OldMin);
            Assert.IsNull(del.NewMin);
        }

        [TestMethod]
        public void MovedWithinToleranceIsNotAChange()
        {
            Assert.IsNull(LinkDiff.CompareElement(El("a"), El("a", x: 0.5 * Mm)));
            var c = LinkDiff.CompareElement(El("a"), El("a", x: 100 * Mm));
            Assert.AreEqual(ChangeAspect.Moved, c.Aspects);
            Assert.AreEqual(100 * Mm, c.MoveDistance, 1e-9);
        }

        [TestMethod]
        public void ResizedIsGeometryNotMoved()
        {
            // Grows about its min corner: centre moves too, so both aspects are reported.
            var c = LinkDiff.CompareElement(El("a", size: 1), El("a", size: 2));
            Assert.IsTrue(c.Aspects.HasFlag(ChangeAspect.Geometry));

            // Same box, different solid (e.g. an opening cut): geometry only.
            var o = El("a");
            var n = El("a");
            n.Volume = o.Volume * 0.9;
            n.FaceCount = 10;
            Assert.AreEqual(ChangeAspect.Geometry, LinkDiff.CompareElement(o, n).Aspects);
        }

        [TestMethod]
        public void CurveLengthChangeIsGeometry()
        {
            var o = El("w"); o.Location = new[] { 0.0, 0, 0, 10, 0, 0 };
            var n = El("w"); n.Location = new[] { -1.0, 0, 0, 11, 0, 0 }; // same midpoint, longer
            Assert.AreEqual(ChangeAspect.Geometry, LinkDiff.CompareElement(o, n).Aspects);
        }

        [TestMethod]
        public void TypeChangeRecordsOldType()
        {
            var c = LinkDiff.CompareElement(El("a", type: "200mm"), El("a", type: "300mm"));
            Assert.AreEqual(ChangeAspect.Type, c.Aspects);
            Assert.AreEqual("Basic Wall : 200mm", c.OldType);
            Assert.AreEqual("300mm", c.Type);
        }

        [TestMethod]
        public void ParameterChangesIgnoreBookkeepingAndEmptyVsMissing()
        {
            var o = El("a", kv: new[] { "Mark", "W1", "Edited by", "alice", "Comments", "" });
            var n = El("a", kv: new[] { "Mark", "W2", "Edited by", "bob", "Fire Rating", "60" });
            var c = LinkDiff.CompareElement(o, n);
            Assert.AreEqual(ChangeAspect.Parameters, c.Aspects);
            CollectionAssert.AreEqual(new[] { "Fire Rating", "Mark" }, c.ParameterChanges.Select(p => p.Name).ToArray());
            var fire = c.ParameterChanges.Single(p => p.Name == "Fire Rating");
            Assert.IsNull(fire.Old);
            Assert.AreEqual("60", fire.New);
        }

        [TestMethod]
        public void TypeParametersCanBeExcluded()
        {
            var o = El("a", kv: new[] { "[Type] Fire Rating", "60" });
            var n = El("a", kv: new[] { "[Type] Fire Rating", "120" });
            Assert.AreEqual(ChangeAspect.Parameters, LinkDiff.CompareElement(o, n).Aspects);
            Assert.IsNull(LinkDiff.CompareElement(o, n, new DiffOptions { CompareTypeParameters = false }));
        }

        [TestMethod]
        public void NullAndDuplicateInputsAreTolerated()
        {
            Assert.AreEqual(0, LinkDiff.Compare(null, null).Count);
            Assert.AreEqual(1, LinkDiff.Compare(null, Snap(El("a"), El("a"))).Count);
        }
    }
}
