using System;
using System.Collections.Generic;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.models
{
    public class Element
    {
        public int id { get; }
        public string uniqueId { get; }
        public string name { get; }
        public List<ParameterBase> parameters { get; }

        private Element(int id, string uniqueId, string name, List<ParameterBase> parameters)
        {
            this.id = id;
            this.uniqueId = uniqueId;
            this.name = name;
            this.parameters = parameters;
        }

        public static Element FromARDBElement(ARDB.Element element)
        {
            if (element is null) throw new ArgumentNullException(nameof(element));
            var parameters = new List<ParameterBase>();
            foreach (ARDB.Parameter param in element.ParametersMap)
            {
                parameters.Add(ParameterBase.FromARDBParameter(param));
            }

            return new Element(element.Id.IntegerValue, element.UniqueId, element.Name, parameters);
        }
    }

    public class ParameterBase
    {
        public string name { get; protected set; }
        public object value { get; protected set; }
        public string valueType { get; protected set; }

        public static ParameterBase FromARDBParameter(ARDB.Parameter parameter)
        {
            if (parameter is null) throw new ArgumentNullException(nameof(parameter));
            var name = parameter.Definition.Name;
            var valueType = parameter.StorageType.ToString();
            switch (parameter.StorageType)
            {
                case ARDB.StorageType.Double:
                    return new Parameter<double>(name, parameter.AsDouble(), valueType);
                case ARDB.StorageType.ElementId:
                    return new Parameter<int>(name, parameter.AsElementId().IntegerValue, valueType);
                case ARDB.StorageType.Integer:
                    return new Parameter<int>(name, parameter.AsInteger(), valueType);
                case ARDB.StorageType.String:
                    return new Parameter<string>(name, parameter.AsString(), valueType);
                case ARDB.StorageType.None:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public class Parameter<T> : ParameterBase
    {
        public new T value { get; private set; }

        public Parameter(string name, T value, string valueType)
        {
            this.name = name;
            this.value = value;
            this.valueType = valueType;
        }
    }
}