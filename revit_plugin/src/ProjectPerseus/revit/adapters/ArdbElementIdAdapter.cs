using ProjectPerseus.revit.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbElementIdAdapter : IArdbElementId
    {
        private readonly Autodesk.Revit.DB.ElementId _id;

        public ArdbElementIdAdapter(Autodesk.Revit.DB.ElementId id)
        {
            _id = id;
        }

        public long Value => _id.GetIdValue();
    }
}