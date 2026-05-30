using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.util;

namespace ProjectPerseusTests
{
    [TestClass]
    public class UtlTest
    {
        [TestMethod]
        public void TestIsValidUrl()
        {
            Assert.IsTrue(UrlUtils.IsValidUrl("https://www.google.com"));
            Assert.IsTrue(UrlUtils.IsValidUrl("http://www.google.com"));
            Assert.IsFalse(UrlUtils.IsValidUrl("www.google.com"));
            Assert.IsFalse(UrlUtils.IsValidUrl("google.com"));
            Assert.IsFalse(UrlUtils.IsValidUrl("google"));
            Assert.IsFalse(UrlUtils.IsValidUrl(""));
            Assert.IsFalse(UrlUtils.IsValidUrl(null));
        }

        private class TestSerializeToStringObject
        {
            public string Field1 { get; set; }
            public int Field2 { get; set; }

            public TestSerializeToStringObject(string field1, int field2)
            {
                Field1 = field1;
                Field2 = field2;
            }
        }

        [TestMethod]
        public void TestSerializeToString()
        {
            var obj = new TestSerializeToStringObject("test", 1);
            var jsonString = JsonUtils.SerializeToJson(obj, null);
            Assert.AreEqual("{\r\n  \"Field1\": \"test\",\r\n  \"Field2\": 1\r\n}", jsonString);
        }
    }
}

