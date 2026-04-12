using Microsoft.Data.SqlClient;

namespace SqlTestDataGenerator.Database
{
    /// <summary>
    /// Manages database connections and stores connection configuration.
    /// </summary>
    public class DatabaseConnectionManager : IDisposable
    {
        private SqlConnection? _connection;
        public string? Server { get; private set; }
        public string? Database { get; private set; }
        public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;
        public string? ConnectionString { get; private set; }

        /// <summary>
        /// Connect using SQL Server authentication.
        /// </summary>
        public async Task<(bool success, string message)> ConnectAsync(
            string server, string database, string? username = null, string? password = null,
            bool useWindowsAuth = true)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = server,
                    InitialCatalog = database,
                    TrustServerCertificate = true,
                    ConnectTimeout = 10
                };

                if (useWindowsAuth)
                {
                    builder.IntegratedSecurity = true;
                }
                else
                {
                    builder.UserID = username ?? "";
                    builder.Password = password ?? "";
                    builder.IntegratedSecurity = false;
                }

                ConnectionString = builder.ConnectionString;
                _connection = new SqlConnection(ConnectionString);
                await _connection.OpenAsync();

                Server = server;
                Database = database;

                return (true, $"Connected to {server}/{database}");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Connect using a raw connection string.
        /// </summary>
        public async Task<(bool success, string message)> ConnectAsync(string connectionString)
        {
            try
            {
                ConnectionString = connectionString;
                _connection = new SqlConnection(connectionString);
                await _connection.OpenAsync();

                Server = _connection.DataSource;
                Database = _connection.Database;

                return (true, $"Connected to {Server}/{Database}");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get an open connection for use by other services.
        /// </summary>
        public SqlConnection GetConnection()
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("Not connected to database.");
            return _connection;
        }

        /// <summary>
        /// Create a new connection (for parallel operations).
        /// </summary>
        public SqlConnection CreateNewConnection()
        {
            if (string.IsNullOrEmpty(ConnectionString))
                throw new InvalidOperationException("No connection string configured.");

            var conn = new SqlConnection(ConnectionString);
            conn.Open();
            return conn;
        }

        public void Disconnect()
        {
            if (_connection != null)
            {
                if (_connection.State == System.Data.ConnectionState.Open)
                    _connection.Close();
                _connection.Dispose();
                _connection = null;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
