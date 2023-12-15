using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using ProjectPerseus.models;
using ProjectPerseus.models.interfaces;
using ProjectPerseusTests.mocks;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ElementTest
    {
        [TestMethod]
        public void TestFromArdbElementConversion()
        {
            // Arrange
            var mockParameters = new List<IArdbParameter>
            {
                new MockArdbParameter("Parameter1", StorageType.Double, 1.23),
                new MockArdbParameter("Parameter2", StorageType.String, "Test")
            };

            var mockElement = new MockArdbElement(new MockArdbElementId(1), "UniqueId", "Name", new MockArdbParameterSet(mockParameters));

            // Act
            var element = new Element(mockElement);

            // Assert
            Assert.AreEqual(mockElement.Id.IntegerValue, element.Id, "Element IDs do not match");
            Assert.AreEqual(mockElement.UniqueId, element.UniqueId, "Element UniqueIds do not match");
            Assert.AreEqual(mockElement.Name, element.Name, "Element Names do not match");
            Assert.AreEqual(mockParameters.Count, element.Parameters.Count, "Parameter counts do not match");
            for (var i = 0; i < mockParameters.Count; i++)
            {
                Assert.AreEqual(mockParameters[i].Definition.Name, element.Parameters[i].Name, "Parameter names do not match");
                
                Assert.AreEqual(mockParameters[i].Definition.Name, element.Parameters[i].Name, "Parameter names do not match");
                switch (mockParameters[i].StorageType)
                {
                    case StorageType.Double:
                        Assert.AreEqual(mockParameters[i].AsDouble(), ((ParameterBase)element.Parameters[i]).Value, "Parameter values do not match");
                        break;
                    case StorageType.ElementId:
                        Assert.AreEqual(mockParameters[i].AsElementId().IntegerValue, ((ParameterBase)element.Parameters[i]).Value, "Parameter values do not match");
                        break;
                    case StorageType.Integer:
                        Assert.AreEqual(mockParameters[i].AsInteger(), ((ParameterBase)element.Parameters[i]).Value, "Parameter values do not match");
                        break;
                    case StorageType.String:
                        Assert.AreEqual(mockParameters[i].AsString(), ((ParameterBase)element.Parameters[i]).Value, "Parameter values do not match");
                        break;
                    case StorageType.None:
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                Assert.AreEqual(mockParameters[i].StorageType.ToString(), element.Parameters[i].ValueType, "Parameter value types do not match");
            }
        }
    }
}