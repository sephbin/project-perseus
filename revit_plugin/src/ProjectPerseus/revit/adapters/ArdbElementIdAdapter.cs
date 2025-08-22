using ProjectPerseus.revit.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbElementIdAdapter : IArdbElementId
    {
        private readonly ARDB.ElementId _elementId;

        public ArdbElementIdAdapter(ARDB.ElementId elementId)
        {
            _elementId = elementId;
        }

        public int IntegerValue => _elementId.IntegerValue;
    }
}