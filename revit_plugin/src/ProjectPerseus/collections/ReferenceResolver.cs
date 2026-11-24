using System;
using System.Collections.Generic;

namespace ProjectPerseus.collections
{
    public enum RefStatus { Empty, Ok, Stale, Broken }

    /// <summary>How a stale reference can be fixed: point it at the element now holding its name, or keep the element and
    /// write its new name.</summary>
    public enum RefFix { None, Repoint, AcceptRename }

    public class Resolution
    {
        public RefStatus Status;
        /// <summary>Element the templates use (0 = none): the id's element when it is still a valid member, else the
        /// single name match.</summary>
        public long ElementId;
        public RefFix Fix;
        /// <summary>Repoint: the element to point at. AcceptRename: the id itself.</summary>
        public long FixId;
        /// <summary>Repoint: its name; AcceptRename: the element's current name.</summary>
        public string FixName;
        public string Reason = "";
    }

    /// <summary>What the model says about an element id (from the Revit adapter).</summary>
    public struct TargetInfo
    {
        public bool Exists;
        public bool IsTarget; // an allowed member of the collection (category, type / instance)
        public string Name;

        public TargetInfo(bool exists, bool isTarget, string name)
        {
            Exists = exists;
            IsTarget = isTarget;
            Name = name ?? "";
        }
    }

    /// <summary>
    /// Pure resolution of a stored reference (REFERENCE_COLLECTIONS_PLAN.md "Resolving a reference"):
    ///   id exists, allowed, same name                   -> Ok
    ///   id exists + allowed, name differs               -> Stale (the element is still used): Repoint when exactly one other
    ///                                                      allowed element has the stored name, else AcceptRename
    ///   id missing / not allowed, one name match        -> Stale, the match is used, Repoint
    ///   id missing / not allowed, no or several matches -> Broken (an existing id element is still used)
    /// Names compare exactly (Revit names are case-sensitive in schedules). Tested in ReferenceCodecTest.cs.
    /// </summary>
    public static class ReferenceResolver
    {
        public static Resolution Resolve(MemberRef r, Func<long, TargetInfo> byId, Func<string, IReadOnlyList<long>> targetsByName)
        {
            if (r.IsEmpty) return new Resolution { Status = RefStatus.Empty };
            var info = r.Id > 0 ? byId(r.Id) : new TargetInfo(false, false, "");
            bool idValid = info.Exists && info.IsTarget;
            if (idValid && string.Equals(info.Name, r.Name, StringComparison.Ordinal))
                return new Resolution { Status = RefStatus.Ok, ElementId = r.Id };

            var matches = string.IsNullOrEmpty(r.Name) ? (IReadOnlyList<long>)Array.Empty<long>() : targetsByName(r.Name) ?? Array.Empty<long>();
            long other = 0;
            int otherCount = 0;
            foreach (long m in matches)
                if (m != r.Id) { other = m; otherCount++; }

            if (idValid)
            {
                if (otherCount == 1)
                    return new Resolution
                    {
                        Status = RefStatus.Stale, ElementId = r.Id, Fix = RefFix.Repoint, FixId = other, FixName = r.Name,
                        Reason = $"{r.Id} is now called '{info.Name}'; another element is called '{r.Name}'.",
                    };
                return new Resolution
                {
                    Status = RefStatus.Stale, ElementId = r.Id, Fix = RefFix.AcceptRename, FixId = r.Id, FixName = info.Name,
                    Reason = $"'{r.Name}' was renamed to '{info.Name}'.",
                };
            }

            string why = r.Id <= 0 ? "stored by name only" :
                         !info.Exists ? $"element {r.Id} no longer exists" :
                         $"element {r.Id} ('{info.Name}') is not an allowed member";
            if (otherCount == 1)
                return new Resolution
                {
                    Status = RefStatus.Stale, ElementId = other, Fix = RefFix.Repoint, FixId = other, FixName = r.Name,
                    Reason = $"{Cap(why)}; '{r.Name}' found as element {other}.",
                };
            return new Resolution
            {
                Status = RefStatus.Broken,
                ElementId = info.Exists ? r.Id : 0,
                Reason = otherCount == 0
                    ? $"{Cap(why)} and no allowed element is called '{r.Name}'."
                    : $"{Cap(why)} and {otherCount} allowed elements are called '{r.Name}'.",
            };
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
