namespace SqlTestDataGenerator.Schema.Models
{
    public class TableSchema
    {
        public string TableName { get; set; } = string.Empty;
        public string SchemaName { get; set; } = "dbo";
        public List<ColumnSchema> Columns { get; set; } = new();
        public PrimaryKeyInfo? PrimaryKey { get; set; }
        public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();
        public List<UniqueConstraintInfo> UniqueConstraints { get; set; } = new();
        public bool HasIdentityColumn => Columns.Any(c => c.IsIdentity);

        public ColumnSchema? GetColumn(string name) =>
            Columns.FirstOrDefault(c => c.ColumnName.Equals(name, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => $"{SchemaName}.{TableName} ({Columns.Count} columns)";
    }

    public class ColumnSchema
    {
        public string TableName { get; set; } = string.Empty;
        public string SchemaName { get; set; } = "dbo";
        public string ColumnName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string SystemDataType { get; set; } = string.Empty;
        public bool IsUserDefinedType { get; set; }
        public bool IsNullable { get; set; }
        public int? MaxLength { get; set; }
        public int? NumericPrecision { get; set; }
        public int? NumericScale { get; set; }
        public string? DefaultValue { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsComputed { get; set; }
        public string ComputedExpression { get; set; } = string.Empty;
        public int OrdinalPosition { get; set; }
        public string ColumnKey => $"{SchemaName}.{TableName}.{ColumnName}";
        public string EffectiveDataType =>
            string.IsNullOrWhiteSpace(SystemDataType) ? DataType : SystemDataType;
        public bool IsStoreGenerated =>
            EffectiveDataType.Equals("rowversion", StringComparison.OrdinalIgnoreCase) ||
            EffectiveDataType.Equals("timestamp", StringComparison.OrdinalIgnoreCase);

        /// <summary>Determines the C# type category for value generation</summary>
        public DataTypeCategory TypeCategory => EffectiveDataType.ToLowerInvariant() switch
        {
            "int" or "bigint" or "smallint" or "tinyint" => DataTypeCategory.Integer,
            "decimal" or "numeric" or "money" or "smallmoney" => DataTypeCategory.Decimal,
            "float" or "real" => DataTypeCategory.Float,
            "bit" => DataTypeCategory.Boolean,
            "datetime" or "datetime2" or "date" or "smalldatetime" => DataTypeCategory.DateTime,
            "time" => DataTypeCategory.Time,
            "datetimeoffset" => DataTypeCategory.DateTimeOffset,
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => DataTypeCategory.String,
            "uniqueidentifier" => DataTypeCategory.Guid,
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => DataTypeCategory.Binary,
            "xml" => DataTypeCategory.Xml,
            "geography" or "geometry" => DataTypeCategory.Spatial,
            "hierarchyid" => DataTypeCategory.HierarchyId,
            "sql_variant" => DataTypeCategory.SqlVariant,
            _ => DataTypeCategory.String
        };

        public override string ToString() => $"{ColumnKey} ({DataType}{(IsNullable ? ", NULL" : ", NOT NULL")})";
    }

    public class PrimaryKeyInfo
    {
        public string ConstraintName { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
    }

    public class ForeignKeyInfo
    {
        public string ConstraintName { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
        public string ReferencedTable { get; set; } = string.Empty;
        public string ReferencedColumn { get; set; } = string.Empty;
        public string ReferencedSchema { get; set; } = "dbo";

        public override string ToString() =>
            $"{ColumnName} → {ReferencedTable}.{ReferencedColumn}";
    }

    public class UniqueConstraintInfo
    {
        public string ConstraintName { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
    }

    public enum DataTypeCategory
    {
        Integer,
        Decimal,
        Float,
        Boolean,
        DateTime,
        Time,
        DateTimeOffset,
        String,
        Guid,
        Binary,
        Xml,
        Spatial,
        HierarchyId,
        SqlVariant
    }
}
