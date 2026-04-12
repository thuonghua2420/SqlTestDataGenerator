using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts aggregate function usage from the SQL query.
    /// Used to understand HAVING conditions and SELECT aggregates.
    /// </summary>
    public class AggregateExtractorVisitor : TSqlFragmentVisitor
    {
        public List<AggregateInfo> Aggregates { get; } = new();

        public override void Visit(FunctionCall node)
        {
            var funcName = node.FunctionName?.Value?.ToUpperInvariant() ?? "";
            var aggFunc = funcName switch
            {
                "COUNT" => (AggregateFunction?)AggregateFunction.Count,
                "SUM" => AggregateFunction.Sum,
                "AVG" => AggregateFunction.Avg,
                "MAX" => AggregateFunction.Max,
                "MIN" => AggregateFunction.Min,
                _ => null
            };

            if (aggFunc.HasValue)
            {
                var info = new AggregateInfo
                {
                    Function = aggFunc.Value,
                    IsDistinct = node.UniqueRowFilter == UniqueRowFilter.Distinct
                };

                if (aggFunc == AggregateFunction.Count && node.UniqueRowFilter == UniqueRowFilter.Distinct)
                {
                    info.Function = AggregateFunction.CountDistinct;
                }

                // Extract column reference(s) from parameters
                if (node.Parameters.Count > 0)
                {
                    var param = node.Parameters[0];
                    if (param is ColumnReferenceExpression colRef)
                    {
                        var parts = colRef.MultiPartIdentifier?.Identifiers;
                        if (parts != null && parts.Count >= 2)
                        {
                            info.TableAlias = parts[0].Value;
                            info.ColumnName = parts[1].Value;
                        }
                        else if (parts != null && parts.Count == 1)
                        {
                            info.ColumnName = parts[0].Value;
                        }
                    }
                    else
                    {
                        // Complex expression like SUM(oi.quantity * oi.unit_price)
                        info.Expression = GetFragmentText(param);
                    }
                }

                Aggregates.Add(info);
            }

            base.Visit(node);
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
