using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlTestDataGenerator.Database
{
    public sealed class ConnectionProfile
    {
        public string Server { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public bool UseWindowsAuth { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public sealed class ConnectionProfileCache
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SqlTestDataGenerator.ConnectionProfile.v1");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private string CacheDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SqlTestDataGenerator");

        private string CachePath => Path.Combine(CacheDirectory, "connection-cache.json");

        public bool TryLoad(out ConnectionProfile profile, out string message)
        {
            profile = new ConnectionProfile();
            message = string.Empty;

            if (!File.Exists(CachePath))
                return false;

            try
            {
                var json = File.ReadAllText(CachePath);
                var stored = JsonSerializer.Deserialize<StoredConnectionProfile>(json);
                if (stored == null ||
                    string.IsNullOrWhiteSpace(stored.Server) ||
                    string.IsNullOrWhiteSpace(stored.Database))
                {
                    message = "Cached connection profile is invalid.";
                    return false;
                }

                profile = new ConnectionProfile
                {
                    Server = stored.Server,
                    Database = stored.Database,
                    UseWindowsAuth = stored.UseWindowsAuth,
                    Username = stored.Username ?? string.Empty,
                    Password = stored.UseWindowsAuth ? string.Empty : Unprotect(stored.ProtectedPassword)
                };
                return true;
            }
            catch (Exception ex)
            {
                message = $"Cannot load cached connection profile: {ex.Message}";
                return false;
            }
        }

        public void Save(ConnectionProfile profile)
        {
            Directory.CreateDirectory(CacheDirectory);

            var stored = new StoredConnectionProfile
            {
                Server = profile.Server,
                Database = profile.Database,
                UseWindowsAuth = profile.UseWindowsAuth,
                Username = profile.UseWindowsAuth ? string.Empty : profile.Username,
                ProtectedPassword = profile.UseWindowsAuth ? string.Empty : Protect(profile.Password)
            };

            File.WriteAllText(CachePath, JsonSerializer.Serialize(stored, JsonOptions));
        }

        public void Clear()
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
            }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string Unprotect(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var protectedBytes = Convert.FromBase64String(value);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        private sealed class StoredConnectionProfile
        {
            public string Server { get; set; } = string.Empty;
            public string Database { get; set; } = string.Empty;
            public bool UseWindowsAuth { get; set; } = true;
            public string Username { get; set; } = string.Empty;
            public string ProtectedPassword { get; set; } = string.Empty;
        }
    }
}
