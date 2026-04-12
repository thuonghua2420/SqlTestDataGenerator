using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Schema.Models;

namespace SqlTestDataGenerator.DataGeneration
{
    /// <summary>
    /// Resolves table INSERT order based on foreign key dependencies.
    /// Uses topological sort to ensure parent tables are inserted before children.
    /// </summary>
    public class DependencyOrderResolver
    {
        /// <summary>
        /// Given a set of table names and their schemas, return the correct INSERT order.
        /// Tables with no FK dependencies come first.
        /// </summary>
        public List<string> ResolveInsertOrder(
            IEnumerable<string> tableNames,
            Dictionary<string, TableSchema> schemas)
        {
            var tables = tableNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // Build dependency graph: table → set of tables it depends on
            foreach (var tableName in tables)
            {
                graph[tableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (schemas.TryGetValue(tableName, out var schema))
                {
                    foreach (var fk in schema.ForeignKeys)
                    {
                        // Only add dependency if the referenced table is in our set
                        if (tables.Contains(fk.ReferencedTable) &&
                            !fk.ReferencedTable.Equals(tableName, StringComparison.OrdinalIgnoreCase)) // Avoid self-ref
                        {
                            graph[tableName].Add(fk.ReferencedTable);
                        }
                    }
                }
            }

            // Topological sort (Kahn's algorithm)
            return TopologicalSort(graph);
        }

        /// <summary>
        /// Resolve INSERT order using JOIN relationships when schema info is not available.
        /// Assumes the left side of a JOIN is the "parent" table.
        /// </summary>
        public List<string> ResolveFromJoins(ParsedQuery query)
        {
            var tables = query.Tables.Select(t => t.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
                graph[table] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var join in query.Joins)
            {
                var leftTable = query.ResolveAlias(join.LeftTableAlias);
                var rightTable = query.ResolveAlias(join.RightTableAlias);

                if (tables.Contains(leftTable) && tables.Contains(rightTable))
                {
                    // Right table depends on left table (in typical JOIN patterns)
                    graph[rightTable].Add(leftTable);
                }
            }

            return TopologicalSort(graph);
        }

        /// <summary>
        /// Returns the reverse order (for DELETE/cleanup scripts).
        /// </summary>
        public List<string> ResolveDeleteOrder(
            IEnumerable<string> tableNames,
            Dictionary<string, TableSchema> schemas)
        {
            var order = ResolveInsertOrder(tableNames, schemas);
            order.Reverse();
            return order;
        }

        private List<string> TopologicalSort(Dictionary<string, HashSet<string>> graph)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in graph.Keys)
            {
                if (!visited.Contains(node))
                {
                    DFS(node, graph, visited, visiting, result);
                }
            }

            return result;
        }

        private void DFS(string node, Dictionary<string, HashSet<string>> graph,
            HashSet<string> visited, HashSet<string> visiting, List<string> result)
        {
            if (visiting.Contains(node))
            {
                // Circular dependency — just return (the table will still be in the result)
                return;
            }

            if (visited.Contains(node))
                return;

            visiting.Add(node);

            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    DFS(dep, graph, visited, visiting, result);
                }
            }

            visiting.Remove(node);
            visited.Add(node);
            result.Add(node);
        }
    }
}
