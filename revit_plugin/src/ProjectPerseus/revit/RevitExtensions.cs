using Autodesk.Revit.DB;

namespace ProjectPerseus.revit
{
    public static class RevitExtensions
    {
        /// <summary>
        /// Safely extracts the ElementId as a 64-bit long across all Revit versions.
        /// </summary>
        public static long GetIdValue(this ElementId id)
        {
#if REVIT2024_OR_GREATER
            // Revit 2024+ uses 64-bit longs natively
            return id.Value;
#else
			// Revit 2023 and older use 32-bit ints. We cast it up to a long.
			return (long)id.IntegerValue;
#endif
        }

        /// <summary>
        /// Safely creates an ElementId from a 64-bit long across all Revit versions.
        /// </summary>
        public static ElementId CreateId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
			// Downcast to int for older versions (assuming the ID isn't massive)
			return new ElementId((int)value);
#endif
        }
    }
}