using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Schema.Models;
using System.Text.RegularExpressions;

namespace SqlTestDataGenerator.Schema
{
    public sealed class DdlSchemaParseResult
    {
        public Dictionary<string, TableSchema> Schemas { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings { get; } = new();
    }

    public sealed class DdlSchemaParser
    {
        public DdlSchemaParseResult Parse(string ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText))
                throw new ArgumentException("DDL text is empty.", nameof(ddlText));

            var result = new DdlSchemaParseResult();
            var statements = ExtractRelevantSchemaStatements(ddlText, result.Warnings);
            if (statements.Count == 0)
            {
                result.Warnings.Add("DDL file did not contain any CREATE TABLE statements.");
                return result;
            }

            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var visitor = new DdlSchemaVisitor(result);

            foreach (var statement in statements)
            {
                var normalizedStatement = NormalizeThreePartObjectNames(statement);
                TSqlFragment fragment;
                IList<ParseError> errors;

                using (var reader = new StringReader(normalizedStatement))
                {
                    fragment = parser.Parse(reader, out errors);
                }

                if (errors.Count > 0)
                {
                    var firstLine = normalizedStatement
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                        .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
                        ?.Trim();
                    var message = string.Join(
                        "; ",
                        errors.Select(e => $"Line {e.Line}, Col {e.Column}: {e.Message}"));
                    result.Warnings.Add($"Skipped unsupported DDL statement `{firstLine}`: {message}");
                    continue;
                }

                fragment.Accept(visitor);
            }

            FinalizeForeignKeys(result);
            FinalizeColumns(result);

            if (result.Schemas.Count == 0)
            {
                var warningText = result.Warnings.Count == 0
                    ? "No supported CREATE TABLE statements were parsed."
                    : string.Join(Environment.NewLine, result.Warnings);
                throw new FormatException($"DDL import found table definitions, but none could be parsed:{Environment.NewLine}{warningText}");
            }

            return result;
        }

        private static List<string> ExtractRelevantSchemaStatements(string ddlText, List<string> warnings)
        {
            var statements = new List<(int Start, string Text)>();

            foreach (Match match in Regex.Matches(
                         ddlText,
                         @"\bCREATE\s+TABLE\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var statement = TryExtractCreateTableStatement(ddlText, match.Index);
                if (statement != null)
                {
                    statements.Add((match.Index, statement));
                }
                else
                {
                    warnings.Add($"Could not isolate CREATE TABLE statement near character {match.Index}.");
                }
            }

            foreach (Match match in Regex.Matches(
                         ddlText,
                         @"\bALTER\s+TABLE\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var statement = TryExtractDelimitedStatement(ddlText, match.Index);
                if (statement != null)
                {
                    statements.Add((match.Index, statement));
                }
            }

            foreach (Match match in Regex.Matches(
                         ddlText,
                         @"\bCREATE\s+UNIQUE\s+(?:NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var statement = TryExtractDelimitedStatement(ddlText, match.Index);
                if (statement != null)
                {
                    statements.Add((match.Index, statement));
                }
            }

            return statements
                .OrderBy(s => s.Start)
                .Select(s => s.Text)
                .ToList();
        }

        private static string? TryExtractCreateTableStatement(string text, int start)
        {
            var openParen = FindNextChar(text, start, '(');
            if (openParen < 0)
                return null;

            var closeParen = FindMatchingParenthesis(text, openParen);
            if (closeParen < 0)
                return null;

            var end = closeParen + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            if (end < text.Length && text[end] == ';')
            {
                end++;
            }

            return text[start..end];
        }

        private static string? TryExtractDelimitedStatement(string text, int start)
        {
            var end = FindStatementTerminator(text, start);
            if (end < 0)
                return null;

            return text[start..(end + 1)];
        }

        private static int FindNextChar(string text, int start, char target)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == target)
                    return i;
            }

            return -1;
        }

        private static int FindMatchingParenthesis(string text, int openParen)
        {
            var depth = 0;
            for (var i = openParen; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '[')
                {
                    i = SkipBracketedIdentifier(text, i);
                    continue;
                }

                if (ch == '\'')
                {
                    i = SkipStringLiteral(text, i);
                    continue;
                }

                if (StartsLineComment(text, i))
                {
                    i = SkipLineComment(text, i);
                    continue;
                }

                if (StartsBlockComment(text, i))
                {
                    i = SkipBlockComment(text, i);
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static int FindStatementTerminator(string text, int start)
        {
            for (var i = start; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '[')
                {
                    i = SkipBracketedIdentifier(text, i);
                    continue;
                }

                if (ch == '\'')
                {
                    i = SkipStringLiteral(text, i);
                    continue;
                }

                if (StartsLineComment(text, i))
                {
                    i = SkipLineComment(text, i);
                    continue;
                }

                if (StartsBlockComment(text, i))
                {
                    i = SkipBlockComment(text, i);
                    continue;
                }

                if (ch == ';')
                    return i;
            }

            return -1;
        }

        private static int SkipBracketedIdentifier(string text, int start)
        {
            for (var i = start + 1; i < text.Length; i++)
            {
                if (text[i] == ']')
                    return i;
            }

            return text.Length - 1;
        }

        private static int SkipStringLiteral(string text, int start)
        {
            for (var i = start + 1; i < text.Length; i++)
            {
                if (text[i] != '\'')
                    continue;

                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i;
            }

            return text.Length - 1;
        }

        private static bool StartsLineComment(string text, int index) =>
            index + 1 < text.Length && text[index] == '-' && text[index + 1] == '-';

        private static bool StartsBlockComment(string text, int index) =>
            index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*';

        private static int SkipLineComment(string text, int start)
        {
            for (var i = start + 2; i < text.Length; i++)
            {
                if (text[i] is '\r' or '\n')
                    return i;
            }

            return text.Length - 1;
        }

        private static int SkipBlockComment(string text, int start)
        {
            for (var i = start + 2; i < text.Length - 1; i++)
            {
                if (text[i] == '*' && text[i + 1] == '/')
                    return i + 1;
            }

            return text.Length - 1;
        }

        private static string NormalizeThreePartObjectNames(string statement)
        {
            return Regex.Replace(
                statement,
                @"(?<![\w\]])(?<database>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$#@]*)\.(?<schema>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$#@]*)\.(?<table>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$#@]*)",
                "${schema}.${table}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static void FinalizeColumns(DdlSchemaParseResult result)
        {
            foreach (var schema in result.Schemas.Values)
            {
                if (schema.PrimaryKey != null)
                {
                    foreach (var columnName in schema.PrimaryKey.Columns)
                    {
                        var column = schema.GetColumn(columnName);
                        if (column != null)
                        {
                            column.IsPrimaryKey = true;
                            column.IsNullable = false;
                        }
                    }
                }

                for (var i = 0; i < schema.Columns.Count; i++)
                {
                    var column = schema.Columns[i];
                    column.OrdinalPosition = column.OrdinalPosition <= 0 ? i + 1 : column.OrdinalPosition;
                    column.SchemaName = string.IsNullOrWhiteSpace(column.SchemaName)
                        ? schema.SchemaName
                        : column.SchemaName;
                    column.TableName = string.IsNullOrWhiteSpace(column.TableName)
                        ? schema.TableName
                        : column.TableName;
                }
            }
        }

        private static void FinalizeForeignKeys(DdlSchemaParseResult result)
        {
            foreach (var schema in result.Schemas.Values)
            {
                foreach (var fk in schema.ForeignKeys)
                {
                    if (!string.IsNullOrWhiteSpace(fk.ReferencedColumn))
                        continue;

                    if (TryFindSchema(result, fk.ReferencedSchema, fk.ReferencedTable, out var referencedSchema) &&
                        referencedSchema.PrimaryKey?.Columns.Count == 1)
                    {
                        fk.ReferencedColumn = referencedSchema.PrimaryKey.Columns[0];
                    }
                    else
                    {
                        fk.ReferencedColumn = fk.ColumnName;
                        result.Warnings.Add(
                            $"Foreign key {schema.SchemaName}.{schema.TableName}.{fk.ColumnName} references " +
                            $"{fk.ReferencedSchema}.{fk.ReferencedTable} without explicit referenced columns; " +
                            $"using {fk.ReferencedColumn} as fallback.");
                    }
                }
            }
        }

        private static bool TryFindSchema(
            DdlSchemaParseResult result,
            string schemaName,
            string tableName,
            out TableSchema schema)
        {
            schema = null!;
            if (result.Schemas.TryGetValue(tableName, out var byTable) &&
                (string.IsNullOrWhiteSpace(schemaName) ||
                 byTable.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase)))
            {
                schema = byTable;
                return true;
            }

            var qualifiedKey = BuildSchemaKey(schemaName, tableName);
            if (result.Schemas.TryGetValue(qualifiedKey, out var byQualifiedName))
            {
                schema = byQualifiedName;
                return true;
            }

            return false;
        }

        private static string BuildSchemaKey(string schemaName, string tableName) =>
            $"{NormalizeSchemaName(schemaName)}.{tableName}";

        private static string NormalizeSchemaName(string? schemaName) =>
            string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName;

        private sealed class DdlSchemaVisitor : TSqlFragmentVisitor
        {
            private readonly DdlSchemaParseResult _result;

            public DdlSchemaVisitor(DdlSchemaParseResult result)
            {
                _result = result;
            }

            public override void Visit(CreateTableStatement node)
            {
                var schema = GetOrCreateSchema(node.SchemaObjectName);
                ApplyTableDefinition(schema, node.Definition);
            }

            public override void Visit(AlterTableAddTableElementStatement node)
            {
                var schema = GetOrCreateSchema(node.SchemaObjectName);
                ApplyTableDefinition(schema, node.Definition);
            }

            public override void Visit(CreateIndexStatement node)
            {
                if (!node.Unique || node.FilterPredicate != null)
                    return;

                var tableName = GetBaseName(node.OnName);
                var schemaName = GetSchemaName(node.OnName);
                if (!TryFindSchema(_result, schemaName, tableName, out var schema))
                {
                    _result.Warnings.Add(
                        $"Ignoring unique index {node.Name?.Value ?? "(unnamed)"} because table {schemaName}.{tableName} was not found in imported DDL.");
                    return;
                }

                var columns = node.Columns
                    .Select(c => GetColumnName(c.Column))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (columns.Count == 0)
                    return;

                AddUniqueConstraint(
                    schema,
                    node.Name?.Value ?? $"UX_{schema.TableName}_{schema.UniqueConstraints.Count + 1}",
                    columns);
            }

            private TableSchema GetOrCreateSchema(SchemaObjectName objectName)
            {
                var tableName = GetBaseName(objectName);
                var schemaName = GetSchemaName(objectName);

                if (_result.Schemas.TryGetValue(tableName, out var existing))
                {
                    if (!existing.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase))
                    {
                        _result.Warnings.Add(
                            $"Duplicate table name {tableName} found in schemas {existing.SchemaName} and {schemaName}; " +
                            "the first table-name mapping is kept for query lookup.");
                    }

                    return existing;
                }

                var schema = new TableSchema
                {
                    SchemaName = schemaName,
                    TableName = tableName
                };

                _result.Schemas[tableName] = schema;
                return schema;
            }

            private void ApplyTableDefinition(TableSchema schema, TableDefinition definition)
            {
                if (definition == null)
                    return;

                foreach (var columnDefinition in definition.ColumnDefinitions)
                {
                    var column = BuildColumnSchema(schema, columnDefinition);
                    var existing = schema.GetColumn(column.ColumnName);
                    if (existing == null)
                    {
                        schema.Columns.Add(column);
                    }
                    else
                    {
                        UpdateColumnSchema(existing, column);
                    }

                    foreach (var constraint in columnDefinition.Constraints)
                    {
                        ApplyConstraint(schema, constraint, column.ColumnName);
                    }

                    if (columnDefinition.DefaultConstraint != null)
                    {
                        ApplyDefaultConstraint(schema, columnDefinition.DefaultConstraint, column.ColumnName);
                    }
                }

                foreach (var constraint in definition.TableConstraints)
                {
                    ApplyConstraint(schema, constraint, owningColumnName: null);
                }
            }

            private ColumnSchema BuildColumnSchema(TableSchema table, ColumnDefinition definition)
            {
                var dataType = ResolveDataType(definition.DataType);
                var nullable = ResolveNullable(definition.Constraints) ?? true;
                var computedExpression = definition.ComputedColumnExpression == null
                    ? string.Empty
                    : GetFragmentText(definition.ComputedColumnExpression);
                var defaultConstraint = definition.DefaultConstraint ??
                    definition.Constraints.OfType<DefaultConstraintDefinition>().FirstOrDefault();

                return new ColumnSchema
                {
                    SchemaName = table.SchemaName,
                    TableName = table.TableName,
                    ColumnName = definition.ColumnIdentifier?.Value ?? string.Empty,
                    DataType = dataType.DataType,
                    SystemDataType = dataType.SystemDataType,
                    IsUserDefinedType = dataType.IsUserDefinedType,
                    IsNullable = nullable,
                    MaxLength = dataType.MaxLength,
                    NumericPrecision = dataType.NumericPrecision,
                    NumericScale = dataType.NumericScale,
                    DefaultValue = defaultConstraint?.Expression == null
                        ? null
                        : GetFragmentText(defaultConstraint.Expression),
                    IsIdentity = definition.IdentityOptions != null,
                    IsComputed = definition.ComputedColumnExpression != null,
                    ComputedExpression = computedExpression,
                    OrdinalPosition = table.Columns.Count + 1
                };
            }

            private static void UpdateColumnSchema(ColumnSchema target, ColumnSchema source)
            {
                target.DataType = source.DataType;
                target.SystemDataType = source.SystemDataType;
                target.IsUserDefinedType = source.IsUserDefinedType;
                target.IsNullable = source.IsNullable;
                target.MaxLength = source.MaxLength;
                target.NumericPrecision = source.NumericPrecision;
                target.NumericScale = source.NumericScale;
                target.DefaultValue = source.DefaultValue;
                target.IsIdentity = source.IsIdentity;
                target.IsComputed = source.IsComputed;
                target.ComputedExpression = source.ComputedExpression;
            }

            private static ColumnTypeMetadata ResolveDataType(DataTypeReference? dataType)
            {
                if (dataType == null)
                {
                    return new ColumnTypeMetadata
                    {
                        DataType = "decimal",
                        SystemDataType = "decimal",
                        NumericPrecision = 18,
                        NumericScale = 2
                    };
                }

                var isUserDefined = dataType is UserDataTypeReference;
                var typeName = dataType is SqlDataTypeReference sqlDataType
                    ? NormalizeSqlDataTypeName(sqlDataType.SqlDataTypeOption.ToString())
                    : NormalizeUserDataTypeName(dataType.Name);

                if (string.IsNullOrWhiteSpace(typeName))
                    typeName = GetFragmentText(dataType).Split('(')[0].Trim();

                var metadata = new ColumnTypeMetadata
                {
                    DataType = typeName,
                    SystemDataType = isUserDefined ? string.Empty : typeName,
                    IsUserDefinedType = isUserDefined
                };

                var parameters = dataType is ParameterizedDataTypeReference parameterized
                    ? parameterized.Parameters.ToList()
                    : new List<Literal>();

                ApplyTypeParameters(metadata, parameters);
                return metadata;
            }

            private static void ApplyTypeParameters(
                ColumnTypeMetadata metadata,
                IReadOnlyList<Literal> parameters)
            {
                var dataType = metadata.DataType.ToLowerInvariant();
                if (IsStringType(dataType) || IsBinaryType(dataType))
                {
                    metadata.MaxLength = parameters.Count == 0 ? DefaultLengthFor(dataType) : ReadLength(parameters[0]);
                    return;
                }

                if (dataType is "decimal" or "numeric")
                {
                    metadata.NumericPrecision = parameters.Count > 0
                        ? ReadInt(parameters[0], 18)
                        : 18;
                    metadata.NumericScale = parameters.Count > 1
                        ? ReadInt(parameters[1], 0)
                        : 0;
                    return;
                }

                if (dataType is "money" or "smallmoney")
                {
                    metadata.NumericScale = 4;
                    return;
                }

                if (dataType is "time" or "datetime2" or "datetimeoffset")
                {
                    metadata.NumericScale = parameters.Count > 0
                        ? ReadInt(parameters[0], 7)
                        : null;
                }
            }

            private static int? ReadLength(Literal literal)
            {
                var text = GetFragmentText(literal).Trim();
                if (text.Equals("max", StringComparison.OrdinalIgnoreCase))
                    return null;

                var value = ReadInt(literal, -1);
                return value > 0 ? value : null;
            }

            private static int ReadInt(Literal literal, int fallback)
            {
                var value = literal.Value;
                if (int.TryParse(value, out var parsed))
                    return parsed;

                var text = GetFragmentText(literal).Trim();
                return int.TryParse(text, out parsed) ? parsed : fallback;
            }

            private static int? DefaultLengthFor(string dataType) =>
                dataType is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary"
                    ? 1
                    : null;

            private static bool IsStringType(string dataType) =>
                dataType is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

            private static bool IsBinaryType(string dataType) =>
                dataType is "binary" or "varbinary" or "image";

            private static string NormalizeSqlDataTypeName(string optionName)
            {
                return optionName switch
                {
                    "BigInt" => "bigint",
                    "SmallInt" => "smallint",
                    "TinyInt" => "tinyint",
                    "SmallMoney" => "smallmoney",
                    "DateTime" => "datetime",
                    "SmallDateTime" => "smalldatetime",
                    "DateTime2" => "datetime2",
                    "DateTimeOffset" => "datetimeoffset",
                    "NText" => "ntext",
                    "VarBinary" => "varbinary",
                    "Sql_Variant" => "sql_variant",
                    "UniqueIdentifier" => "uniqueidentifier",
                    _ => optionName.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant()
                };
            }

            private static string NormalizeUserDataTypeName(SchemaObjectName? name)
            {
                if (name == null)
                    return string.Empty;

                return name.BaseIdentifier?.Value ?? GetFragmentText(name);
            }

            private static bool? ResolveNullable(IEnumerable<ConstraintDefinition> constraints)
            {
                var nullableConstraint = constraints
                    .OfType<NullableConstraintDefinition>()
                    .LastOrDefault();

                return nullableConstraint?.Nullable;
            }

            private void ApplyConstraint(
                TableSchema schema,
                ConstraintDefinition constraint,
                string? owningColumnName)
            {
                switch (constraint)
                {
                    case UniqueConstraintDefinition unique:
                        ApplyUniqueConstraint(schema, unique, owningColumnName);
                        break;
                    case ForeignKeyConstraintDefinition foreignKey:
                        ApplyForeignKeyConstraint(schema, foreignKey, owningColumnName);
                        break;
                    case DefaultConstraintDefinition defaultConstraint:
                        ApplyDefaultConstraint(schema, defaultConstraint, owningColumnName);
                        break;
                }
            }

            private void ApplyUniqueConstraint(
                TableSchema schema,
                UniqueConstraintDefinition constraint,
                string? owningColumnName)
            {
                var columns = constraint.Columns
                    .Select(c => GetColumnName(c.Column))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (columns.Count == 0 && !string.IsNullOrWhiteSpace(owningColumnName))
                    columns.Add(owningColumnName);

                if (columns.Count == 0)
                    return;

                var constraintName = constraint.ConstraintIdentifier?.Value ??
                    (constraint.IsPrimaryKey
                        ? $"PK_{schema.TableName}"
                        : $"UQ_{schema.TableName}_{schema.UniqueConstraints.Count + 1}");

                if (constraint.IsPrimaryKey)
                {
                    schema.PrimaryKey = new PrimaryKeyInfo
                    {
                        ConstraintName = constraintName,
                        Columns = columns
                    };

                    foreach (var columnName in columns)
                    {
                        var column = schema.GetColumn(columnName);
                        if (column != null)
                        {
                            column.IsPrimaryKey = true;
                            column.IsNullable = false;
                        }
                    }
                }
                else
                {
                    AddUniqueConstraint(schema, constraintName, columns);
                }
            }

            private void ApplyForeignKeyConstraint(
                TableSchema schema,
                ForeignKeyConstraintDefinition constraint,
                string? owningColumnName)
            {
                var columns = constraint.Columns
                    .Select(c => c.Value)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                if (columns.Count == 0 && !string.IsNullOrWhiteSpace(owningColumnName))
                    columns.Add(owningColumnName);

                if (columns.Count == 0)
                    return;

                var referencedTable = GetBaseName(constraint.ReferenceTableName);
                var referencedSchema = GetSchemaName(constraint.ReferenceTableName);
                var referencedColumns = constraint.ReferencedTableColumns
                    .Select(c => c.Value)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();
                var constraintName = constraint.ConstraintIdentifier?.Value ??
                    $"FK_{schema.TableName}_{referencedTable}_{schema.ForeignKeys.Count + 1}";

                for (var i = 0; i < columns.Count; i++)
                {
                    var referencedColumn = i < referencedColumns.Count
                        ? referencedColumns[i]
                        : referencedColumns.FirstOrDefault() ?? string.Empty;

                    schema.ForeignKeys.Add(new ForeignKeyInfo
                    {
                        ConstraintName = constraintName,
                        ColumnName = columns[i],
                        ReferencedTable = referencedTable,
                        ReferencedSchema = referencedSchema,
                        ReferencedColumn = referencedColumn
                    });
                }
            }

            private static void ApplyDefaultConstraint(
                TableSchema schema,
                DefaultConstraintDefinition constraint,
                string? owningColumnName)
            {
                var columnName = constraint.Column?.Value ?? owningColumnName;
                if (string.IsNullOrWhiteSpace(columnName))
                    return;

                var column = schema.GetColumn(columnName);
                if (column != null && constraint.Expression != null)
                {
                    column.DefaultValue = GetFragmentText(constraint.Expression);
                }
            }

            private static void AddUniqueConstraint(
                TableSchema schema,
                string constraintName,
                List<string> columns)
            {
                if (schema.UniqueConstraints.Any(c =>
                        c.Columns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase)))
                {
                    return;
                }

                schema.UniqueConstraints.Add(new UniqueConstraintInfo
                {
                    ConstraintName = constraintName,
                    Columns = columns
                });
            }

            private static string GetBaseName(SchemaObjectName? name) =>
                name?.BaseIdentifier?.Value ?? string.Empty;

            private static string GetSchemaName(SchemaObjectName? name) =>
                NormalizeSchemaName(name?.SchemaIdentifier?.Value);

            private static string GetColumnName(ColumnReferenceExpression? column)
            {
                var identifiers = column?.MultiPartIdentifier?.Identifiers;
                return identifiers == null || identifiers.Count == 0
                    ? string.Empty
                    : identifiers[^1].Value;
            }

            private static string GetFragmentText(TSqlFragment node)
            {
                if (node.ScriptTokenStream != null &&
                    node.FirstTokenIndex >= 0 &&
                    node.LastTokenIndex >= node.FirstTokenIndex)
                {
                    var tokens = new List<string>();
                    for (var i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
                    {
                        tokens.Add(node.ScriptTokenStream[i].Text);
                    }

                    return string.Join(string.Empty, tokens).Trim();
                }

                return string.Empty;
            }
        }

        private sealed class ColumnTypeMetadata
        {
            public string DataType { get; set; } = string.Empty;
            public string SystemDataType { get; set; } = string.Empty;
            public bool IsUserDefinedType { get; set; }
            public int? MaxLength { get; set; }
            public int? NumericPrecision { get; set; }
            public int? NumericScale { get; set; }
        }
    }
}
