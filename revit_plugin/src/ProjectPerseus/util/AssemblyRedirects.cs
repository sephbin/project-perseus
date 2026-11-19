using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Binding redirects for an add-in. Revit reads only Revit.exe.config, so when a library (Scriban) asks for an older
    /// version of a support assembly than the one shipped beside ProjectPerseus.dll (System.Threading.Tasks.Extensions
    /// 4.2.0.0 vs 4.2.0.1), .NET Framework fails the load. This hands back the loaded or shipped copy when it's the same
    /// assembly at the same or a newer version. Revit 2025+ (.NET 8) has these in the runtime. Installed in Plugin.OnStartup.
    /// </summary>
    internal static class AssemblyRedirects
    {
        private static readonly string[] Names =
        {
            "System.Threading.Tasks.Extensions", "System.Memory", "System.Buffers",
            "System.Runtime.CompilerServices.Unsafe", "System.Numerics.Vectors", "Microsoft.Bcl.AsyncInterfaces",
        };

        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var wanted = new AssemblyName(args.Name);
                if (!Names.Contains(wanted.Name, StringComparer.OrdinalIgnoreCase)) return null;

                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, wanted.Name, StringComparison.OrdinalIgnoreCase) &&
                                         (wanted.Version == null || a.GetName().Version >= wanted.Version));
                if (loaded != null) return loaded;

                string dir = Path.GetDirectoryName(typeof(AssemblyRedirects).Assembly.Location) ?? "";
                string path = Path.Combine(dir, wanted.Name + ".dll");
                if (!File.Exists(path)) return null;
                var shipped = AssemblyName.GetAssemblyName(path);
                return wanted.Version == null || shipped.Version >= wanted.Version ? Assembly.LoadFrom(path) : null;
            }
            catch
            {
                return null; // never let a resolve handler throw inside Revit
            }
        }
    }
}
