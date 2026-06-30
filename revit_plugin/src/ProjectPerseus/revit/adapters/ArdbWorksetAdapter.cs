using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.revit.interfaces;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbWorksetAdapter : IArdbElement
    {
        private readonly Workset _workset;

        public ArdbWorksetAdapter(Workset workset)
        {
            _workset = workset;
        }

        public IArdbElementId Id => new WorksetIdAdapter(_workset.Id.IntegerValue);

        public string UniqueId => $"WORKSET-{_workset.Id.IntegerValue}";

        public string Name => _workset.Name;

        // Worksets have no Revit category
        public Category CategoryName => null;

        public IArdbParameterSet ParametersSet => new WorksetParameterSet(_workset);

        private class WorksetIdAdapter : IArdbElementId
        {
            public WorksetIdAdapter(int id) { Value = id; }
            public long Value { get; }
        }

        private class WorksetParameterSet : IArdbParameterSet, System.Collections.IEnumerable
        {
            private readonly Workset _ws;
            public WorksetParameterSet(Workset ws) { _ws = ws; }

            public IEnumerator<IArdbParameter> GetEnumerator()
            {
                yield return new SyntheticStringParam("Workset Kind", _ws.Kind.ToString());
                yield return new SyntheticStringParam("Is Editable", _ws.IsEditable.ToString());
                yield return new SyntheticStringParam("Is Open", _ws.IsOpen.ToString());
                if (!string.IsNullOrEmpty(_ws.Owner))
                    yield return new SyntheticStringParam("Owner", _ws.Owner);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private class SyntheticStringParam : IArdbParameter
        {
            private readonly string _name;
            private readonly string _value;

            public SyntheticStringParam(string name, string value) { _name = name; _value = value; }

            public string Guid => null;
            public long? DefinitionId => null;
            public IArdbDefinition Definition => new SimpleDefinition(_name);
            public StorageType StorageType => StorageType.String;
            public bool HasValue => true;
            public double AsDouble() => 0;
            public IArdbElementId AsElementId() => null;
            public int AsInteger() => 0;
            public string AsString() => _value;
            public string GetSpecType() => null;
        }

        private class SimpleDefinition : IArdbDefinition
        {
            public SimpleDefinition(string name) { Name = name; }
            public string Name { get; }
            public string ParameterGroup => "Workset Properties";
        }
    }
}
