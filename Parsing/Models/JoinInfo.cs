namespace SqlTestDataGenerator.Parsing.Models
{
    /// <summary>
    /// Represents a JOIN relationship between two tables.
    /// </summary>
    public class JoinInfo
    {
        /// <summary>Type of join</summary>
        public JoinType Type { get; set; }

        /// <summary>Left table alias or name</summary>
        public string LeftTableAlias { get; set; } = string.Empty;

        /// <summary>Left column in join condition</summary>
        public string LeftColumn { get; set; } = string.Empty;

        /// <summary>Right table alias or name</summary>
        public string RightTableAlias { get; set; } = string.Empty;

        /// <summary>Right column in join condition</summary>
        public string RightColumn { get; set; } = string.Empty;

        /// <summary>Full ON condition text for complex join conditions</summary>
        public string OnConditionText { get; set; } = string.Empty;

        /// <summary>Additional conditions in ON clause (beyond the simple column match)</summary>
        public List<ConditionInfo> AdditionalOnConditions { get; set; } = new();

        public override string ToString() =>
            $"{Type} JOIN {RightTableAlias}: {LeftTableAlias}.{LeftColumn} = {RightTableAlias}.{RightColumn}";
    }

    public enum JoinType
    {
        Inner,
        Left,
        Right,
        Full,
        Cross
    }
}
