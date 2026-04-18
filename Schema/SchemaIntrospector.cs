using Microsoft.Data.SqlClient;
using SqlTestDataGenerator.Schema.Models;
using System.Data;

namespace SqlTestDataGenerator.Schema
{
    /// <summary>
    /// Reads database schema metadata (columns, types, FK, PK, constraints)
    /// from SQL Server using INFORMATION_SCHEMA and sys catalog views.
    /// </summary>
    public class SchemaIntrospector
    {
        private readonly Func<SqlConnection> _connectionFactory;
        private readonly Dictionary<string, TableSchema> _cache = new(StringComparer.OrdinalIgnoreCase);

        public SchemaIntrospector(Func<SqlConnection> connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        /// <summary>
        /// Get the full schema for a table. Results are cached.
        /// </summary>
        public TableSchema GetTableSchema(string tableName, string schemaName = "dbo")
        {
            var key = $"{schemaName}.{tableName}";
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var schema = new TableSchema
            {
                TableName = tableName,
                SchemaName = schemaName
            };

            using var conn = _connectionFactory();

            schema.Columns = GetColumns(conn, tableName, schemaName);
            schema.PrimaryKey = GetPrimaryKey(conn, tableName, schemaName);
            schema.ForeignKeys = GetForeignKeys(conn, tableName, schemaName);
            schema.UniqueConstraints = GetUniqueConstraints(conn, tableName, schemaName);

            // Mark PK columns
            if (schema.PrimaryKey != null)
            {
                foreach (var pkCol in schema.PrimaryKey.Columns)
                {
                    var col = schema.GetColumn(pkCol);
                    if (col != null) col.IsPrimaryKey = true;
                }
            }

            // Detect IDENTITY columns
            DetectIdentityColumns(conn, tableName, schemaName, schema);
            DetectComputedColumns(conn, tableName, schemaName, schema);

            _cache[key] = schema;
            return schema;
        }

        /// <summary>
        /// Get schemas for all tables listed.
        /// </summary>
        public Dictionary<string, TableSchema> GetSchemas(IEnumerable<string> tableNames, string schemaName = "dbo")
        {
            var result = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in tableNames)
            {
                result[name] = GetTableSchema(name, schemaName);
            }
            return result;
        }

        /// <summary>
        /// Clear the schema cache.
        /// </summary>
        public void ClearCache() => _cache.Clear();

        // ═════════════════════════════════════════════════════════════════
        // Private query methods
        // ═════════════════════════════════════════════════════════════════

        private List<ColumnSchema> GetColumns(SqlConnection conn, string tableName, string schemaName)
        {
            var columns = new List<ColumnSchema>();
            var sql = @"
                SELECT 
                    c.COLUMN_NAME,
                    c.DATA_TYPE,
                    c.IS_NULLABLE,
                    c.CHARACTER_MAXIMUM_LENGTH,
                    c.NUMERIC_PRECISION,
                    c.NUMERIC_SCALE,
                    c.COLUMN_DEFAULT,
                    c.ORDINAL_POSITION
                FROM INFORMATION_SCHEMA.COLUMNS c
                WHERE c.TABLE_NAME = @TableName
                  AND c.TABLE_SCHEMA = @SchemaName
                ORDER BY c.ORDINAL_POSITION";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int? maxLength = null;
                if (!reader.IsDBNull(3))
                {
                    var rawLen = reader.GetInt32(3);
                    // SQL Server reports (max) types as -1. Treat that as unbounded.
                    maxLength = rawLen > 0 ? rawLen : null;
                }

                columns.Add(new ColumnSchema
                {
                    TableName = tableName,
                    SchemaName = schemaName,
                    ColumnName = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    MaxLength = maxLength,
                    NumericPrecision = reader.IsDBNull(4) ? null : (int?)Convert.ToInt32(reader.GetValue(4)),
                    NumericScale = reader.IsDBNull(5) ? null : (int?)Convert.ToInt32(reader.GetValue(5)),
                    DefaultValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                    OrdinalPosition = reader.GetInt32(7)
                });
            }
            return columns;
        }

        private PrimaryKeyInfo? GetPrimaryKey(SqlConnection conn, string tableName, string schemaName)
        {
            var sql = @"
                SELECT 
                    TC.CONSTRAINT_NAME,
                    KCU.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS TC
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE KCU 
                    ON TC.CONSTRAINT_NAME = KCU.CONSTRAINT_NAME
                   AND TC.TABLE_SCHEMA = KCU.TABLE_SCHEMA
                WHERE TC.TABLE_NAME = @TableName
                  AND TC.TABLE_SCHEMA = @SchemaName
                  AND TC.CONSTRAINT_TYPE = 'PRIMARY KEY'
                ORDER BY KCU.ORDINAL_POSITION";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);

            PrimaryKeyInfo? pk = null;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pk ??= new PrimaryKeyInfo { ConstraintName = reader.GetString(0) };
                pk.Columns.Add(reader.GetString(1));
            }
            return pk;
        }

        private List<ForeignKeyInfo> GetForeignKeys(SqlConnection conn, string tableName, string schemaName)
        {
            var fks = new List<ForeignKeyInfo>();
            var sql = @"
                SELECT 
                    FK.CONSTRAINT_NAME,
                    CU.COLUMN_NAME AS FK_Column,
                    PK.TABLE_NAME AS Referenced_Table,
                    PT.COLUMN_NAME AS Referenced_Column,
                    PK.TABLE_SCHEMA AS Referenced_Schema
                FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS RC
                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS FK 
                    ON RC.CONSTRAINT_NAME = FK.CONSTRAINT_NAME
                   AND RC.CONSTRAINT_SCHEMA = FK.CONSTRAINT_SCHEMA
                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS PK 
                    ON RC.UNIQUE_CONSTRAINT_NAME = PK.CONSTRAINT_NAME
                   AND RC.UNIQUE_CONSTRAINT_SCHEMA = PK.CONSTRAINT_SCHEMA
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE CU 
                    ON RC.CONSTRAINT_NAME = CU.CONSTRAINT_NAME
                   AND RC.CONSTRAINT_SCHEMA = CU.CONSTRAINT_SCHEMA
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE PT 
                    ON RC.UNIQUE_CONSTRAINT_NAME = PT.CONSTRAINT_NAME
                   AND RC.UNIQUE_CONSTRAINT_SCHEMA = PT.CONSTRAINT_SCHEMA
                WHERE FK.TABLE_NAME = @TableName
                  AND FK.TABLE_SCHEMA = @SchemaName";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                fks.Add(new ForeignKeyInfo
                {
                    ConstraintName = reader.GetString(0),
                    ColumnName = reader.GetString(1),
                    ReferencedTable = reader.GetString(2),
                    ReferencedColumn = reader.GetString(3),
                    ReferencedSchema = reader.GetString(4)
                });
            }
            return fks;
        }

        private List<UniqueConstraintInfo> GetUniqueConstraints(SqlConnection conn, string tableName, string schemaName)
        {
            var constraints = new Dictionary<string, UniqueConstraintInfo>();
            var sql = @"
                SELECT 
                    TC.CONSTRAINT_NAME,
                    KCU.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS TC
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE KCU 
                    ON TC.CONSTRAINT_NAME = KCU.CONSTRAINT_NAME
                   AND TC.TABLE_SCHEMA = KCU.TABLE_SCHEMA
                WHERE TC.TABLE_NAME = @TableName
                  AND TC.TABLE_SCHEMA = @SchemaName
                  AND TC.CONSTRAINT_TYPE = 'UNIQUE'
                ORDER BY KCU.ORDINAL_POSITION";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var constraintName = reader.GetString(0);
                if (!constraints.ContainsKey(constraintName))
                    constraints[constraintName] = new UniqueConstraintInfo { ConstraintName = constraintName };
                constraints[constraintName].Columns.Add(reader.GetString(1));
            }
            return constraints.Values.ToList();
        }

        private void DetectIdentityColumns(SqlConnection conn, string tableName, string schemaName, TableSchema schema)
        {
            var sql = @"
                SELECT c.name
                FROM sys.columns c
                JOIN sys.tables t ON c.object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.name = @TableName
                  AND s.name = @SchemaName
                  AND c.is_identity = 1";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var colName = reader.GetString(0);
                var col = schema.GetColumn(colName);
                if (col != null) col.IsIdentity = true;
            }
        }

        private void DetectComputedColumns(SqlConnection conn, string tableName, string schemaName, TableSchema schema)
        {
            var sql = @"
                SELECT c.name
                FROM sys.columns c
                JOIN sys.tables t ON c.object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.name = @TableName
                  AND s.name = @SchemaName
                  AND c.is_computed = 1";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var colName = reader.GetString(0);
                var col = schema.GetColumn(colName);
                if (col != null) col.IsComputed = true;
            }
        }
    }
}
