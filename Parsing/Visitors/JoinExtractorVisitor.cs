using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTestDataGenerator.Parsing.Models;
using JoinType = SqlTestDataGenerator.Parsing.Models.JoinType;

namespace SqlTestDataGenerator.Parsing.Visitors
{
    /// <summary>
    /// Extracts JOIN information including join type and ON conditions.
    /// Prefers the first column-to-column comparison as the primary relationship and
    /// keeps all ON predicates so generation can satisfy literal filters too.
    /// </summary>
    public class JoinExtractorVisitor : TSqlFragmentVisitor
    {
        public List<JoinInfo> Joins { get; } = new();

        public override void Visit(QualifiedJoin node)
        {
            var joinInfo = new JoinInfo
            {
                Type = ConvertJoinType(node.QualifiedJoinType)
            };

            ExtractTableFromReference(node.FirstTableReference, joinInfo, isLeft: true);
            ExtractTableFromReference(node.SecondTableReference, joinInfo, isLeft: false);

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
            var joinInfo = new JoinInfo { Type = JoinType.Cross };
            ExtractTableFromReference(node.FirstTableReference, joinInfo, isLeft: true);
            ExtractTableFromReference(node.SecondTableReference, joinInfo, isLeft: false);
            Joins.Add(joinInfo);
            base.Visit(node);
        }

        private void ExtractTableFromReference(TableReference tableRef, JoinInfo joinInfo, bool isLeft)
        {
            switch (tableRef)
            {
                case NamedTableReference named:
                {
                    var tableName = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier?.Value ?? string.Empty;
                    if (isLeft)
                        joinInfo.LeftTableAlias = tableName;
                    else
                        joinInfo.RightTableAlias = tableName;
                    break;
                }

                case QueryDerivedTable derived:
                {
                    var alias = derived.Alias?.Value ?? string.Empty;
                    if (isLeft)
                        joinInfo.LeftTableAlias = alias;
                    else
                        joinInfo.RightTableAlias = alias;
                    break;
                }

                case QualifiedJoin nested when isLeft:
                    ExtractTableFromReference(nested.SecondTableReference, joinInfo, isLeft: true);
                    break;
            }
        }

        private bool ExtractJoinCondition(BooleanExpression condition, JoinInfo joinInfo)
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
                    return true;
                }

                return false;
            }

            if (condition is BooleanBinaryExpression binary)
            {
                var foundPrimary = ExtractJoinCondition(binary.FirstExpression, joinInfo) ||
                                   ExtractJoinCondition(binary.SecondExpression, joinInfo);

                var conditionVisitor = new ConditionExtractorVisitor(ConditionSource.JoinOn);
                condition.Accept(conditionVisitor);
                joinInfo.AdditionalOnConditions.AddRange(conditionVisitor.Conditions);
                return foundPrimary;
            }

            return false;
        }

        private static (string alias, string column) ExtractColumnRef(ScalarExpression expr)
        {
            if (expr is ColumnReferenceExpression colRef)
            {
                var parts = colRef.MultiPartIdentifier?.Identifiers;
                if (parts != null && parts.Count >= 2)
                    return (parts[0].Value, parts[1].Value);
                if (parts != null && parts.Count == 1)
                    return (string.Empty, parts[0].Value);
            }

            return (string.Empty, string.Empty);
        }

        private static JoinType ConvertJoinType(QualifiedJoinType sqlJoinType) => sqlJoinType switch
        {
            QualifiedJoinType.Inner => JoinType.Inner,
            QualifiedJoinType.LeftOuter => JoinType.Left,
            QualifiedJoinType.RightOuter => JoinType.Right,
            QualifiedJoinType.FullOuter => JoinType.Full,
            _ => JoinType.Inner
        };

        private static string GetFragmentText(TSqlFragment fragment)
        {
            if (fragment.ScriptTokenStream == null || fragment.FirstTokenIndex < 0 || fragment.LastTokenIndex < 0)
                return string.Empty;

            var tokens = new List<string>();
            for (int i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            {
                tokens.Add(fragment.ScriptTokenStream[i].Text);
            }

            return string.Join("", tokens).Trim();
        }
    }
}
