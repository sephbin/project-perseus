using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.mapping;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ParamCompatTest
    {
        private static ParamRef P(string kind, string spec = null, string label = null, bool type = false, bool ro = false) =>
            new ParamRef { Name = kind + (spec ?? ""), Kind = kind, Spec = spec, SpecLabel = label, IsType = type, ReadOnly = ro };

        private const string Length = "autodesk.spec.aec:length-2.0.0", Length1 = "autodesk.spec.aec:length-1.0.0", Area = "autodesk.spec.aec:area-2.0.0";

        [TestMethod]
        public void TextNeverSilentlyBecomesANumber()
        {
            Assert.AreEqual(CompatLevel.Ok, ParamCompat.Check(P(ParamCompat.Measure, Length, "Length"), P(ParamCompat.Text)).Level);
            Assert.AreEqual(CompatLevel.Ok, ParamCompat.Check(P(ParamCompat.Integer), P(ParamCompat.Text)).Level);
            Assert.AreEqual(CompatLevel.Warn, ParamCompat.Check(P(ParamCompat.Text), P(ParamCompat.Number)).Level);
            Assert.AreEqual(CompatLevel.Warn, ParamCompat.Check(P(ParamCompat.Text), P(ParamCompat.YesNo)).Level);
        }

        [TestMethod]
        public void MeasuresNeedTheSameSpec()
        {
            Assert.AreEqual(CompatLevel.Ok, ParamCompat.Check(P(ParamCompat.Measure, Length), P(ParamCompat.Measure, Length1)).Level);   // versions differ
            Assert.AreEqual(CompatLevel.Error, ParamCompat.Check(P(ParamCompat.Measure, Length, "Length"), P(ParamCompat.Measure, Area, "Area")).Level);
            Assert.AreEqual(CompatLevel.Warn, ParamCompat.Check(P(ParamCompat.Measure, Length), P(ParamCompat.Number)).Level);
            Assert.AreEqual(CompatLevel.Error, ParamCompat.Check(P(ParamCompat.YesNo), P(ParamCompat.Measure, Length)).Level);
        }

        [TestMethod]
        public void ElementsOnlyAsTextAndReadOnlyTargetsRefused()
        {
            Assert.AreEqual(CompatLevel.Ok, ParamCompat.Check(P(ParamCompat.Element), P(ParamCompat.Text)).Level);
            Assert.AreEqual(CompatLevel.Error, ParamCompat.Check(P(ParamCompat.Element), P(ParamCompat.Integer)).Level);
            Assert.AreEqual(CompatLevel.Error, ParamCompat.Check(P(ParamCompat.Text), P(ParamCompat.Text, ro: true)).Level);
        }

        [TestMethod]
        public void TypeTargetFromInstanceSourceWarns()
        {
            Assert.AreEqual(CompatLevel.Warn, ParamCompat.Check(P(ParamCompat.Text), P(ParamCompat.Text, type: true)).Level);
            Assert.AreEqual(CompatLevel.Ok, ParamCompat.Check(P(ParamCompat.Text, type: true), P(ParamCompat.Text, type: true)).Level);
        }

        [TestMethod]
        public void PreviewCountsWhatMaps()
        {
            var values = new List<SourceValue>
            {
                new SourceValue { HasValue = true, Text = "120" },
                new SourceValue { HasValue = true, Text = "2400 mm" },
                new SourceValue { HasValue = true, Text = "FR60" },
                new SourceValue { HasValue = false },
            };
            var p = ParamCompat.Preview(P(ParamCompat.Text), P(ParamCompat.Number), values);
            Assert.AreEqual(4, p.Total);
            Assert.AreEqual(2, p.Converted);
            Assert.AreEqual(1, p.Failing);
            Assert.AreEqual(1, p.Empty);
            StringAssert.Contains(p.Problems[0], "FR60");
        }

        [TestMethod]
        public void ConversionsShowTheLocalValue()
        {
            Assert.AreEqual("Yes", ParamCompat.Convert(P(ParamCompat.YesNo), P(ParamCompat.Text), new SourceValue { HasValue = true, Number = 1, Text = "Yes" }).Shown);
            Assert.AreEqual("No", ParamCompat.Convert(P(ParamCompat.Text), P(ParamCompat.YesNo), new SourceValue { HasValue = true, Text = "false" }).Shown);
            Assert.AreEqual("3", ParamCompat.Convert(P(ParamCompat.Number), P(ParamCompat.Integer), new SourceValue { HasValue = true, Number = 2.6 }).Shown);
            Assert.AreEqual("2400 mm", ParamCompat.Convert(P(ParamCompat.Measure, Length), P(ParamCompat.Text), new SourceValue { HasValue = true, Number = 7.874, Display = 2400, Text = "2400 mm" }).Shown);
            Assert.IsFalse(ParamCompat.Convert(P(ParamCompat.Text), P(ParamCompat.Integer), new SourceValue { HasValue = true, Text = "abc" }).Ok);
        }

        [TestMethod]
        public void NewParameterGuidsFollowNameAndDataType()
        {
            Assert.AreEqual(ParamCompat.GuidFor("Fire Rating", Length), ParamCompat.GuidFor(" fire rating ", Length1));   // case, spaces, spec version
            Assert.AreNotEqual(ParamCompat.GuidFor("Fire Rating", Length), ParamCompat.GuidFor("Fire Rating", "autodesk.spec:spec.string-2.0.0"));
            Assert.AreEqual("text", ParamCompat.SpecForNew(P(ParamCompat.Element, "x"), "text"));
            Assert.AreEqual(Length, ParamCompat.SpecForNew(P(ParamCompat.Measure, Length), "text"));
        }

        [TestMethod]
        public void KeysSeparateSharedBuiltInAndNamed()
        {
            var a = new ParamRef { Name = "Fire Rating", Guid = "g1", Storage = "String" };
            var b = new ParamRef { Name = "Fire Rating", Storage = "String" };
            var c = new ParamRef { Name = "Fire Rating", Storage = "String", IsType = true };
            Assert.AreNotEqual(a.Key, b.Key);
            Assert.AreNotEqual(b.Key, c.Key);
        }
    }
}
