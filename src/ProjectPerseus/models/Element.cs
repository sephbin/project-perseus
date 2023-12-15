using System;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.models
{
    public class Element
    {
        public int id { get; }
        public string uniqueId { get; }

        public string name { get; }
        public string comments { get; }

        private Element(int id,
            string uniqueId,
            string name,
            string comments)
        {
            this.id = id;
            this.uniqueId = uniqueId;
            this.name = name;
            this.comments = comments;
        }

        private const string CommentsParameterKey = "Comments";

        public static Element FromARDBElement(ARDB.Element element)
        {
            if (element is null) throw new ArgumentNullException(nameof(element));
            if (element.Id is null) throw new ArgumentNullException(nameof(element.Id));
            if (element.UniqueId is null) throw new ArgumentNullException(nameof(element.UniqueId));
            if (element.Name is null) throw new ArgumentNullException(nameof(element.Name));
            String comments = null;
            try
            {
                // if anything goes wrong here, swallow it and just don't set the comments
                // if (element.ParametersMap is null) throw new ArgumentNullException(nameof(element.ParametersMap));
                if (element.ParametersMap.Contains(CommentsParameterKey))
                    comments = element.ParametersMap.get_Item(CommentsParameterKey).AsString();
            }
            catch (Exception ex)
            {
            }

            return new Element(
                element.Id.IntegerValue,
                element.UniqueId,
                element.Name,
                comments);
        }
    }
}