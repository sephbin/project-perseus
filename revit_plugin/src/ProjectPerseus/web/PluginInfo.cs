using System;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace ProjectPerseus.web
{
    /// <summary>
    /// Who is calling the web app: Perseus version, Revit version, Windows user and machine, sent as headers on every
    /// request (X-Perseus-*) and a "ProjectPerseus/x.y.z" User-Agent, so the server can report who uses Perseus and
    /// on which version (django-perseus PluginClient). Init() is called from Plugin.OnStartup.
    /// </summary>
    public static class PluginInfo
    {
        public static string Version { get; private set; } =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        public static string RevitVersion { get; private set; } = "";

        public static void Init(string revitVersion, string pluginVersion)
        {
            RevitVersion = revitVersion ?? "";
            if (!string.IsNullOrEmpty(pluginVersion)) Version = pluginVersion;
        }

        public static string UserAgent => $"ProjectPerseus/{Version} (Revit {RevitVersion})";

        private static string Safe(string s) => (s ?? "").Replace("\r", "").Replace("\n", "");

        public static void Apply(HttpWebRequest request)
        {
            if (request == null) return;
            try
            {
                request.UserAgent = UserAgent;
                request.Headers["X-Perseus-Version"] = Version;
                request.Headers["X-Perseus-Revit"] = RevitVersion;
                request.Headers["X-Perseus-User"] = Safe(Environment.UserName);
                request.Headers["X-Perseus-Machine"] = Safe(Environment.MachineName);
            }
            catch { /* never block a request over its headers */ }
        }

        public static void Apply(HttpClient client)
        {
            if (client == null) return;
            try
            {
                var h = client.DefaultRequestHeaders;
                foreach (var name in new[] { "X-Perseus-Version", "X-Perseus-Revit", "X-Perseus-User", "X-Perseus-Machine" }) h.Remove(name);
                h.UserAgent.Clear();
                h.TryAddWithoutValidation("User-Agent", UserAgent);
                h.TryAddWithoutValidation("X-Perseus-Version", Version);
                h.TryAddWithoutValidation("X-Perseus-Revit", RevitVersion);
                h.TryAddWithoutValidation("X-Perseus-User", Safe(Environment.UserName));
                h.TryAddWithoutValidation("X-Perseus-Machine", Safe(Environment.MachineName));
            }
            catch { /* never block a request over its headers */ }
        }
    }
}
