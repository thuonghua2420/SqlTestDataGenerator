namespace SqlTestDataGenerator.Parsing.Models
{
    public abstract class PredicateExpression
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class PredicateLeafExpression : PredicateExpression
    {
        public ConditionInfo Condition { get; set; } = new();
    }

    public sealed class PredicateBinaryExpression : PredicateExpression
    {
        public LogicalOp Operator { get; set; }
        public PredicateExpression Left { get; set; } = null!;
        public PredicateExpression Right { get; set; } = null!;
    }

    public sealed class PredicateNotExpression : PredicateExpression
    {
        public PredicateExpression Inner { get; set; } = null!;
    }

    public sealed class PredicateScope
    {
        public string ScopeId { get; set; } = string.Empty;
        public string ScopeLabel { get; set; } = string.Empty;
        public ConditionSource Source { get; set; }
        public PredicateExpression? Root { get; set; }
        public List<ConditionInfo> Conditions { get; set; } = new();
    }
}
