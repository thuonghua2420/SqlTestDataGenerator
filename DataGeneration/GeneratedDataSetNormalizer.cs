using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    /// <summary>
    /// Applies schema-aware normalization to generated rows so all downstream outputs
    /// use SQL Server-compatible runtime values.
    /// </summary>
    public class GeneratedDataSetNormalizer
    {
        public void Normalize(GeneratedDataSet dataSet, Dictionary<string, TableSchema> schemas)
        {
            foreach (var scenario in dataSet.Scenarios)
            {
                foreach (var tableRows in EnumerateAllTableRows(scenario))
                {
                    if (!schemas.TryGetValue(tableRows.Key, out var schema))
                        continue;

                    foreach (var row in tableRows.Value)
                    {
                        foreach (var kvp in row.ColumnValues.ToList())
                        {
                            var column = schema.GetColumn(kvp.Key);
                            if (column == null || column.IsComputed || column.IsStoreGenerated)
                                continue;

                            row.SetValue(kvp.Key, SqlServerValueNormalizer.NormalizeValue(column, kvp.Value));
                        }
                    }
                }
            }
        }

        private static IEnumerable<KeyValuePair<string, List<GeneratedRow>>> EnumerateAllTableRows(BranchScenario scenario)
        {
            foreach (var tableRows in scenario.TableRows)
                yield return tableRows;

            foreach (var tableRows in scenario.AntiMatchRows)
                yield return tableRows;
        }
    }
}
