using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ProjectPerseus.auth
{
    // DPAPI-encrypted JWT refresh-token persistence at %AppData%\ProjectPerseus\jwt_refresh.bin.
    // The in-memory access token lives in JwtAuth; only the refresh token survives process exit
    // (and only for the current Windows user, since DPAPI binds to the user profile).
    internal static class TokenCache
    {
        private static readonly string _jwtRefreshPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProjectPerseus", "jwt_refresh.bin");

        public static void StoreRefreshToken(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_jwtRefreshPath));
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token), null,
                    DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_jwtRefreshPath, encrypted);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to store JWT refresh token: {ex.Message}");
            }
        }

        public static string LoadRefreshToken()
        {
            try
            {
                if (!File.Exists(_jwtRefreshPath)) return null;
                var encrypted = File.ReadAllBytes(_jwtRefreshPath);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }
}
