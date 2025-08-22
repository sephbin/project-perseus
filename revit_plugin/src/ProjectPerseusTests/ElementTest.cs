using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using ProjectPerseus;
using ProjectPerseus.models;
using ProjectPerseus.revit.interfaces;
using ProjectPerseusTests.mocks;
using Element = ProjectPerseus.models.Element;
using StorageType = ProjectPerseus.revit.StorageType;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ElementTest
    {
        [TestMethod]
        public void TestFromArdbElementConversion()
        {
            var mockParameters = GetMockElement(out var mockElement);

            var element = new Element(mockElement);

            // Assert
            Assert.AreEqual(mockElement.Id.IntegerValue, element.Id, "Element IDs do not match");
            Assert.AreEqual(mockElement.UniqueId, element.UniqueId, "Element UniqueIds do not match");
            Assert.AreEqual(mockElement.Name, element.Name, "Element Names do not match");
            Assert.AreEqual(mockParameters.Count, element.Parameters.Count, "Parameter counts do not match");
            for (var i = 0; i < mockParameters.Count; i++)
            {
                // Assert.AreEqual(mockParameters[i].Guid, element.Parameters[i].Guid, "Parameter guids do not match");
                Assert.AreEqual(mockParameters[i].Definition?.Name, element.Parameters[i].Name,
                    "Parameter names do not match");

                Assert.AreEqual(mockParameters[i].Definition?.Name, element.Parameters[i].Name,
                    "Parameter names do not match");
                switch (mockParameters[i].StorageType)
                {
                    case StorageType.Double:
                        Assert.AreEqual(mockParameters[i].AsDouble(), ((ParameterBase)element.Parameters[i]).Value,
                            "Parameter values do not match");
                        break;
                    case StorageType.ElementId:
                        Assert.AreEqual(mockParameters[i].AsElementId().IntegerValue,
                            ((ParameterBase)element.Parameters[i]).Value, "Parameter values do not match");
                        break;
                    case StorageType.Integer:
                        Assert.AreEqual(mockParameters[i].AsInteger(), ((ParameterBase)element.Parameters[i]).Value,
                            "Parameter values do not match");
                        break;
                    case StorageType.String:
                        Assert.AreEqual(mockParameters[i].AsString(), ((ParameterBase)element.Parameters[i]).Value,
                            "Parameter values do not match");
                        break;
                    case StorageType.None:
                        Assert.AreEqual(null, ((ParameterBase)element.Parameters[i]).Value,
                            "Parameter values do not match");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                Assert.AreEqual(mockParameters[i].StorageType.ToString(), element.Parameters[i].ValueType,
                    "Parameter value types do not match");
            }
        }

        private static List<IArdbParameter> GetMockElement(out MockArdbElement mockElement)
        {
            var mockParameters = new List<IArdbParameter>
            {
                new MockArdbParameter("1", new MockArdbDefinition("Parameter1", "ParameterGroup1"), StorageType.Double, 1.23),
                new MockArdbParameter("2", new MockArdbDefinition("Parameter2", "ParameterGroup2"), StorageType.String, "Test"),
                new MockArdbParameter("3", null, StorageType.String, "Test"),
                new MockArdbParameter("4", new MockArdbDefinition("Parameter4", "ParameterGroup4"), StorageType.None, null)
            };

            Category categoryName = null; // TODO: setting CategoryName to null so this builds. Needs to be fixed.
            mockElement = new MockArdbElement(new MockArdbElementId(1), "UniqueId", "Name",
                new MockArdbParameterSet(mockParameters), categoryName);
            return mockParameters;
        }


        [TestMethod]
        public void TestFromArdbElementToDeltaConversion()
        {
            string expectedJson = @"{
                ""action"": ""Create"",
                ""element"": {
                    ""id"": 1,
                    ""unique_id"": ""UniqueId"",
                    ""name"": ""Name"",
                    ""parameters"": [
                        {
                            ""name"": ""Parameter1"",
                            ""value"": 1.23,
                            ""value_type"": ""Double""
                        },
                        {
                            ""name"": ""Parameter2"",
                            ""value"": ""Test"",
                            ""value_type"": ""String""
                        },
                        {
                            ""value"": ""Test"",
                            ""value_type"": ""String""
                        },
                        {
                            ""name"": ""Parameter4"",
                            ""value_type"": ""None""
                        }
                    ]
                }
            }";
            GetMockElement(out var mockElement);
            var elementDelta = new ElementDelta(ElementDelta.DeltaAction.Create, mockElement);
            var actualJson = Utl.SerializeToJson(elementDelta);
            
            var expectedToken = JToken.Parse(expectedJson);
            var actualToken = JToken.Parse(actualJson);

            Assert.IsTrue(JToken.DeepEquals(expectedToken, actualToken), "JSON does not match");
        }
    }
}