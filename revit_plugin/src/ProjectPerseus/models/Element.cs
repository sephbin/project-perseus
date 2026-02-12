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


        [JsonProperty("last_edited_by")] public string Username => GetLastEditedBy();


        //WTF is this for? Revit returns the same id for different files.
        //[JsonProperty("source_model")] public string SourceModel => _doc.ProjectInformation.UniqueId;
        //[JsonProperty("source_model")] public string SourceModel => _doc.GetCloudModelPath()?.GetModelGUID().ToString() ?? RevitFacade.GetDocumentVersionGuid(_doc).ToString();
        [JsonProperty("source_model")]
        public string SourceModel => _docGuid; //ModelGuidStorage.GetOrCreate(_doc);

        [JsonProperty("source_state")] public string SourceState => RevitFacade.GetDocumentVersionGuid(_doc).ToString();



        //[JsonProperty("category")] public string Category => _element.Category.Name;

        private string GetLastEditedBy()
        {
            // 1. If not workshared, we cannot get history. Return current user.
            if (!_doc.IsWorkshared) return Environment.UserName;

            try
            {
                // 2. Convert the Adapter ID back to a native Revit ElementId
                var eid = new ARDB.ElementId(_element.Id.IntegerValue);

                // 3. Get Tooltip info (this contains LastChangedBy)
                var info = ARDB.WorksharingUtils.GetWorksharingTooltipInfo(_doc, eid);

                if (!string.IsNullOrEmpty(info.LastChangedBy))
                {
                    return info.LastChangedBy;
                }

                // Fallback to creator if LastChangedBy is somehow empty
                if (!string.IsNullOrEmpty(info.Creator))
                {
                    return info.Creator;
                }
            }
            catch
            {
                // Fails silently (e.g., if element is a Category or View that doesn't support worksharing info)
                // Returns default below
            }

            return Environment.UserName;
        }
        private List<IParameter> GetParameters()
        {
            // Use a Dictionary to enforce Unique Names
            // Key = Parameter Name, Value = The Parameter Object
            var paramDict = new Dictionary<string, IParameter>();

            foreach (var param in _element.ParametersSet)
            {
                try
                {
                    var newParam = ParameterBase.FromArdbParameter(_element.CategoryName, param);
                    string pName = newParam.Name;

                    // If this is a NEW parameter name, just add it.
                    if (!paramDict.ContainsKey(pName))
                    {
                        paramDict.Add(pName, newParam);
                    }
                    // If we have a COLLISION (Duplicate Name), decide which one to keep.
                    else
                    {
                        var existingParam = paramDict[pName];

                        // LOGIC: Prefer ElementId (The Link) over String (The Text)
                        // If the NEW one is an Int/ElementId and the OLD one is a String, overwrite it.
                        if (newParam.ValueType.Contains("ElementId") && existingParam.ValueType.Contains("String"))
                        {
                            paramDict[pName] = newParam;
                        }
                        // (Otherwise, keep the existing one)
                    }
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Element.GetParameters: {ex.Message}");
                }
            }
            try
            {
                // We need the raw native element to access .ToRoom / .FromRoom properties
                var rawId = new ARDB.ElementId(_element.Id.IntegerValue);
                var rawElem = _doc.GetElement(rawId);

                if (rawElem is ARDB.FamilyInstance fi)
                {
                    // --- Inject "From Room" ---
                    if (fi.FromRoom != null)
                    {
                        var p = new Parameter<int>("From Room", fi.FromRoom.Id.IntegerValue, "ElementId");
                        // We use indexer [] to overwrite if a parameter named "From Room" already exists (Revit properties > Params)
                        paramDict["From Room"] = p;
                    }

                    // --- Inject "To Room" ---
                    if (fi.ToRoom != null)
                    {
                        var p = new Parameter<int>("To Room", fi.ToRoom.Id.IntegerValue, "ElementId");
                        paramDict["To Room"] = p;
                    }

                    // --- Inject "Room" (General placement for Furniture etc) ---
                    // Note: fi.Room is phase-dependent. If null, we skip.
                    if (fi.Room != null)
                    {
                        var p = new Parameter<int>("Room", fi.Room.Id.IntegerValue, "ElementId");
                        if (!paramDict.ContainsKey("Room")) paramDict["Room"] = p;
                    }
                }
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Element.GetParameters (Virtual Injection): {ex.Message}");
            }

            // Convert the Dictionary values back to a List
            return new List<IParameter>(paramDict.Values);
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

            if (parameter is null) throw new ArgumentNullException(nameof(parameter));

            var name = CreateParameterName(parameter.Definition?.Name, elementCategory, parameter.Definition?.ParameterGroup);

            var valueType = parameter.StorageType.ToString();

            // If it is a number, try to find out WHAT KIND of number (Length, Area, etc.)
            if (parameter.StorageType == StorageType.Double)
            {
                string spec = parameter.GetSpecType();

                // If we found a valid spec (e.g., "autodesk.spec.aec:length-2.0.0" or "Length")
                // Use that as the value_type instead of the generic "Double"
                if (!string.IsNullOrEmpty(spec))
                {
                    valueType = spec;
                }
            }



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