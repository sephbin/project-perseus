using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ProjectPerseus.auth
{
    // DPAPI-encrypted PAT stored in %AppData%\ProjectPerseus\pat.bin. When present,
    // AuthService.GetTokenAsync returns it before checking any server-driven auth flow,
    // allowing headless/batch use without interactive OAuth prompts. Scheme becomes "Token"
    // so the server treats it as a Django TokenAuthentication credential.
    internal static class PersonalAccessToken
    {
        private static readonly string _patPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProjectPerseus", "pat.bin");

        public static void Store(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_patPath));
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token), null,
                    DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_patPath, encrypted);
                Utl.WriteLog("Personal API token stored (DPAPI-encrypted).");
            }
            catch (Exception ex) { Utl.WriteLog($"Failed to store PAT: {ex.Message}"); }
        }

        public static bool Exists() => File.Exists(_patPath);

        public static void Clear()
        {
            try { if (File.Exists(_patPath)) File.Delete(_patPath); }
            catch (Exception ex) { Utl.WriteLog($"Failed to clear PAT: {ex.Message}"); }
        }

        public static string Load()
        {
            try
            {
                if (!File.Exists(_patPath)) return null;
                var encrypted = File.ReadAllBytes(_patPath);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }
}
