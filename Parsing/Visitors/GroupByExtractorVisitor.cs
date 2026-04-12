using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts GROUP BY columns from the SQL query.
    /// </summary>
    public class GroupByExtractorVisitor : TSqlFragmentVisitor
    {
        public List<GroupByColumn> GroupByColumns { get; } = new();

        public override void Visit(GroupByClause node)
        {
            foreach (var grouping in node.GroupingSpecifications)
            {
                if (grouping is ExpressionGroupingSpecification exprGroup)
                {
                    if (exprGroup.Expression is ColumnReferenceExpression colRef)
                    {
                        var parts = colRef.MultiPartIdentifier?.Identifiers;
                        if (parts != null)
                        {
                            var col = new GroupByColumn();
                            if (parts.Count >= 2)
                            {
                                col.TableAlias = parts[0].Value;
                                col.ColumnName = parts[1].Value;
                            }
                            else if (parts.Count == 1)
                            {
                                col.ColumnName = parts[0].Value;
                            }
                            GroupByColumns.Add(col);
                        }
                    }
                }
            }
            base.Visit(node);
        }
    }
}
