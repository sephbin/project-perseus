using System;
using System.Collections.Generic;
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
        private static readonly string K = ReviewLog.Key("link", "e1");

        private static ReviewEvent Ev(string id, string comparison, string element, int minutes, string status = null,
                                      string text = null, string author = "alice", string signature = null) =>
            new ReviewEvent
            {
                Id = id, ComparisonId = comparison, LinkInstanceUid = "link", LinkedElementUid = element,
                CreatedUtc = T0.AddMinutes(minutes), Status = status, Text = text, Author = author, Signature = signature,
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
            var s = ReviewLog.Fold(events, "c2")[K];
            Assert.AreEqual(ReviewStatus.Accepted, s.Status);
            Assert.AreEqual("bob", s.StatusBy);
            Assert.IsFalse(s.StatusCarried);
            Assert.AreEqual(1, s.CommentCount);
            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, s.Events.Select(e => e.Id).ToArray());
        }

        [TestMethod]
        public void StatusCarriesOverWhenTheElementHasNotChangedSince()
        {
            var events = new[] { Ev("1", "c1", "e1", 0, ReviewStatus.Accepted, signature: "sigA") };
            var s = ReviewLog.Fold(events, "c2", new Dictionary<string, string> { { K, "sigA" } })[K];
            Assert.AreEqual(ReviewStatus.Accepted, s.Status);
            Assert.IsTrue(s.StatusCarried);
            Assert.AreEqual("c1", s.StatusComparisonId);
        }

        [TestMethod]
        public void StatusDoesNotCarryOverWhenTheElementChangedAgainButCommentsDo()
        {
            var events = new[]
            {
                Ev("1", "c1", "e1", 0, ReviewStatus.Accepted, "fine last month", signature: "sigA"),
                Ev("2", "c2", "e1", 60, text: "moved again"),
            };
            var s = ReviewLog.Fold(events, "c2", new Dictionary<string, string> { { K, "sigB" } })[K];
            Assert.AreEqual(ReviewStatus.Unreviewed, s.Status);
            Assert.AreEqual(2, s.Events.Count);
        }

        [TestMethod]
        public void ActionsStayOpenUntilClosedAcrossComparisons()
        {
            var open1 = Ev("1", "c1", "e1", 0);
            open1.Action = new ReviewAction { Id = "a1", Type = "Approve", Assignee = "bob" };
            var open2 = Ev("2", "c2", "e1", 10);
            open2.Action = new ReviewAction { Id = "a2", Type = "Respond", Assignee = "Structural engineer" };
            var close1 = Ev("3", "c2", "e1", 20);
            close1.ClosesActionId = "a1";
            close1.CloseOutcome = ReviewAction.Done;

            var s = ReviewLog.Fold(new[] { open1, open2 }, "c2")[K];
            Assert.AreEqual(2, s.OpenActions.Count);
            Assert.AreEqual("bob, Structural engineer", s.Assignees);
            Assert.AreEqual("alice", s.OpenActions[0].CreatedBy);

            s = ReviewLog.Fold(new[] { open1, open2, close1 }, "c2")[K];
            Assert.AreEqual("a2", s.OpenActions.Single().Id);
            Assert.AreEqual("Structural engineer", s.Assignees);
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
