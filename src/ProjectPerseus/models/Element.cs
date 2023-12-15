using System;
using System.Collections.Generic;
using ProjectPerseus.models.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.models
{
    public class Element
    {
        private readonly IArdbElement _element;

        public Element(IArdbElement element)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
        }

        public int Id => _element.Id.IntegerValue;
        public string UniqueId => _element.UniqueId;
        public string Name => _element.Name;
        public List<IParameter> Parameters => GetParameters();

        private List<IParameter> GetParameters()
        {
            var parameters = new List<IParameter>();
            foreach (var param in _element.ParametersSet)
            {
                parameters.Add(ParameterBase.FromArdbParameter(param));
            }

            return parameters;
        }
    }

    public interface IParameter
    {
        string Name { get; }
        object Value { get; }
        string ValueType { get; }
    }

    public class ParameterBase : IParameter
    {
        public string Name { get; protected set; }
        public object Value { get; protected set; }
        public string ValueType { get; protected set; }

        public static ParameterBase FromArdbParameter(IArdbParameter parameter)
        {
            if (parameter is null) throw new ArgumentNullException(nameof(parameter));
            var name = parameter.Definition.Name;
            var valueType = parameter.StorageType.ToString();
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return new Parameter<double>(name, parameter.AsDouble(), valueType);
                case StorageType.ElementId:
                    return new Parameter<int>(name, parameter.AsElementId().IntegerValue, valueType);
                case StorageType.Integer:
                    return new Parameter<int>(name, parameter.AsInteger(), valueType);
                case StorageType.String:
                    return new Parameter<string>(name, parameter.AsString(), valueType);
                case StorageType.None:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public class Parameter<T> : ParameterBase
    {
        public Parameter(string name, T value, string valueType)
        {
            Name = name;
            Value = value;
            ValueType = valueType;
        }
    }
}