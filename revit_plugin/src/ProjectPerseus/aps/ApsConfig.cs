using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using ProjectPerseus.logging;

namespace ProjectPerseus.aps
{
    /// <summary>
    /// Autodesk Platform Services app settings at %AppData%\ProjectPerseus\aps.json: the APS app's client ID (a
    /// "Desktop, Mobile, Single-Page App" = public client, PKCE, no secret) and the loopback port of its callback URL,
    /// which must be registered in the app exactly as CallbackUrl. Used by ApsAuth to list / download cloud versions
    /// of linked models (federator version pickers).
    /// </summary>
    internal class ApsConfig
    {
        public string ClientId { get; set; }
        public int CallbackPort { get; set; } = 8765;

        [JsonIgnore] internal string CallbackUrl => $"http://localhost:{CallbackPort}/perseus/aps/callback";
        [JsonIgnore] internal bool IsSet => !string.IsNullOrWhiteSpace(ClientId);

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "aps.json");

        internal static ApsConfig Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<ApsConfig>(File.ReadAllText(FilePath)) ?? new ApsConfig();
            }
            catch (Exception ex) { Log.Info($"[APS] aps.json unreadable: {ex.Message}"); }
            return new ApsConfig();
        }

        internal void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
