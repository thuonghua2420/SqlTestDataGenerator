using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts subqueries from the SQL AST, including nested subqueries.
    /// Handles: IN (subquery), NOT IN (subquery), EXISTS (subquery), 
    /// scalar subqueries in comparisons, subqueries in FROM (derived tables).
    /// </summary>
    public class SubqueryExtractorVisitor : TSqlFragmentVisitor
    {
        public List<SubqueryInfo> Subqueries { get; } = new();
        private int _nextId = 1;
        private int _currentNestingLevel;

        // ─── IN (subquery) / NOT IN (subquery) ─────────────────────────
        public override void Visit(InPredicate node)
        {
            if (node.Subquery != null)
            {
                var subInfo = new SubqueryInfo
                {
                    Id = _nextId++,
                    Context = SubqueryContext.WhereClause,
                    Operator = node.NotDefined ? SubqueryOperator.NotIn : SubqueryOperator.In,
                    NestingLevel = _currentNestingLevel,
                    SubquerySql = GetFragmentText(node.Subquery)
                };

                // Extract parent column reference
                ExtractParentColumn(node.Expression, subInfo);

                // Parse the subquery itself
                ParseSubqueryContent(node.Subquery, subInfo);

                Subqueries.Add(subInfo);
            }
            // Don't call base — we don't want to double-process
        }

        // ─── EXISTS (subquery) ──────────────────────────────────────────
        public override void Visit(ExistsPredicate node)
        {
            var subInfo = new SubqueryInfo
            {
                Id = _nextId++,
                Context = SubqueryContext.ExistsCheck,
                Operator = SubqueryOperator.Exists,
                NestingLevel = _currentNestingLevel,
                SubquerySql = GetFragmentText(node.Subquery)
            };

            ParseSubqueryContent(node.Subquery, subInfo);
            Subqueries.Add(subInfo);
        }

        // ─── NOT EXISTS ─────────────────────────────────────────────────
        public override void Visit(BooleanNotExpression node)
        {
            if (node.Expression is ExistsPredicate existsPred)
            {
                var subInfo = new SubqueryInfo
                {
                    Id = _nextId++,
                    Context = SubqueryContext.ExistsCheck,
                    Operator = SubqueryOperator.NotExists,
                    NestingLevel = _currentNestingLevel,
                    SubquerySql = GetFragmentText(existsPred.Subquery)
                };

                ParseSubqueryContent(existsPred.Subquery, subInfo);
                Subqueries.Add(subInfo);
            }
            else
            {
                base.Visit(node);
            }
        }

        // ─── Scalar subquery in comparison (e.g., col = (SELECT ...)) ──
        public override void Visit(ScalarSubquery node)
        {
            var subInfo = new SubqueryInfo
            {
                Id = _nextId++,
                Context = SubqueryContext.WhereClause,
                Operator = SubqueryOperator.ScalarComparison,
                NestingLevel = _currentNestingLevel,
                SubquerySql = GetFragmentText(node)
            };

            if (node.QueryExpression is QuerySpecification spec)
            {
                ParseQuerySpec(spec, subInfo);
            }

            Subqueries.Add(subInfo);
        }

        // ═════════════════════════════════════════════════════════════════
        // Internal parsing helpers
        // ═════════════════════════════════════════════════════════════════

        private void ParseSubqueryContent(ScalarSubquery subquery, SubqueryInfo subInfo)
        {
            if (subquery.QueryExpression is QuerySpecification spec)
            {
                ParseQuerySpec(spec, subInfo);
            }
        }

        private void ParseQuerySpec(QuerySpecification spec, SubqueryInfo subInfo)
        {
            // Extract SELECT column
            if (spec.SelectElements.Count > 0)
            {
                var firstSelect = spec.SelectElements[0];
                if (firstSelect is SelectScalarExpression scalarExpr)
                {
                    if (scalarExpr.Expression is ColumnReferenceExpression colRef)
                    {
                        var parts = colRef.MultiPartIdentifier?.Identifiers;
                        if (parts != null)
                        {
                            if (parts.Count >= 2)
                            {
                                subInfo.SelectTableAlias = parts[0].Value;
                                subInfo.SelectColumn = parts[1].Value;
                            }
                            else if (parts.Count == 1)
                            {
                                subInfo.SelectColumn = parts[0].Value;
                            }
                        }
                    }
                }
            }

            // Extract tables from subquery
            var tableVisitor = new TableExtractorVisitor();
            spec.FromClause?.Accept(tableVisitor);
            subInfo.Tables.AddRange(tableVisitor.Tables);

            // Extract WHERE conditions from subquery
            if (spec.WhereClause != null)
            {
                var condVisitor = new ConditionExtractorVisitor(ConditionSource.SubqueryWhere);
                spec.WhereClause.Accept(condVisitor);
                subInfo.Conditions.AddRange(condVisitor.Conditions);
            }

            // Check for nested subqueries
            _currentNestingLevel++;
            var nestedVisitor = new SubqueryExtractorVisitor
            {
                _currentNestingLevel = this._currentNestingLevel
            };
            // Visit WHERE for nested subqueries
            if (spec.WhereClause != null)
            {
                spec.WhereClause.Accept(nestedVisitor);
                subInfo.NestedSubqueries.AddRange(nestedVisitor.Subqueries);
            }
            _currentNestingLevel--;

            // Check if correlated (references parent table aliases)
            // This is a simplified check - it looks for column references
            // that don't match any table alias in the subquery
            var subTableAliases = subInfo.Tables
                .Select(t => t.EffectiveName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var cond in subInfo.Conditions)
            {
                if (!string.IsNullOrEmpty(cond.TableAlias) &&
                    !subTableAliases.Contains(cond.TableAlias))
                {
                    subInfo.IsCorrelated = true;
                    break;
                }
            }
        }

        private void ExtractParentColumn(ScalarExpression expr, SubqueryInfo subInfo)
        {
            if (expr is ColumnReferenceExpression colRef)
            {
                var parts = colRef.MultiPartIdentifier?.Identifiers;
                if (parts != null)
                {
                    if (parts.Count >= 2)
                    {
                        subInfo.ParentTableAlias = parts[0].Value;
                        subInfo.ParentColumnName = parts[1].Value;
                    }
                    else if (parts.Count == 1)
                    {
                        subInfo.ParentColumnName = parts[0].Value;
                    }
                }
            }
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
    }
}
