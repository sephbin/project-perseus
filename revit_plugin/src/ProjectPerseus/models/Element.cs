using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus.revit;
using ProjectPerseus.models.geometry;
using ARDB = Autodesk.Revit.DB;

using ProjectPerseus;
using Autodesk.Revit.UI;
using Autodesk.Revit.Creation;

using ProjectPerseus.logging;
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
                Log.Info($"Element.GetGeometries [{_element.Id.Value}]: {ex.Message}");
                return null;
            }
        }

        private List<IParameter> GetParameters()
        {
            var paramList = new List<IParameter>();

            int rawParamCount = 0;
            foreach (var param in _element.ParametersSet)
            {
                rawParamCount++;
                try
                {
                    paramList.Add(ParameterBase.FromArdbParameter(_element.CategoryName, param));
                }
                catch (Exception ex)
                {
                    Log.Info($"Element.GetParameters [{_element.Id.Value}]: param#{rawParamCount} threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (rawParamCount == 0)
                Log.Debug($"Element.GetParameters [{_element.Id.Value}] '{_element.Name}': ParametersSet yielded 0 parameters");

            try
            {
                var rawId = RevitExtensions.CreateId(_element.Id.Value);
                var rawElem = _doc.GetElement(rawId);

                if (rawElem == null)
                {
                    Log.Debug($"Element.GetParameters [{_element.Id.Value}] '{_element.Name}': doc.GetElement returned null — skipping synthetic params");
                }
                else
                {
                    if (rawElem is ARDB.FamilyInstance fi)
                    {
                        // Host is not phase-dependent — no try/catch needed.
                        if (fi.Host != null)
                            paramList.Add(new Parameter<long>("Host Id", fi.Host.Id.GetIdValue(), "ElementId", null, "synthetic"));

                        // FromRoom / ToRoom / Room throw InvalidOperationException when the
                        // element does not exist in the active phase. Catch per-property so
                        // a missing phase membership only skips that one value, not the whole block.
                        try { if (fi.FromRoom != null) paramList.Add(new Parameter<long>("From Room", fi.FromRoom.Id.GetIdValue(), "ElementId", null, "synthetic")); }
                        catch (InvalidOperationException) { }

                        try { if (fi.ToRoom != null) paramList.Add(new Parameter<long>("To Room", fi.ToRoom.Id.GetIdValue(), "ElementId", null, "synthetic")); }
                        catch (InvalidOperationException) { }

                        try { if (fi.Room != null) paramList.Add(new Parameter<long>("Room", fi.Room.Id.GetIdValue(), "ElementId", null, "synthetic")); }
                        catch (InvalidOperationException) { }
                    }

                    // Object-class introspection: lets consumers distinguish instances from types
                    // (FamilySymbol, WallType, FloorType, etc.) and walk instance → type via Type Id.
                    // Type Id is -1 (InvalidElementId) when this element IS itself a type.
                    paramList.Add(new Parameter<bool>("Is FamilySymbol", rawElem is ARDB.FamilySymbol, "Boolean", null, "synthetic"));
                    paramList.Add(new Parameter<bool>("Is ElementType", rawElem is ARDB.ElementType, "Boolean", null, "synthetic"));
                    paramList.Add(new Parameter<bool>("Is ElementType (Subclass)", rawElem.GetType().IsSubclassOf(typeof(ARDB.ElementType)), "Boolean", null, "synthetic"));
                    paramList.Add(new Parameter<long>("Type Id", rawElem.GetTypeId().GetIdValue(), "ElementId", null, "synthetic"));
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Element.GetParameters [{_element.Id.Value}] '{_element.Name}': synthetic params threw {ex.GetType().Name}: {ex.Message}");
            }

            return paramList;
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
            //Log.Info("ParameterBase.CreateParameterName");
            //if (category == null) throw new ArgumentNullException(nameof(category));
            //Log.Info("ParameterBase.CreateParameterName - after if");
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