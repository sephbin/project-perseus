using System;
using System.Collections.Generic;
using Newtonsoft.Json;
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
        [JsonProperty("id")]
        public int Id => _element.Id.IntegerValue;
        [JsonProperty("unique_id")]
        public string UniqueId => _element.UniqueId;
        [JsonProperty("name")]
        public string Name => _element.Name;
        [JsonProperty("parameters")]
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
        [JsonProperty("name")]
        public string Name { get; protected set; }
        [JsonProperty("value")]
        public object Value { get; protected set; }
        [JsonProperty("value_type")]
        public string ValueType { get; protected set; }

        public static ParameterBase FromArdbParameter(IArdbParameter parameter)
        {
            if (parameter is null) throw new ArgumentNullException(nameof(parameter));
            var name = parameter.Definition?.Name;
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
                    // assert that the parameter.HasValue is false
                    if(parameter.HasValue && parameter.Definition != null)
                        throw new ArgumentException("Parameter has a value and a definition, but the storage type is None.");
                    return new Parameter<string>(name, null, valueType);
                case StorageType.Null:
                    return new Parameter<string>(name, null, null);
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