using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Parsing.Visitors;

namespace SqlTestDataGenerator.Parsing
{
    /// <summary>
    /// Core service that parses SQL text and produces a fully analyzed ParsedQuery.
    /// Uses Microsoft.SqlServer.TransactSql.ScriptDom for T-SQL parsing.
    /// </summary>
    public class SqlParserService
    {
        /// <summary>
        /// Parse a SQL SELECT statement and extract all relevant information.
        /// </summary>
        public ParsedQuery Parse(string sql)
        {
            var result = new ParsedQuery { OriginalSql = sql };
            var predicateBuilder = new PredicateTreeBuilder();

            // Step 1: Parse SQL into AST
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            TSqlFragment fragment;
            IList<ParseError> errors;

            using (var reader = new StringReader(sql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            if (errors.Any())
            {
                result.Errors.AddRange(errors.Select(e => $"Line {e.Line}, Col {e.Column}: {e.Message}"));
                return result;
            }

            // Step 2: Find the QuerySpecification (main SELECT)
            var querySpec = FindQuerySpecification(fragment);
            if (querySpec == null)
            {
                result.Errors.Add("Could not find a SELECT statement in the provided SQL.");
                return result;
            }

            // Step 3: Extract tables
            var tableVisitor = new TableExtractorVisitor();
            fragment.Accept(tableVisitor);
            var cteNames = ExtractCteNames(fragment);
            result.Tables = tableVisitor.Tables
                .Where(t => !cteNames.Contains(t.TableName))
                .ToList();
            result.DerivedColumnMappings = CollectDerivedColumnMappings(fragment);

            // Step 4: Extract JOINs and update table roles
            var joinVisitor = new JoinExtractorVisitor();
            if (querySpec.FromClause != null)
            {
                querySpec.FromClause.Accept(joinVisitor);
            }
            result.Joins = joinVisitor.Joins;
            RewriteJoinAliases(result);
            UpdateTableRolesFromJoins(result);
            var outerScopeAliasMap = BuildScopeAliasMap(querySpec.FromClause);

            // Step 5/6: Extract exact WHERE/HAVING/JOIN predicate scopes
            AnalyzePredicateScopes(querySpec, result, predicateBuilder, outerScopeAliasMap, "Main");
            AnalyzeJoinPredicateScopes(querySpec.FromClause, result, predicateBuilder, outerScopeAliasMap, "Main");

            // Step 7: Extract GROUP BY
            if (querySpec.GroupByClause != null)
            {
                var groupByVisitor = new GroupByExtractorVisitor();
                querySpec.GroupByClause.Accept(groupByVisitor);
                result.GroupByColumns = groupByVisitor.GroupByColumns;
            }

            // Step 8: Extract subqueries
            var subqueryVisitor = new SubqueryExtractorVisitor();
            if (querySpec.WhereClause != null)
                querySpec.WhereClause.Accept(subqueryVisitor);
            if (querySpec.HavingClause != null)
                querySpec.HavingClause.Accept(subqueryVisitor);
            result.Subqueries = subqueryVisitor.Subqueries;

            // Step 9: Extract aggregates
            var aggregateVisitor = new AggregateExtractorVisitor();
            querySpec.Accept(aggregateVisitor);
            result.Aggregates = aggregateVisitor.Aggregates;

            // Step 10: Extract SELECT columns
            ExtractSelectColumns(querySpec, result);

            // Step 10.5: Merge execution constraints from nested query bodies.
            AnalyzeNestedQuerySpecifications(fragment, result, predicateBuilder, querySpec);

            // Step 10.6: Link outer EXISTS/IN predicate leaves back to extracted subqueries.
            AttachSubqueryPredicateKeys(result);

            // Step 11: DISTINCT / TOP
            result.HasDistinct = querySpec.UniqueRowFilter == UniqueRowFilter.Distinct;
            if (querySpec.TopRowFilter != null && querySpec.TopRowFilter.Expression is IntegerLiteral topLit)
            {
                result.TopCount = int.Parse(topLit.Value);
            }

            // Step 12: Validate and warn
            ValidateAndWarn(result);

            return result;
        }

        /// <summary>
        /// Generates a summary of the parsed query for UI display.
        /// </summary>
        public string GenerateSummary(ParsedQuery query)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("═══ ANALYSIS RESULT ═══");
            sb.AppendLine();

            // Tables
            sb.AppendLine($"📋 Tables ({query.Tables.Count}):");
            foreach (var t in query.Tables)
            {
                var role = t.Role switch
                {
                    TableRole.From => "FROM",
                    TableRole.InnerJoin => "INNER JOIN",
                    TableRole.LeftJoin => "LEFT JOIN",
                    TableRole.RightJoin => "RIGHT JOIN",
                    TableRole.FullJoin => "FULL JOIN",
                    TableRole.CrossJoin => "CROSS JOIN",
                    _ => t.Role.ToString()
                };
                sb.AppendLine($"  • {t.TableName} ({t.Alias}) [{role}]");
            }
            sb.AppendLine();

            // Joins
            if (query.Joins.Any())
            {
                sb.AppendLine($"🔗 Joins ({query.Joins.Count}):");
                foreach (var j in query.Joins)
                {
                    sb.AppendLine($"  • {j}");
                }
                sb.AppendLine();
            }

            // WHERE conditions
            if (query.WhereConditions.Any())
            {
                sb.AppendLine($"🔍 WHERE Conditions ({query.WhereConditions.Count}):");
                foreach (var c in query.WhereConditions)
                {
                    sb.AppendLine($"  • {c}");
                }
                sb.AppendLine();
            }

            // HAVING conditions
            if (query.HavingConditions.Any())
            {
                sb.AppendLine($"📊 HAVING Conditions ({query.HavingConditions.Count}):");
                foreach (var c in query.HavingConditions)
                {
                    sb.AppendLine($"  • {c}");
                }
                sb.AppendLine();
            }

            // GROUP BY
            if (query.GroupByColumns.Any())
            {
                sb.AppendLine($"📦 GROUP BY ({query.GroupByColumns.Count}):");
                foreach (var g in query.GroupByColumns)
                {
                    sb.AppendLine($"  • {g}");
                }
                sb.AppendLine();
            }

            // Subqueries
            if (query.Subqueries.Any())
            {
                sb.AppendLine($"🔄 Subqueries ({query.Subqueries.Count}):");
                foreach (var s in query.Subqueries)
                {
                    sb.AppendLine($"  • {s}");
                    if (s.NestedSubqueries.Any())
                    {
                        foreach (var ns in s.NestedSubqueries)
                        {
                            sb.AppendLine($"    └─ {ns}");
                        }
                    }
                }
                sb.AppendLine();
            }

            // Aggregates
            if (query.Aggregates.Any())
            {
                sb.AppendLine($"∑ Aggregates ({query.Aggregates.Count}):");
                foreach (var a in query.Aggregates)
                {
                    sb.AppendLine($"  • {a}");
                }
                sb.AppendLine();
            }

            // Warnings
            if (query.Warnings.Any())
            {
                sb.AppendLine("⚠ Warnings:");
                foreach (var w in query.Warnings)
                {
                    sb.AppendLine($"  • {w}");
                }
            }

            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════
        // Private helpers
        // ═════════════════════════════════════════════════════════════════

        private QuerySpecification? FindQuerySpecification(TSqlFragment fragment)
        {
            if (fragment is TSqlScript script)
            {
                foreach (var batch in script.Batches)
                {
                    foreach (var stmt in batch.Statements)
                    {
                        if (stmt is SelectStatement selectStmt)
                        {
                            if (selectStmt.QueryExpression is QuerySpecification spec)
                                return spec;
                        }
                    }
                }
            }
            return null;
        }

        private void ExtractSelectColumns(QuerySpecification spec, ParsedQuery result)
        {
            foreach (var elem in spec.SelectElements)
            {
                if (elem is SelectScalarExpression scalarExpr)
                {
                    var col = new SelectColumnInfo
                    {
                        OutputAlias = scalarExpr.ColumnName?.Value ?? ""
                    };

                    if (scalarExpr.Expression is ColumnReferenceExpression colRef)
                    {
                        var parts = colRef.MultiPartIdentifier?.Identifiers;
                        if (parts != null && parts.Count >= 2)
                        {
                            col.TableAlias = parts[0].Value;
                            col.ColumnName = parts[1].Value;
                        }
                        else if (parts != null && parts.Count == 1)
                        {
                            col.ColumnName = parts[0].Value;
                        }
                    }
                    else if (scalarExpr.Expression is FunctionCall func)
                    {
                        col.IsAggregate = IsAggregateFunction(func.FunctionName?.Value ?? "");
                        col.Expression = GetFragmentText(func);
                    }
                    else
                    {
                        col.Expression = GetFragmentText(scalarExpr.Expression);
                    }

                    result.SelectColumns.Add(col);
                }
                else if (elem is SelectStarExpression)
                {
                    result.SelectColumns.Add(new SelectColumnInfo { ColumnName = "*" });
                }
            }
        }

        private void UpdateTableRolesFromJoins(ParsedQuery result)
        {
            foreach (var join in result.Joins)
            {
                var role = join.Type switch
                {
                    Models.JoinType.Inner => TableRole.InnerJoin,
                    Models.JoinType.Left => TableRole.LeftJoin,
                    Models.JoinType.Right => TableRole.RightJoin,
                    Models.JoinType.Full => TableRole.FullJoin,
                    Models.JoinType.Cross => TableRole.CrossJoin,
                    _ => TableRole.InnerJoin
                };

                // Update the right table's role
                var rightTable = result.Tables.FirstOrDefault(t =>
                    t.Alias.Equals(join.RightTableAlias, StringComparison.OrdinalIgnoreCase) ||
                    t.TableName.Equals(join.RightTableAlias, StringComparison.OrdinalIgnoreCase));

                if (rightTable != null)
                {
                    rightTable.Role = role;
                }
            }
        }

        private void AnalyzeNestedQuerySpecifications(
            TSqlFragment fragment,
            ParsedQuery result,
            PredicateTreeBuilder predicateBuilder,
            QuerySpecification mainQuerySpec)
        {
            int nestedIndex = 1;

            foreach (var cte in ExtractCteQuerySpecifications(fragment))
            {
                AnalyzeQuerySpecification(cte.Name, cte.Spec, result, predicateBuilder);
            }

            foreach (var spec in ExtractDerivedTableQuerySpecifications(fragment))
            {
                if (ReferenceEquals(spec, mainQuerySpec))
                    continue;

                AnalyzeQuerySpecification($"Derived Query {nestedIndex++}", spec, result, predicateBuilder);
            }

            RewriteConditionAliases(result);
            RewriteJoinAliases(result);
            DeduplicateAnalysis(result);
        }

        private void AnalyzeQuerySpecification(
            string scopeLabel,
            QuerySpecification spec,
            ParsedQuery result,
            PredicateTreeBuilder predicateBuilder)
        {
            var scopeAliasMap = BuildScopeAliasMap(spec.FromClause);
            var joinVisitor = new JoinExtractorVisitor();
            if (spec.FromClause != null)
            {
                spec.FromClause.Accept(joinVisitor);
                result.Joins.AddRange(joinVisitor.Joins);
            }

            if (spec.WhereClause != null)
            {
                var subqueryVisitor = new SubqueryExtractorVisitor();
                spec.WhereClause.Accept(subqueryVisitor);
                result.Subqueries.AddRange(subqueryVisitor.Subqueries);
            }

            AnalyzePredicateScopes(spec, result, predicateBuilder, scopeAliasMap, scopeLabel);
            AnalyzeJoinPredicateScopes(spec.FromClause, result, predicateBuilder, scopeAliasMap, scopeLabel);

            if (spec.HavingClause != null)
            {
                var subqueryVisitor = new SubqueryExtractorVisitor();
                spec.HavingClause.Accept(subqueryVisitor);
                result.Subqueries.AddRange(subqueryVisitor.Subqueries);
            }

            if (spec.GroupByClause != null)
            {
                var groupByVisitor = new GroupByExtractorVisitor();
                spec.GroupByClause.Accept(groupByVisitor);
                result.GroupByColumns.AddRange(groupByVisitor.GroupByColumns);
            }

            var aggregateVisitor = new AggregateExtractorVisitor();
            spec.Accept(aggregateVisitor);
            result.Aggregates.AddRange(aggregateVisitor.Aggregates);
        }

        private IEnumerable<(string Name, QuerySpecification Spec)> ExtractCteQuerySpecifications(TSqlFragment fragment)
        {
            var visitor = new CteCollectorVisitor();
            fragment.Accept(visitor);

            foreach (var cte in visitor.CommonTableExpressions)
            {
                var cteName = cte.ExpressionName?.Value ?? "CTE";
                foreach (var spec in EnumerateQuerySpecifications(cte.QueryExpression))
                {
                    yield return ($"CTE {cteName}", spec);
                }
            }
        }

        private IEnumerable<QuerySpecification> ExtractDerivedTableQuerySpecifications(TSqlFragment fragment)
        {
            var visitor = new DerivedTableCollectorVisitor();
            fragment.Accept(visitor);

            foreach (var derivedTable in visitor.DerivedTables)
            {
                foreach (var spec in EnumerateQuerySpecifications(derivedTable.QueryExpression))
                {
                    yield return spec;
                }
            }
        }

        private IEnumerable<QuerySpecification> EnumerateQuerySpecifications(QueryExpression? expression)
        {
            if (expression == null)
                yield break;

            switch (expression)
            {
                case QuerySpecification spec:
                    yield return spec;
                    yield break;

                case QueryParenthesisExpression paren:
                    foreach (var nested in EnumerateQuerySpecifications(paren.QueryExpression))
                    {
                        yield return nested;
                    }
                    yield break;

                case BinaryQueryExpression binary:
                    foreach (var left in EnumerateQuerySpecifications(binary.FirstQueryExpression))
                    {
                        yield return left;
                    }

                    foreach (var right in EnumerateQuerySpecifications(binary.SecondQueryExpression))
                    {
                        yield return right;
                    }
                    yield break;
            }
        }

        private void DeduplicateAnalysis(ParsedQuery result)
        {
            result.WhereConditions = result.WhereConditions
                .DistinctBy(BuildConditionKey)
                .ToList();
            result.HavingConditions = result.HavingConditions
                .DistinctBy(BuildConditionKey)
                .ToList();
            result.Joins = result.Joins
                .DistinctBy(BuildJoinKey)
                .ToList();
            result.Subqueries = NormalizeSubqueries(result.Subqueries);
            result.Aggregates = result.Aggregates
                .DistinctBy(BuildAggregateKey)
                .ToList();
            result.GroupByColumns = result.GroupByColumns
                .DistinctBy(g => $"{g.TableAlias}.{g.ColumnName}")
                .ToList();

            UpdateTableRolesFromJoins(result);
        }

        private void AnalyzePredicateScopes(
            QuerySpecification spec,
            ParsedQuery result,
            PredicateTreeBuilder predicateBuilder,
            IReadOnlyDictionary<string, string> scopeAliasMap,
            string scopeLabel)
        {
            if (spec.WhereClause != null)
            {
                var whereScope = predicateBuilder.BuildScope(
                    spec.WhereClause.SearchCondition,
                    ConditionSource.Where,
                    $"{NormalizeScopeLabel(scopeLabel)}:where",
                    $"{scopeLabel} WHERE");
                whereScope.Conditions = FilterConditionsToScope(whereScope.Conditions, scopeAliasMap);
                result.PredicateScopes.Add(whereScope);
                result.WhereConditions.AddRange(whereScope.Conditions);
            }

            if (spec.HavingClause != null)
            {
                var havingScope = predicateBuilder.BuildScope(
                    spec.HavingClause.SearchCondition,
                    ConditionSource.Having,
                    $"{NormalizeScopeLabel(scopeLabel)}:having",
                    $"{scopeLabel} HAVING");
                havingScope.Conditions = FilterConditionsToScope(havingScope.Conditions, scopeAliasMap);
                result.PredicateScopes.Add(havingScope);
                result.HavingConditions.AddRange(havingScope.Conditions);
            }
        }

        private void AnalyzeJoinPredicateScopes(
            FromClause? fromClause,
            ParsedQuery result,
            PredicateTreeBuilder predicateBuilder,
            IReadOnlyDictionary<string, string> scopeAliasMap,
            string scopeLabel)
        {
            if (fromClause == null)
                return;

            int joinIndex = 1;
            foreach (var join in EnumerateQualifiedJoins(fromClause.TableReferences))
            {
                if (join.SearchCondition == null)
                    continue;

                var joinScope = predicateBuilder.BuildScope(
                    join.SearchCondition,
                    ConditionSource.JoinOn,
                    $"{NormalizeScopeLabel(scopeLabel)}:join{joinIndex}",
                    $"{scopeLabel} JOIN {ResolveTableReferenceLabel(join.SecondTableReference)}");
                joinScope.Conditions = FilterConditionsToScope(joinScope.Conditions, scopeAliasMap);
                result.PredicateScopes.Add(joinScope);
                joinIndex++;
            }
        }

        private List<SubqueryInfo> NormalizeSubqueries(IEnumerable<SubqueryInfo> subqueries)
        {
            return subqueries
                .GroupBy(BuildSubqueryIdentityKey)
                .Select(group =>
                {
                    var canonical = group.First();
                    canonical.Operator = SelectPreferredSubqueryOperator(group.Select(s => s.Operator));
                    canonical.Tables = group
                        .SelectMany(s => s.Tables)
                        .DistinctBy(BuildTableIdentityKey)
                        .ToList();
                    canonical.Conditions = group
                        .SelectMany(s => s.Conditions)
                        .DistinctBy(BuildConditionKey)
                        .ToList();
                    canonical.NestedSubqueries = NormalizeSubqueries(group.SelectMany(s => s.NestedSubqueries));
                    canonical.IsCorrelated = group.Any(s => s.IsCorrelated);
                    canonical.NestingLevel = group.Min(s => s.NestingLevel);
                    return canonical;
                })
                .ToList();
        }

        private static string BuildConditionKey(ConditionInfo condition)
        {
            return string.Join("|",
                condition.Source,
                condition.TableAlias,
                condition.ColumnName,
                condition.Operator,
                condition.Value,
                condition.SecondValue,
                condition.LikePattern,
                condition.RightTableAlias,
                condition.RightColumnName,
                condition.AggregateFunc?.ToString() ?? string.Empty,
                condition.ExpressionText,
                condition.HasSubquery ? "1" : "0",
                condition.IsNegated ? "1" : "0");
        }

        private Dictionary<string, string> BuildScopeAliasMap(FromClause? fromClause)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fromClause == null)
                return map;

            var visitor = new TableExtractorVisitor();
            fromClause.Accept(visitor);

            foreach (var table in visitor.Tables)
            {
                if (!string.IsNullOrWhiteSpace(table.Alias))
                {
                    map[table.Alias] = table.TableName;
                }

                if (!string.IsNullOrWhiteSpace(table.TableName))
                {
                    map[table.TableName] = table.TableName;
                }
            }

            var derivedVisitor = new DerivedTableCollectorVisitor();
            fromClause.Accept(derivedVisitor);
            foreach (var derived in derivedVisitor.DerivedTables)
            {
                var alias = derived.Alias?.Value;
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    map[alias] = alias;
                }
            }

            return map;
        }

        private List<ConditionInfo> FilterConditionsToScope(
            IEnumerable<ConditionInfo> conditions,
            IReadOnlyDictionary<string, string> scopeAliasMap)
        {
            return conditions
                .Where(c => ConditionBelongsToScope(c, scopeAliasMap))
                .ToList();
        }

        private static bool ConditionBelongsToScope(
            ConditionInfo condition,
            IReadOnlyDictionary<string, string> scopeAliasMap)
        {
            if (condition.HasSubquery ||
                condition.Operator is ComparisonOp.Exists or ComparisonOp.NotExists)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(condition.TableAlias))
                return true;

            return scopeAliasMap.ContainsKey(condition.TableAlias);
        }

        private void AttachSubqueryPredicateKeys(ParsedQuery result)
        {
            var rootConditionLookup = result.PredicateScopes
                .SelectMany(s => s.Conditions)
                .Where(c => c.IsSubqueryPredicate)
                .ToList();

            AttachSubqueryPredicateKeys(result.Subqueries, rootConditionLookup);
        }

        private void AttachSubqueryPredicateKeys(
            IEnumerable<SubqueryInfo> subqueries,
            IReadOnlyList<ConditionInfo> candidateConditions)
        {
            foreach (var subquery in subqueries)
            {
                var match = candidateConditions.FirstOrDefault(c =>
                    IsMatchingSubqueryPredicate(c, subquery));

                if (match != null)
                {
                    subquery.PredicateConditionKey = match.Key;
                }

                var nestedConditions = subquery.WherePredicateScope?.Conditions
                    .Where(c => c.IsSubqueryPredicate)
                    .ToList() ?? new List<ConditionInfo>();

                AttachSubqueryPredicateKeys(subquery.NestedSubqueries, nestedConditions);
            }
        }

        private static bool IsMatchingSubqueryPredicate(ConditionInfo condition, SubqueryInfo subquery)
        {
            if (!string.Equals(condition.SubquerySql, subquery.SubquerySql, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!IsMatchingSubqueryOperator(condition.Operator, subquery.Operator))
                return false;

            if (subquery.Operator is SubqueryOperator.In or SubqueryOperator.NotIn)
            {
                return string.Equals(condition.TableAlias, subquery.ParentTableAlias, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(condition.ColumnName, subquery.ParentColumnName, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static bool IsMatchingSubqueryOperator(ComparisonOp conditionOperator, SubqueryOperator subqueryOperator)
        {
            return (conditionOperator, subqueryOperator) switch
            {
                (ComparisonOp.In, SubqueryOperator.In) => true,
                (ComparisonOp.NotIn, SubqueryOperator.NotIn) => true,
                (ComparisonOp.Exists, SubqueryOperator.Exists) => true,
                (ComparisonOp.NotExists, SubqueryOperator.NotExists) => true,
                _ => false
            };
        }

        private static string NormalizeScopeLabel(string value)
        {
            return new string(value
                .Where(ch => char.IsLetterOrDigit(ch))
                .ToArray())
                .ToLowerInvariant();
        }

        private static string BuildJoinKey(JoinInfo join)
        {
            return string.Join("|",
                join.Type,
                join.LeftTableAlias,
                join.LeftColumn,
                join.RightTableAlias,
                join.RightColumn,
                join.OnConditionText);
        }

        private void RewriteJoinAliases(ParsedQuery result)
        {
            foreach (var join in result.Joins)
            {
                var leftAlias = join.LeftTableAlias;
                var leftColumn = join.LeftColumn;
                ResolveDerivedAliasColumn(result, ref leftAlias, ref leftColumn);
                join.LeftTableAlias = leftAlias;
                join.LeftColumn = leftColumn;

                var rightAlias = join.RightTableAlias;
                var rightColumn = join.RightColumn;
                ResolveDerivedAliasColumn(result, ref rightAlias, ref rightColumn);
                join.RightTableAlias = rightAlias;
                join.RightColumn = rightColumn;

                foreach (var condition in join.AdditionalOnConditions)
                {
                    ResolveConditionDerivedAliases(result, condition);
                }
            }
        }

        private static void RewriteConditionAliases(ParsedQuery result)
        {
            foreach (var scope in result.PredicateScopes)
            {
                foreach (var condition in scope.Conditions)
                {
                    ResolveConditionDerivedAliases(result, condition);
                }
            }

            RewriteSubqueryConditionAliases(result, result.Subqueries);
        }

        private static void RewriteSubqueryConditionAliases(ParsedQuery result, IEnumerable<SubqueryInfo> subqueries)
        {
            foreach (var subquery in subqueries)
            {
                foreach (var condition in subquery.Conditions)
                {
                    ResolveConditionDerivedAliases(result, condition);
                }

                if (subquery.WherePredicateScope != null)
                {
                    foreach (var condition in subquery.WherePredicateScope.Conditions)
                    {
                        ResolveConditionDerivedAliases(result, condition);
                    }
                }

                RewriteSubqueryConditionAliases(result, subquery.NestedSubqueries);
            }
        }

        private static void ResolveConditionDerivedAliases(ParsedQuery result, ConditionInfo condition)
        {
            ResolveScalarExpressionDerivedAliases(result, condition.LeftExpression);
            ResolveScalarExpressionDerivedAliases(result, condition.RightExpression);

            if (!string.IsNullOrWhiteSpace(condition.TableAlias) &&
                !string.IsNullOrWhiteSpace(condition.ColumnName))
            {
                var alias = condition.TableAlias;
                var column = condition.ColumnName;
                ResolveDerivedAliasColumn(result, ref alias, ref column);
                condition.TableAlias = alias;
                condition.ColumnName = column;
            }

            if (!string.IsNullOrWhiteSpace(condition.RightTableAlias) &&
                !string.IsNullOrWhiteSpace(condition.RightColumnName))
            {
                var alias = condition.RightTableAlias;
                var column = condition.RightColumnName;
                ResolveDerivedAliasColumn(result, ref alias, ref column);
                condition.RightTableAlias = alias;
                condition.RightColumnName = column;
            }

            foreach (var reference in condition.ReferencedColumns)
            {
                if (string.IsNullOrWhiteSpace(reference.TableAlias) ||
                    string.IsNullOrWhiteSpace(reference.ColumnName))
                {
                    continue;
                }

                var alias = reference.TableAlias;
                var column = reference.ColumnName;
                ResolveDerivedAliasColumn(result, ref alias, ref column);
                reference.TableAlias = alias;
                reference.ColumnName = column;
            }
        }

        private static void ResolveScalarExpressionDerivedAliases(ParsedQuery result, ScalarExpressionInfo? expression)
        {
            switch (expression)
            {
                case ColumnScalarExpressionInfo column:
                {
                    var alias = column.TableAlias;
                    var columnName = column.ColumnName;
                    ResolveDerivedAliasColumn(result, ref alias, ref columnName);
                    column.TableAlias = alias;
                    column.ColumnName = columnName;
                    break;
                }

                case FunctionScalarExpressionInfo function:
                    foreach (var argument in function.Arguments)
                    {
                        ResolveScalarExpressionDerivedAliases(result, argument);
                    }
                    break;

                case BinaryScalarExpressionInfo binary:
                    ResolveScalarExpressionDerivedAliases(result, binary.Left);
                    ResolveScalarExpressionDerivedAliases(result, binary.Right);
                    break;

                case UnaryScalarExpressionInfo unary:
                    ResolveScalarExpressionDerivedAliases(result, unary.Operand);
                    break;
            }
        }

        private static void ResolveDerivedAliasColumn(ParsedQuery result, ref string alias, ref string column)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(alias) &&
                   !string.IsNullOrWhiteSpace(column) &&
                   seen.Add($"{alias}|{column}") &&
                   result.TryResolveDerivedColumn(alias, column, out var binding))
            {
                alias = binding.SourceAlias;
                column = binding.SourceColumn;
            }
        }

        private static string BuildSubqueryKey(SubqueryInfo subquery)
        {
            return string.Join("|",
                subquery.Operator,
                subquery.ParentTableAlias,
                subquery.ParentColumnName,
                subquery.SelectTableAlias,
                subquery.SelectColumn,
                subquery.SubquerySql);
        }

        private static string BuildSubqueryIdentityKey(SubqueryInfo subquery)
        {
            return string.Join("|",
                subquery.ParentTableAlias,
                subquery.ParentColumnName,
                subquery.SelectTableAlias,
                subquery.SelectColumn,
                subquery.SubquerySql);
        }

        private static string BuildTableIdentityKey(TableInfo table)
        {
            return string.Join("|", table.SchemaName, table.TableName, table.Alias, table.Role);
        }

        private static SubqueryOperator SelectPreferredSubqueryOperator(IEnumerable<SubqueryOperator> operators)
        {
            var operatorSet = operators.ToHashSet();

            if (operatorSet.Contains(SubqueryOperator.NotExists))
                return SubqueryOperator.NotExists;
            if (operatorSet.Contains(SubqueryOperator.NotIn))
                return SubqueryOperator.NotIn;
            if (operatorSet.Contains(SubqueryOperator.Exists))
                return SubqueryOperator.Exists;
            if (operatorSet.Contains(SubqueryOperator.In))
                return SubqueryOperator.In;
            if (operatorSet.Contains(SubqueryOperator.Any))
                return SubqueryOperator.Any;
            if (operatorSet.Contains(SubqueryOperator.All))
                return SubqueryOperator.All;

            return SubqueryOperator.ScalarComparison;
        }

        private static string BuildAggregateKey(AggregateInfo aggregate)
        {
            return string.Join("|",
                aggregate.Function,
                aggregate.TableAlias,
                aggregate.ColumnName,
                aggregate.Expression,
                aggregate.IsDistinct ? "1" : "0");
        }

        private void ValidateAndWarn(ParsedQuery result)
        {
            // Check for OR conditions (harder to generate data for)
            if (result.PredicateScopes.Any(s => ContainsOperator(s.Root, LogicalOp.Or)))
            {
                result.Warnings.Add("Query contains OR conditions — multiple data paths may be needed.");
            }

            // Check for correlated subqueries
            if (result.Subqueries.Any(s => s.IsCorrelated))
            {
                result.Warnings.Add("Correlated subqueries detected — data generation may be more complex.");
            }

            // Check for complex HAVING expressions
            if (result.HavingConditions.Any(c => c.AggregateFunc.HasValue && !string.IsNullOrEmpty(c.ExpressionText) && c.ExpressionText.Contains('*')))
            {
                result.Warnings.Add("HAVING with computed aggregate expressions — values will be approximated.");
            }

            // Check for nested subqueries
            if (result.Subqueries.Any(s => s.NestedSubqueries.Any()))
            {
                result.Warnings.Add("Nested subqueries detected — all levels will be analyzed.");
            }
        }

        private static bool ContainsOperator(PredicateExpression? expression, LogicalOp op)
        {
            return expression switch
            {
                PredicateBinaryExpression binary when binary.Operator == op => true,
                PredicateBinaryExpression binary => ContainsOperator(binary.Left, op) || ContainsOperator(binary.Right, op),
                PredicateNotExpression notExpr => ContainsOperator(notExpr.Inner, op),
                _ => false
            };
        }

        private bool IsAggregateFunction(string name) =>
            name.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MAX" or "MIN";

        private HashSet<string> ExtractCteNames(TSqlFragment fragment)
        {
            var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (fragment is not TSqlScript script)
                return cteNames;

            foreach (var batch in script.Batches)
            {
                foreach (var stmt in batch.Statements)
                {
                    if (stmt is SelectStatement selectStmt &&
                        selectStmt.WithCtesAndXmlNamespaces?.CommonTableExpressions != null)
                    {
                        foreach (var cte in selectStmt.WithCtesAndXmlNamespaces.CommonTableExpressions)
                        {
                            if (!string.IsNullOrWhiteSpace(cte.ExpressionName?.Value))
                            {
                                cteNames.Add(cte.ExpressionName.Value);
                            }
                        }
                    }
                }
            }

            return cteNames;
        }

        private Dictionary<string, Dictionary<string, DerivedColumnBinding>> CollectDerivedColumnMappings(TSqlFragment fragment)
        {
            var visitor = new DerivedTableCollectorVisitor();
            fragment.Accept(visitor);

            var mappings = new Dictionary<string, Dictionary<string, DerivedColumnBinding>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, spec) in ExtractCteQuerySpecifications(fragment))
            {
                var alias = name.StartsWith("CTE ", StringComparison.OrdinalIgnoreCase)
                    ? name[4..]
                    : name;
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                var bindings = BuildDerivedColumnBindings(alias, spec);
                if (bindings.Count > 0)
                {
                    mappings[alias] = bindings;
                }
            }

            foreach (var derivedTable in visitor.DerivedTables)
            {
                var alias = derivedTable.Alias?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                var bindings = BuildDerivedColumnBindings(alias, derivedTable.QueryExpression);
                if (bindings.Count > 0)
                {
                    mappings[alias] = bindings;
                }
            }

            return mappings;
        }

        private Dictionary<string, DerivedColumnBinding> BuildDerivedColumnBindings(string derivedAlias, QueryExpression? expression)
        {
            var bindings = new Dictionary<string, DerivedColumnBinding>(StringComparer.OrdinalIgnoreCase);
            var spec = EnumerateQuerySpecifications(expression).FirstOrDefault();
            if (spec == null)
                return bindings;

            var localDerivedMappings = new Dictionary<string, Dictionary<string, DerivedColumnBinding>>(StringComparer.OrdinalIgnoreCase);
            if (spec.FromClause != null)
            {
                foreach (var tableReference in spec.FromClause.TableReferences)
                {
                    CollectDerivedMappingsFromTableReference(tableReference, localDerivedMappings);
                }
            }

            foreach (var element in spec.SelectElements.OfType<SelectScalarExpression>())
            {
                var outputColumn = element.ColumnName?.Value;
                if (string.IsNullOrWhiteSpace(outputColumn) &&
                    element.Expression is ColumnReferenceExpression colRef)
                {
                    outputColumn = colRef.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                }

                if (string.IsNullOrWhiteSpace(outputColumn))
                    continue;

                if (!TryResolveExpressionSource(element.Expression, localDerivedMappings, out var sourceAlias, out var sourceColumn))
                    continue;

                bindings[outputColumn] = new DerivedColumnBinding
                {
                    DerivedAlias = derivedAlias,
                    OutputColumn = outputColumn,
                    SourceAlias = sourceAlias,
                    SourceColumn = sourceColumn,
                    SourceExpression = GetFragmentText(element.Expression)
                };
            }

            return bindings;
        }

        private void CollectDerivedMappingsFromTableReference(
            TableReference tableReference,
            Dictionary<string, Dictionary<string, DerivedColumnBinding>> mappings)
        {
            switch (tableReference)
            {
                case QueryDerivedTable derivedTable:
                {
                    var alias = derivedTable.Alias?.Value ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        var bindings = BuildDerivedColumnBindings(alias, derivedTable.QueryExpression);
                        if (bindings.Count > 0)
                        {
                            mappings[alias] = bindings;
                        }
                    }
                    break;
                }

                case QualifiedJoin qualifiedJoin:
                    CollectDerivedMappingsFromTableReference(qualifiedJoin.FirstTableReference, mappings);
                    CollectDerivedMappingsFromTableReference(qualifiedJoin.SecondTableReference, mappings);
                    break;

                case JoinParenthesisTableReference joinParen:
                    CollectDerivedMappingsFromTableReference(joinParen.Join, mappings);
                    break;
            }
        }

        private bool TryResolveExpressionSource(
            ScalarExpression expression,
            IReadOnlyDictionary<string, Dictionary<string, DerivedColumnBinding>> localDerivedMappings,
            out string sourceAlias,
            out string sourceColumn)
        {
            sourceAlias = string.Empty;
            sourceColumn = string.Empty;

            switch (expression)
            {
                case ColumnReferenceExpression colRef:
                {
                    var parts = colRef.MultiPartIdentifier?.Identifiers;
                    if (parts == null || parts.Count == 0)
                        return false;

                    var alias = parts.Count >= 2 ? parts[0].Value : string.Empty;
                    var column = parts[^1].Value;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (!string.IsNullOrWhiteSpace(alias) &&
                           localDerivedMappings.TryGetValue(alias, out var derivedColumns) &&
                           derivedColumns.TryGetValue(column, out var binding) &&
                           seen.Add($"{alias}|{column}"))
                    {
                        alias = binding.SourceAlias;
                        column = binding.SourceColumn;
                    }

                    sourceAlias = alias;
                    sourceColumn = column;
                    return !string.IsNullOrWhiteSpace(sourceColumn);
                }

                case ParenthesisExpression paren:
                    return TryResolveExpressionSource(paren.Expression, localDerivedMappings, out sourceAlias, out sourceColumn);

                case FunctionCall func when IsAggregateFunction(func.FunctionName?.Value ?? string.Empty) &&
                                            func.Parameters.Count > 0:
                    return TryResolveExpressionSource(func.Parameters[0], localDerivedMappings, out sourceAlias, out sourceColumn);
            }

            return false;
        }

        private static IEnumerable<QualifiedJoin> EnumerateQualifiedJoins(IEnumerable<TableReference> tableReferences)
        {
            foreach (var tableReference in tableReferences)
            {
                foreach (var join in EnumerateQualifiedJoins(tableReference))
                {
                    yield return join;
                }
            }
        }

        private static IEnumerable<QualifiedJoin> EnumerateQualifiedJoins(TableReference tableReference)
        {
            switch (tableReference)
            {
                case QualifiedJoin qualifiedJoin:
                    foreach (var nested in EnumerateQualifiedJoins(qualifiedJoin.FirstTableReference))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateQualifiedJoins(qualifiedJoin.SecondTableReference))
                    {
                        yield return nested;
                    }

                    yield return qualifiedJoin;
                    yield break;

                case JoinParenthesisTableReference joinParen:
                    foreach (var nested in EnumerateQualifiedJoins(joinParen.Join))
                    {
                        yield return nested;
                    }
                    yield break;
            }
        }

        private static string ResolveTableReferenceLabel(TableReference tableReference)
        {
            return tableReference switch
            {
                NamedTableReference named => named.Alias?.Value ?? named.SchemaObject.BaseIdentifier?.Value ?? "JOIN",
                QueryDerivedTable derived => derived.Alias?.Value ?? "Derived",
                QualifiedJoin nested => ResolveTableReferenceLabel(nested.SecondTableReference),
                _ => "JOIN"
            };
        }

        private string GetFragmentText(TSqlFragment node)
        {
            if (node.ScriptTokenStream != null && node.FirstTokenIndex >= 0 && node.LastTokenIndex >= 0)
            {
                var tokens = new List<string>();
                for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
                {
                    tokens.Add(node.ScriptTokenStream[i].Text);
                }
                return string.Join("", tokens).Trim();
            }
            return "";
        }

        private sealed class QuerySpecificationCollectorVisitor : TSqlFragmentVisitor
        {
            public List<QuerySpecification> QuerySpecifications { get; } = new();

            public override void Visit(QuerySpecification node)
            {
                QuerySpecifications.Add(node);
                base.Visit(node);
            }
        }

        private sealed class DerivedTableCollectorVisitor : TSqlFragmentVisitor
        {
            public List<QueryDerivedTable> DerivedTables { get; } = new();

            public override void Visit(QueryDerivedTable node)
            {
                DerivedTables.Add(node);
                base.Visit(node);
            }
        }

        private sealed class CteCollectorVisitor : TSqlFragmentVisitor
        {
            public List<CommonTableExpression> CommonTableExpressions { get; } = new();

            public override void Visit(CommonTableExpression node)
            {
                CommonTableExpressions.Add(node);
                base.Visit(node);
            }
        }
    }
}
