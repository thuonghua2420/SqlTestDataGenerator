using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration.Models
{
    public sealed class GenerationOptions
    {
        public int ExpectedResultRows { get; set; } = 1;
        public bool EnableRandomDiversity { get; set; }
        public bool EnableMaxBoundaryValues { get; set; }
        public bool EnsureAntiMatchRows { get; set; }
        public int JoinFanoutCount { get; set; } = 10;
    }

    public sealed class GenerationPlan
    {
        public GenerationOptions Options { get; set; } = new();
        public List<TablePlan> Tables { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    public sealed class TablePlan
    {
        public string TableName { get; set; } = string.Empty;
        public TableSchema? Schema { get; set; }
        public List<RowPlan> Rows { get; set; } = new();
    }

    public sealed class RowPlan
    {
        public string TableName { get; set; } = string.Empty;
        public RowRole Role { get; set; } = RowRole.Match;
        public int Ordinal { get; set; }
        public List<ColumnValuePlan> Columns { get; set; } = new();
    }

    public sealed class ColumnValuePlan
    {
        public string ColumnName { get; set; } = string.Empty;
        public ValueBinding Binding { get; set; } = ValueBinding.RandomDistinct;
        public object? Value { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public enum RowRole
    {
        Match,
        AntiMatch,
        JoinSupport,
        FkAncestor,
        AggregateSupport,
        UpdateBefore,
        UpdateAfter
    }

    public enum ValueBinding
    {
        Predicate,
        Relationship,
        BoundaryMax,
        RandomDistinct,
        AntiPredicate,
        ComputedSource
    }
}
