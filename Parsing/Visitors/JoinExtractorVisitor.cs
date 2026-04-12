using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;
using JoinType = SqlTestDataGenerator.Parsing.Models.JoinType;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts JOIN information including join type and ON conditions.
    /// Also updates TableInfo.Role for joined tables.
    /// </summary>
    public class JoinExtractorVisitor : TSqlFragmentVisitor
    {
        public List<JoinInfo> Joins { get; } = new();

        public override void Visit(QualifiedJoin node)
        {
            var joinInfo = new JoinInfo
            {
                Type = ConvertJoinType(node.QualifiedJoinType),
            };

            // Extract left and right table references
            ExtractTableFromReference(node.FirstTableReference, joinInfo, isLeft: true);
            ExtractTableFromReference(node.SecondTableReference, joinInfo, isLeft: false);

            // Extract ON condition
            if (node.SearchCondition != null)
            {
                joinInfo.OnConditionText = GetFragmentText(node.SearchCondition);
                ExtractJoinCondition(node.SearchCondition, joinInfo);
            }

            Joins.Add(joinInfo);
            base.Visit(node);
        }

        public override void Visit(UnqualifiedJoin node)
        {
            // CROSS JOIN
            var joinInfo = new JoinInfo { Type = JoinType.Cross };
            ExtractTableFromReference(node.FirstTableReference, joinInfo, isLeft: true);
            ExtractTableFromReference(node.SecondTableReference, joinInfo, isLeft: false);
            Joins.Add(joinInfo);
            base.Visit(node);
        }

        private void ExtractTableFromReference(TableReference tableRef, JoinInfo joinInfo, bool isLeft)
        {
            if (tableRef is NamedTableReference named)
            {
                var tableName = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier?.Value ?? "";
                if (isLeft)
                    joinInfo.LeftTableAlias = tableName;
                else
                    joinInfo.RightTableAlias = tableName;
            }
            else if (tableRef is QualifiedJoin nested)
            {
                // For chained joins, the left side is the deepest table
                if (isLeft)
                {
                    ExtractTableFromReference(nested.SecondTableReference, joinInfo, isLeft: true);
                }
            }
        }

        private void ExtractJoinCondition(BooleanExpression condition, JoinInfo joinInfo)
        {
            if (condition is BooleanComparisonExpression comp)
            {
                var (leftAlias, leftCol) = ExtractColumnRef(comp.FirstExpression);
                var (rightAlias, rightCol) = ExtractColumnRef(comp.SecondExpression);

                if (!string.IsNullOrEmpty(leftCol) && !string.IsNullOrEmpty(rightCol))
                {
                    joinInfo.LeftTableAlias = leftAlias;
                    joinInfo.LeftColumn = leftCol;
                    joinInfo.RightTableAlias = rightAlias;
                    joinInfo.RightColumn = rightCol;
                }
            }
            else if (condition is BooleanBinaryExpression binary)
            {
                // Handle AND/OR in ON clause — take the first simple condition as primary
                ExtractJoinCondition(binary.FirstExpression, joinInfo);

                // Additional conditions go into AdditionalOnConditions
                var additionalConditionVisitor = new ConditionExtractorVisitor();
                binary.SecondExpression.Accept(additionalConditionVisitor);
                joinInfo.AdditionalOnConditions.AddRange(additionalConditionVisitor.Conditions);
            }
        }

        private (string alias, string column) ExtractColumnRef(ScalarExpression expr)
        {
            if (expr is ColumnReferenceExpression colRef)
            {
                var parts = colRef.MultiPartIdentifier?.Identifiers;
                if (parts != null && parts.Count >= 2)
                    return (parts[0].Value, parts[1].Value);
                if (parts != null && parts.Count == 1)
                    return ("", parts[0].Value);
            }
            return ("", "");
        }

        private JoinType ConvertJoinType(QualifiedJoinType sqlJoinType) => sqlJoinType switch
        {
            QualifiedJoinType.Inner => JoinType.Inner,
            QualifiedJoinType.LeftOuter => JoinType.Left,
            QualifiedJoinType.RightOuter => JoinType.Right,
            QualifiedJoinType.FullOuter => JoinType.Full,
            _ => JoinType.Inner
        };

        private string GetFragmentText(TSqlFragment fragment)
        {
            if (fragment.StartOffset >= 0 && fragment.FragmentLength > 0)
            {
                // Fragment text extraction would need the original SQL; 
                // we'll populate this from the parser service
                return "";
            }
            return "";
        }
    }
}
