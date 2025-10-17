using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus.revit;
using ARDB = Autodesk.Revit.DB;

using ProjectPerseus;
using Autodesk.Revit.UI;
using Autodesk.Revit.Creation;

namespace ProjectPerseus.models
{
    public class Element
    {
        private readonly IArdbElement _element;
        private readonly Autodesk.Revit.DB.Document _doc;
        private readonly string _docGuid;

        public Element(IArdbElement element, Autodesk.Revit.DB.Document doc, string docGuid)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _docGuid = docGuid ?? throw new ArgumentNullException(nameof(docGuid));
        }
        
        [JsonIgnore]
        public IArdbElement originalElement => _element;

        [JsonProperty("element_id")] public int Id => _element.Id.IntegerValue;

        [JsonProperty("unique_id")] public string UniqueId => _element.UniqueId;
        [JsonProperty("name")] public string Name => _element.Name;
        [JsonProperty("parameters")] public List<IParameter> Parameters => GetParameters();
        

        [JsonProperty("last_edited_by")] public string Username => Environment.UserName;


        //WTF is this for? Revit returns the same id for different files.
        //[JsonProperty("source_model")] public string SourceModel => _doc.ProjectInformation.UniqueId;
        //[JsonProperty("source_model")] public string SourceModel => _doc.GetCloudModelPath()?.GetModelGUID().ToString() ?? RevitFacade.GetDocumentVersionGuid(_doc).ToString();
        [JsonProperty("source_model")]
        public string SourceModel => _docGuid; //ModelGuidStorage.GetOrCreate(_doc);

        [JsonProperty("source_state")] public string SourceState => RevitFacade.GetDocumentVersionGuid(_doc).ToString();



        //[JsonProperty("category")] public string Category => _element.Category.Name;


        private List<IParameter> GetParameters()
        {
            //Utl.WriteLog("Element.GetParameters");
            //Utl.WriteLog(UniqueId);
            var parameters = new List<IParameter>();

            foreach (var param in _element.ParametersSet)
            {
                try
                {
                    parameters.Add(ParameterBase.FromArdbParameter(_element.CategoryName, param));
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Element.GetParameters: {ex.Message}"); 
                }
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
        [JsonProperty("name")] public string Name { get; protected set; }
        [JsonProperty("value")] public object Value { get; protected set; }
        [JsonProperty("value_type")] public string ValueType { get; protected set; }

        public static ParameterBase FromArdbParameter(ARDB.Category elementCategory, IArdbParameter parameter)
        {
            //Utl.WriteLog("ParameterBase.FromArdbParameter");
            if (parameter is null) throw new ArgumentNullException(nameof(parameter));
            //Utl.WriteLog("ParameterBase.FromArdbParameter - before CreateParameterName");
            var name = CreateParameterName(parameter.Definition?.Name, elementCategory, parameter.Definition?.ParameterGroup);
            //Utl.WriteLog("ParameterBase.FromArdbParameter - after CreateParameterName");
            //var name = elementCategory;
            var valueType = parameter.StorageType.ToString();

            //Utl.WriteLog("FromArdbParameter: name; " + name);
            //Utl.WriteLog("FromArdbParameter: valueType; " + valueType);

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
                    if (parameter.HasValue && parameter.Definition != null)
                        throw new ArgumentException(
                            "Parameter has a value and a definition, but the storage type is None.");
                    return new Parameter<string>(name, null, valueType);
                case StorageType.Null:
                    return new Parameter<string>(name, null, null);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static string CreateParameterName(string parameterName, ARDB.Category category, string parameterGroup)
        {
            //Utl.WriteLog("ParameterBase.CreateParameterName");
            //if (category == null) throw new ArgumentNullException(nameof(category));
            //Utl.WriteLog("ParameterBase.CreateParameterName - after if");
            return parameterName;

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