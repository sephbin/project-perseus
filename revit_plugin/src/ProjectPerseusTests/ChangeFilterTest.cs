using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ChangeFilterTest
    {
        private static ElementChange Mod(ChangeAspect a, long id = 1) =>
            new ElementChange { Kind = ChangeKind.Modified, Aspects = a, Id = id, Category = "Walls", Level = "L1", Family = "Basic Wall", Type = "200mm" };

        private static ElementReviewState State(string status = ReviewStatus.Unreviewed, params string[] assignees)
        {
            var s = new ElementReviewState { Status = status };
            foreach (var a in assignees) s.OpenActions.Add(new ReviewAction { Id = a, Type = "Review", Assignee = a });
            return s;
        }

        [TestMethod]
        public void EmptyFilterMatchesEverything()
        {
            Assert.IsTrue(new ChangeFilter().Matches(Mod(ChangeAspect.Moved), null));
        }

        [TestMethod]
        public void AspectFilterOnlyMatchesModifications()
        {
            var f = new ChangeFilter { Aspect = ChangeAspect.Moved };
            Assert.IsTrue(f.Matches(Mod(ChangeAspect.Moved | ChangeAspect.Type), State()));
            Assert.IsFalse(f.Matches(Mod(ChangeAspect.Type), State()));
            Assert.IsFalse(f.Matches(new ElementChange { Kind = ChangeKind.Added }, State()));
        }

        [TestMethod]
        public void NeedsAttentionIsUnreviewedOrQuery()
        {
            var f = new ChangeFilter { Status = ChangeFilter.NeedsAttention };
            Assert.IsTrue(f.Matches(Mod(ChangeAspect.Moved), State(ReviewStatus.Unreviewed)));
            Assert.IsTrue(f.Matches(Mod(ChangeAspect.Moved), State(ReviewStatus.Query)));
            Assert.IsFalse(f.Matches(Mod(ChangeAspect.Moved), State(ReviewStatus.Resolved)));
        }

        [TestMethod]
        public void AssigneeMeAndUnassigned()
        {
            var c = Mod(ChangeAspect.Moved);
            Assert.IsTrue(new ChangeFilter { Assignee = ChangeFilter.AssigneeMe, Me = "alice" }.Matches(c, State(ReviewStatus.Query, "ALICE")));
            Assert.IsFalse(new ChangeFilter { Assignee = ChangeFilter.AssigneeMe, Me = "alice" }.Matches(c, State(ReviewStatus.Query, "bob")));
            Assert.IsTrue(new ChangeFilter { Assignee = ChangeFilter.Unassigned }.Matches(c, State()));
            Assert.IsFalse(new ChangeFilter { Assignee = ChangeFilter.Unassigned }.Matches(c, State(ReviewStatus.Query, "bob")));
        }

        [TestMethod]
        public void InViewAndSearch()
        {
            var c = Mod(ChangeAspect.Parameters, 42);
            c.ParameterChanges.Add(new ParameterChange { Name = "Fire Rating", Old = "60", New = "120" });
            Assert.IsTrue(new ChangeFilter { InView = new HashSet<long> { 42 } }.Matches(c, State()));
            Assert.IsFalse(new ChangeFilter { InView = new HashSet<long>() }.Matches(c, State()));
            Assert.IsTrue(new ChangeFilter { Search = "fire" }.Matches(c, State()));
            Assert.IsTrue(new ChangeFilter { Search = "42" }.Matches(c, State()));
            var s = State();
            s.Events.Add(new ReviewEvent { Text = "check with the engineer" });
            Assert.IsTrue(new ChangeFilter { Search = "engineer" }.Matches(c, s));
            Assert.IsFalse(new ChangeFilter { Search = "nothing-like-this" }.Matches(c, s));
        }
    }
}
