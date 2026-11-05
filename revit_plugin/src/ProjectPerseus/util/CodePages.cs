using System;
using System.Text;
using ProjectPerseus.logging;

namespace ProjectPerseus.util
{
    /// <summary>
    /// .NET 8 (Revit 2025+) ships only Unicode/ASCII/Latin-1 encodings by default, so libraries that ask for legacy
    /// code pages (EPPlus reading Excel zip entries asks for IBM437) throw "'IBM437' is not a supported encoding
    /// name". Registering CodePagesEncodingProvider once at startup restores them. .NET Framework (Revit 2024) has
    /// them built in, so the provider is looked up by reflection and skipped when absent.
    /// </summary>
    internal static class CodePages
    {
        internal static void Register()
        {
            try
            {
                var type = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages", false);
                var instance = type?.GetProperty("Instance")?.GetValue(null) as EncodingProvider;
                if (instance == null) return;
                Encoding.RegisterProvider(instance);
                Log.Info("[CodePages] registered legacy code page encodings");
            }
            catch (Exception ex)
            {
                Log.Warn($"[CodePages] could not register code page encodings: {ex.Message}");
            }
        }
    }
}
