using System.Collections.Generic;
using System.Linq;
using ARDB = Autodesk.Revit.DB;

namespace RevitSyncPlugin
{
    public class CategoryDataExtractor
    {
        private readonly ARDB.Document _doc;

        public CategoryDataExtractor(ARDB.Document doc)
        {
            _doc = doc;
        }

        public List<ARDB.Category> Extract()
        {
            // Derived from
            // https://github.com/mcneel/rhino.inside-revit/blob/8f08fff3cd9ed3955463084d9027c1eaa3972af0/src/RhinoInside.Revit.External/DB/Extensions/Document.cs#L670
            using (var collector = new ARDB.FilteredElementCollector(_doc).OfClass(typeof(ARDB.GraphicsStyle)))
            {
                var ardbCategories =
                    collector.Cast<ARDB.GraphicsStyle>().Select(x => x.GraphicsStyleCategory).Where(x =>
                        x.Id != ARDB.ElementId.InvalidElementId && x.Name != string.Empty);


                // _FullName = value?.FullName();
                // _CategoryType = value?.CategoryType;
                // _IsTagCategory = value?.IsTagCategory;
                // _IsSubcategory = value?.Parent is object;
                // _CanAddSubcategory = value?.CanAddSubcategory;
                // _AllowsBoundParameters = value?.AllowsBoundParameters;
                // _HasMaterialQuantities = value?.HasMaterialQuantities;
                // _IsCuttable = value?.IsCuttable;

                // if (parentId is object)
                //     categories = categories.Where(x => parentId == (x.Parent?.Id ?? ElementId.InvalidElementId));
                //
                // return new HashSet<Category>(categories, CategoryEqualityComparer.SameDocument);
                return new HashSet<ARDB.Category>(ardbCategories, new SameDocumentComparer()).ToList();
                // .Select(x => new Category(x)).ToList()
                // CategoriesToJsonFile(categories);
            }
        }

        // From: https://github.com/mcneel/rhino.inside-revit/blob/4467f4c2aaff00e37ef73a5dbc52d95e81424758/src/RhinoInside.Revit.External/DB/Extensions/Category.cs#L22
        struct SameDocumentComparer : IEqualityComparer<ARDB.Category>
        {
            bool IEqualityComparer<ARDB.Category>.Equals(ARDB.Category x, ARDB.Category y) =>
                ReferenceEquals(x, y) || x?.Id == y?.Id;

            int IEqualityComparer<ARDB.Category>.GetHashCode(ARDB.Category obj) => obj?.Id.GetHashCode() ?? 0;
        }

        // private class Category
        // {
        //     public int Id { get; } // todo: this can change apparently
        //
        //     public string Name { get; }
        //
        //     public ARDB.CategoryType CategoryType { get; }
        //
        //     public bool IsTagCategory { get; }
        //
        //     public bool IsSubcategory { get; }
        //
        //     public bool CanAddSubcategory { get; }
        //
        //     public bool AllowsBoundParameters { get; }
        //
        //     public bool HasMaterialQuantities { get; }
        //
        //     public bool IsCuttable { get; }
        //
        //     private Category(int id,
        //         string name,
        //         ARDB.CategoryType categoryType,
        //         bool isTagCategory,
        //         bool isSubcategory,
        //         bool canAddSubcategory,
        //         bool allowsBoundParameters,
        //         bool hasMaterialQuantities,
        //         bool isCuttable)
        //     {
        //         Id = id;
        //         Name = name;
        //         CategoryType = categoryType;
        //         IsTagCategory = isTagCategory;
        //         IsSubcategory = isSubcategory;
        //         CanAddSubcategory = canAddSubcategory;
        //         AllowsBoundParameters = allowsBoundParameters;
        //         HasMaterialQuantities = hasMaterialQuantities;
        //         IsCuttable = isCuttable;
        //     }
        //
        //     public Category(ARDB.Category category) : this(
        //         category.Id.IntegerValue,
        //         category.Name,
        //         category.CategoryType,
        //         category.IsTagCategory,
        //         category.Parent is object,
        //         category.CanAddSubcategory,
        //         category.AllowsBoundParameters,
        //         category.HasMaterialQuantities,
        //         category.IsCuttable)
        //     {
        //     }
        // }

        // private static void CategoriesToJsonFile(List<Category> categories)
        // {
        //     Utl.PrettyWriteJson(categories, "./categories.json", null);
        // }
    }
}