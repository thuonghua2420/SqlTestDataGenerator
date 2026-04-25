namespace SqlTestDataGenerator.Parsing.Models
{
    public abstract class ScalarExpressionInfo
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class ColumnScalarExpressionInfo : ScalarExpressionInfo
    {
        public string TableAlias { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
    }

    public sealed class LiteralScalarExpressionInfo : ScalarExpressionInfo
    {
        public string Value { get; set; } = string.Empty;
        public ScalarLiteralKind Kind { get; set; }
    }

    public sealed class FunctionScalarExpressionInfo : ScalarExpressionInfo
    {
        public string Name { get; set; } = string.Empty;
        public List<ScalarExpressionInfo> Arguments { get; set; } = new();
    }

    public sealed class BinaryScalarExpressionInfo : ScalarExpressionInfo
    {
        public ScalarBinaryOperator Operator { get; set; }
        public ScalarExpressionInfo? Left { get; set; }
        public ScalarExpressionInfo? Right { get; set; }
    }

    public sealed class UnaryScalarExpressionInfo : ScalarExpressionInfo
    {
        public string Operator { get; set; } = string.Empty;
        public ScalarExpressionInfo? Operand { get; set; }
    }

    public enum ScalarLiteralKind
    {
        String,
        Integer,
        Numeric,
        Real,
        Money,
        Null,
        Binary,
        Default,
        Variable,
        Other
    }

    public enum ScalarBinaryOperator
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
        BitwiseAnd,
        BitwiseOr,
        BitwiseXor,
        Unknown
    }
}
