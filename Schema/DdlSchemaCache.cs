using SqlTestDataGenerator.Schema.Models;
using System.Text.Json;

namespace SqlTestDataGenerator.Schema
{
    public sealed class DdlSchemaCache
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IgnoreReadOnlyProperties = true
        };

        private string CacheDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SqlTestDataGenerator");

        private string CachePath => Path.Combine(CacheDirectory, "ddl-schema-cache.json");

        public bool TryLoad(
            out Dictionary<string, TableSchema> schemas,
            out string sourceFile,
            out DateTime importedAtUtc,
            out string message)
        {
            schemas = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
            sourceFile = string.Empty;
            importedAtUtc = default;
            message = string.Empty;

            if (!File.Exists(CachePath))
                return false;

            try
            {
                var json = File.ReadAllText(CachePath);
                var stored = JsonSerializer.Deserialize<StoredDdlSchemaCache>(json, JsonOptions);
                if (stored == null || stored.Schemas.Count == 0)
                {
                    message = "Cached DDL schema is empty.";
                    return false;
                }

                schemas = new Dictionary<string, TableSchema>(
                    stored.Schemas,
                    StringComparer.OrdinalIgnoreCase);
                sourceFile = stored.SourceFile ?? string.Empty;
                importedAtUtc = stored.ImportedAtUtc;
                return true;
            }
            catch (Exception ex)
            {
                message = $"Cannot load cached DDL schema: {ex.Message}";
                return false;
            }
        }

        public void Save(
            string sourceFile,
            Dictionary<string, TableSchema> schemas,
            DateTime importedAtUtc)
        {
            Directory.CreateDirectory(CacheDirectory);

            var stored = new StoredDdlSchemaCache
            {
                SourceFile = sourceFile,
                ImportedAtUtc = importedAtUtc,
                Schemas = new Dictionary<string, TableSchema>(
                    schemas,
                    StringComparer.OrdinalIgnoreCase)
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

        private sealed class StoredDdlSchemaCache
        {
            public string? SourceFile { get; set; }
            public DateTime ImportedAtUtc { get; set; }
            public Dictionary<string, TableSchema> Schemas { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
