using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using ProjectPerseus;

namespace ProjectPerseusTests
{
    [TestClass]
    public class UtlTest
    {
        [TestMethod]
        public void TestIsValidUrl()
        {
            Assert.IsTrue(Utl.IsValidUrl("https://www.google.com"));
            Assert.IsTrue(Utl.IsValidUrl("http://www.google.com"));
            Assert.IsFalse(Utl.IsValidUrl("www.google.com"));
            Assert.IsFalse(Utl.IsValidUrl("google.com"));
            Assert.IsFalse(Utl.IsValidUrl("google"));
            Assert.IsFalse(Utl.IsValidUrl(""));
            Assert.IsFalse(Utl.IsValidUrl(null));
        }
    }
}
