using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ReviewLogTest
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

        private static ReviewEvent Ev(string id, string comparison, string element, int minutes, string status = null,
                                      string text = null, string author = "alice") =>
            new ReviewEvent
            {
                Id = id, ComparisonId = comparison, LinkInstanceUid = "link", LinkedElementUid = element,
                CreatedUtc = T0.AddMinutes(minutes), Status = status, Text = text, Author = author,
            };

        [TestMethod]
        public void LatestStatusInTheViewedComparisonWins()
        {
            var events = new[]
            {
                Ev("3", "c2", "e1", 30, ReviewStatus.Accepted, author: "bob"),
                Ev("1", "c2", "e1", 10, ReviewStatus.Query),
                Ev("2", "c2", "e1", 20, text: "asked the engineer"),
            };
            var s = ReviewLog.Fold(events, "c2")[ReviewLog.Key("link", "e1")];
            Assert.AreEqual(ReviewStatus.Accepted, s.Status);
            Assert.AreEqual("bob", s.StatusBy);
            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, s.Events.Select(e => e.Id).ToArray());
        }

        [TestMethod]
        public void StatusFromAnotherComparisonDoesNotCarryOverButCommentsDo()
        {
            var events = new[]
            {
                Ev("1", "c1", "e1", 0, ReviewStatus.Accepted, "fine last month"),
                Ev("2", "c2", "e1", 60, text: "moved again"),
            };
            var s = ReviewLog.Fold(events, "c2")[ReviewLog.Key("link", "e1")];
            Assert.AreEqual(ReviewStatus.Unreviewed, s.Status);
            Assert.AreEqual(2, s.Events.Count);
        }

        [TestMethod]
        public void SameTimestampOrdersById()
        {
            var events = new[] { Ev("b", "c", "e", 0, ReviewStatus.Ignored), Ev("a", "c", "e", 0, ReviewStatus.Query) };
            Assert.AreEqual(ReviewStatus.Ignored, ReviewLog.Fold(events, "c")[ReviewLog.Key("link", "e")].Status);
        }

        [TestMethod]
        public void NullEventsGiveEmptyState()
        {
            Assert.AreEqual(0, ReviewLog.Fold(null, "c").Count);
            Assert.AreEqual(0, ReviewLog.Fold(new ReviewEvent[] { null }, "c").Count);
        }
    }
}
