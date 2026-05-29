using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus.revit;
using ProjectPerseus.models.geometry;
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

        [JsonProperty("element_id")] public long Id => _element.Id.Value;

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

        [JsonProperty("geometries", NullValueHandling = NullValueHandling.Ignore)]
        public List<NamedGeometry> Geometries => GetGeometries();



        //[JsonProperty("category")] public string Category => _element.Category.Name;

        private string GetLastEditedBy()
        {
            // 1. If the model isn't workshared, there is no history to get.
            if (!_doc.IsWorkshared) return null;

            try
            {
                var eid = RevitExtensions.CreateId(_element.Id.Value);
                var rawElem = _doc.GetElement(eid);

                // 2. Skip System Elements: If it doesn't have a valid Workset, it has no history.
                if (rawElem == null || rawElem.WorksetId == ARDB.WorksetId.InvalidWorksetId)
                {
                    return null;
                }

                // 3. The Workset Trap: If the workset is closed, Revit cannot read the tooltip.
                var workset = _doc.GetWorksetTable().GetWorkset(rawElem.WorksetId);
                if (workset != null && !workset.IsOpen)
                {
                    return null;
                }

                // 4. Fetch the true historical tooltip
                var info = ARDB.WorksharingUtils.GetWorksharingTooltipInfo(_doc, eid);

                if (!string.IsNullOrEmpty(info.LastChangedBy))
                {
                    return info.LastChangedBy;
                }

                if (!string.IsNullOrEmpty(info.Creator))
                {
                    return info.Creator;
                }
            }
            catch
            {
                // Catch edge-case API exceptions without crashing the serialization
            }

            // 5. If Revit genuinely has no history for this element, return null 
            return null;
        }
        private List<NamedGeometry> GetGeometries()
        {
            try
            {
                var rawId = RevitExtensions.CreateId(_element.Id.Value);
                var rawElem = _doc.GetElement(rawId);
                return ElementGeometryExtractor.Extract(rawElem);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Element.GetGeometries [{_element.Id.Value}]: {ex.Message}");
                return null;
            }
        }

        private List<IParameter> GetParameters()
        {
            // Key = (name, definitionId) so same-name built-in params with different
            // BuiltInParameter enum values are kept as distinct entries rather than
            // non-deterministically collapsing into one.
            var paramDict = new Dictionary<(string, string), IParameter>();

            foreach (var param in _element.ParametersSet)
            {
                try
                {
                    var newParam = ParameterBase.FromArdbParameter(_element.CategoryName, param);
                    string pName = newParam.Name;

                    if (string.IsNullOrEmpty(pName)) continue;

                    var key = (pName, newParam.ParamId);

                    if (!paramDict.ContainsKey(key))
                    {
                        paramDict.Add(key, newParam);
                    }
                    else
                    {
                        var existingParam = paramDict[key];
                        // Prefer ElementId over String for exact-key collisions
                        if (newParam.ValueType.Contains("ElementId") && existingParam.ValueType.Contains("String"))
                            paramDict[key] = newParam;
                    }
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Element.GetParameters: {ex.Message}");
                }
            }

            try
            {
                var rawId = RevitExtensions.CreateId(_element.Id.Value);
                var rawElem = _doc.GetElement(rawId);

                if (rawElem is ARDB.FamilyInstance fi)
                {
                    if (fi.Host != null)
                        paramDict[("Host Id", null)] = new Parameter<long>("Host Id", fi.Host.Id.GetIdValue(), "ElementId", null, "synthetic");

                    if (fi.FromRoom != null)
                        paramDict[("From Room", null)] = new Parameter<long>("From Room", fi.FromRoom.Id.GetIdValue(), "ElementId", null, "synthetic");

                    if (fi.ToRoom != null)
                        paramDict[("To Room", null)] = new Parameter<long>("To Room", fi.ToRoom.Id.GetIdValue(), "ElementId", null, "synthetic");

                    if (fi.Room != null && !paramDict.ContainsKey(("Room", null)))
                        paramDict[("Room", null)] = new Parameter<long>("Room", fi.Room.Id.GetIdValue(), "ElementId", null, "synthetic");
                }
            }
            catch
            {
                // "target instance does not exist in the given phase" is normal for
                // FamilyInstances not present in the active phase — skip silently.
            }

            // For each group of same-name params, drop null-valued entries when any
            // valued alternative exists — eliminates the None vs value false-change
            // caused by duplicate built-in params (e.g. two "Category" params).
            return paramDict.Values
                .GroupBy(p => p.Name)
                .SelectMany(g =>
                {
                    var valued = g.Where(p => p.Value != null).ToList();
                    return valued.Count > 0 ? valued : g.ToList();
                })
                .ToList();
        }
    }

    public interface IParameter
    {
        string Name { get; }
        object Value { get; }
        string ValueType { get; }
        string ParamId { get; }
        string ParamIdType { get; }
    }

    public class ParameterBase : IParameter
    {
        [JsonProperty("name")] public string Name { get; protected set; }
        [JsonProperty("value")] public object Value { get; protected set; }
        [JsonProperty("value_type")] public string ValueType { get; protected set; }
        [JsonProperty("param_id")] public string ParamId { get; protected set; }
        [JsonProperty("param_id_type")] public string ParamIdType { get; protected set; }

        public static ParameterBase FromArdbParameter(ARDB.Category elementCategory, IArdbParameter parameter)
        {

            if (parameter is null) throw new ArgumentNullException(nameof(parameter));

            var name = CreateParameterName(parameter.Definition?.Name, elementCategory, parameter.Definition?.ParameterGroup);

            string paramId;
            string paramIdType;
            if (parameter.Guid != null)
            {
                paramId = parameter.Guid;
                paramIdType = "shared";
            }
            else if (parameter.DefinitionId.HasValue)
            {
                paramId = parameter.DefinitionId.Value.ToString();
                paramIdType = parameter.DefinitionId.Value < 0 ? "builtin" : "project";
            }
            else
            {
                paramId = null;
                paramIdType = "synthetic";
            }

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
                    return new Parameter<double>(name, parameter.AsDouble(), valueType, paramId, paramIdType);
                case StorageType.ElementId:
                    return new Parameter<long>(name, parameter.AsElementId().Value, valueType, paramId, paramIdType);
                case StorageType.Integer:
                    return new Parameter<int>(name, parameter.AsInteger(), valueType, paramId, paramIdType);
                case StorageType.String:
                    return new Parameter<string>(name, parameter.AsString(), valueType, paramId, paramIdType);

                case StorageType.None:
                    if (parameter.HasValue && parameter.Definition != null)
                        throw new ArgumentException(
                            "Parameter has a value and a definition, but the storage type is None.");
                    return new Parameter<string>(name, null, valueType, paramId, paramIdType);
                case StorageType.Null:
                    return new Parameter<string>(name, null, null, null, "synthetic");
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
        public Parameter(string name, T value, string valueType, string paramId = null, string paramIdType = "synthetic")
        {
            Name = name;
            Value = value;
            ValueType = valueType;
            ParamId = paramId;
            ParamIdType = paramIdType;
        }
    }
}