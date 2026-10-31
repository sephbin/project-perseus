using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.util;

namespace ProjectPerseusTests
{
    [TestClass]
    public class PositionOrderTest
    {
        private static PositionSample S(long id, double x, double y, double z = 0) =>
            new PositionSample { Id = id, X = x, Y = y, Z = z };

        private static string Order(string spec, params PositionSample[] items)
        {
            var list = new List<PositionSample>(items);
            list.Sort(PositionOrder.Parse(spec));
            return string.Join(",", list.Select(s => s.Id));
        }

        [TestMethod]
        public void Parse_AcceptsValidAndRejectsInvalidSpecs()
        {
            Assert.IsTrue(PositionOrder.Parse("").IsDefault);
            Assert.IsTrue(PositionOrder.Parse(null).IsDefault);
            Assert.IsFalse(PositionOrder.Parse("X, -Y").IsDefault);
            Assert.IsNotNull(PositionOrder.Parse("-Y~500, X"));
            Assert.IsNotNull(PositionOrder.Parse("Id"));
            Assert.IsNull(PositionOrder.Parse("A"));
            Assert.IsNull(PositionOrder.Parse("X~0"));
            Assert.IsNull(PositionOrder.Parse("--Y"));
            Assert.IsNull(PositionOrder.Parse("X, Y, Z, Id, X"));   // more than 4 keys
        }

        [TestMethod]
        public void LeftToRightThenTopToBottom()
        {
            // 1 and 2 share X (a column); the one with larger Y is higher up, so it comes first.
            Assert.AreEqual("3,2,1", Order("X, -Y", S(1, 100, 0), S(2, 100, 50), S(3, 0, 10)));
        }

        [TestMethod]
        public void RowsFromTheTopEachLeftToRight()
        {
            // Bucketing makes 1000 and 1100 (mm) the same row; then X orders within the row.
            Assert.AreEqual("2,1,3", Order("-Y~500, X", S(1, 500, 1000), S(2, 100, 1100), S(3, 0, 0)));
        }

        [TestMethod]
        public void TiesFallBackToElementId()
        {
            Assert.AreEqual("1,2,3", Order("X", S(3, 5, 0), S(1, 5, 9), S(2, 5, 4)));
            Assert.AreEqual("1,2,3", Order("", S(3, 0, 0), S(1, 9, 9), S(2, 4, 4)));
        }

        [TestMethod]
        public void NegativeCoordinatesBucketConsistently()
        {
            // floor(-1/500) = -1 and floor(499/500) = 0, so these two are different rows; -1 sorts first ascending.
            Assert.AreEqual("1,2", Order("Y~500", S(2, 0, 499), S(1, 0, -1)));
        }
    }
}
