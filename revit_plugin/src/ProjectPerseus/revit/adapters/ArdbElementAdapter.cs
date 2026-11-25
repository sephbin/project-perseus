using ProjectPerseus.revit.interfaces;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.revit.adapters
{
    public class ArdbElementAdapter: IArdbElement
    {
        private readonly ARDB.Element _element;

        public ArdbElementAdapter(ARDB.Element element)
        {
            _element = element;
        }

        public IArdbElementId Id => new ArdbElementIdAdapter(_element.Id);
        public string UniqueId => _element.UniqueId;
        public string Name => ElementNames.Of(_element);
        public IArdbParameterSet ParametersSet => new ArdbParameterSetAdapter(_element.Parameters);
        public ARDB.Category CategoryName => _element.Category;
    }
}