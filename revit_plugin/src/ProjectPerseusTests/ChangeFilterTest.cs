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
        public void TickListsMatchAnyTickedValue()
        {
            var moved = Mod(ChangeAspect.Moved);
            var param = Mod(ChangeAspect.Parameters);
            var added = new ElementChange { Kind = ChangeKind.Added, Category = "Doors" };

            var f = new ChangeFilter { Kinds = new HashSet<ChangeKind> { ChangeKind.Added, ChangeKind.Modified }, ModifiedAspects = ChangeAspect.Moved };
            Assert.IsTrue(f.Matches(moved, State()));
            Assert.IsFalse(f.Matches(param, State()));
            Assert.IsTrue(f.Matches(added, State()));
            Assert.IsFalse(new ChangeFilter { Kinds = new HashSet<ChangeKind>() }.Matches(added, State()));

            var cats = new ChangeFilter { Categories = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "walls" } };
            Assert.IsTrue(cats.Matches(moved, State()));
            Assert.IsFalse(cats.Matches(added, State()));
            Assert.IsTrue(new ChangeFilter { Levels = new HashSet<string> { ChangeFilter.NoValue } }.Matches(added, State()));

            var st = new ChangeFilter { Statuses = new HashSet<string> { ReviewStatus.Unreviewed, ReviewStatus.Query } };
            Assert.IsTrue(st.Matches(moved, State()));
            Assert.IsFalse(st.Matches(moved, State(ReviewStatus.Accepted)));

            var who = new ChangeFilter { Assignees = new HashSet<string> { ChangeFilter.AssigneeMe, ChangeFilter.Unassigned }, Me = "alice" };
            Assert.IsTrue(who.Matches(moved, State()));
            Assert.IsTrue(who.Matches(moved, State(ReviewStatus.Query, "alice")));
            Assert.IsFalse(who.Matches(moved, State(ReviewStatus.Query, "bob")));
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
