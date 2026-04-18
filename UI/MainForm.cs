using SqlTestDataGenerator.Database;
using SqlTestDataGenerator.DataGeneration;
using SqlTestDataGenerator.Output;
using SqlTestDataGenerator.Parsing;
using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;
using System.Drawing.Drawing2D;

namespace SqlTestDataGenerator.UI
{
    public class MainForm : Form
    {
        // ── Services ──
        private readonly SqlParserService _parser = new();
        private readonly BranchCoverageAnalyzer _branchAnalyzer = new();
        private readonly DataGenerationEngine _dataEngine = new();
        private readonly InsertScriptGenerator _insertGenerator = new();
        private readonly CleanupScriptGenerator _cleanupGenerator = new();
        private readonly DependencyOrderResolver _orderResolver = new();
        private DatabaseConnectionManager? _connectionManager;
        private SchemaIntrospector? _schemaIntrospector;

        // ── State ──
        private ParsedQuery? _currentQuery;
        private Dictionary<string, TableSchema>? _schemas;
        private DataGeneration.Models.GeneratedDataSet? _currentDataSet;

        // ── UI Controls ──
        private Panel _headerPanel = null!;
        private Label _titleLabel = null!;
        private Label _connectionStatusLabel = null!;
        private Button _connectBtn = null!;
        private Button _disconnectBtn = null!;

        private SplitContainer _mainSplitter = null!;
        private SplitContainer _topSplitter = null!;
        private SplitContainer _bottomSplitter = null!;

        private TextBox _sqlInput = null!;
        private TextBox _analysisOutput = null!;
        private TextBox _scriptCleanOutput = null!;
        private TextBox _scriptOutput = null!;

        private Button _analyzeBtn = null!;
        private Button _generateBtn = null!;
        private Button _copyBtn = null!;
        private Button _saveBtn = null!;
        private Button _copyCleanupBtn = null!;

        private Panel _toolbarPanel = null!;
        private Panel _bottomToolbar = null!;

        private CheckedListBox _scenarioList = null!;
        private Label _statusLabel = null!;
        private NumericUpDown _startIdInput = null!;
        private NumericUpDown _rowsPerTableInput = null!;

        // ── Colors (Standard Light Theme) ──
        private readonly Color _bgDark = SystemColors.Control;
        private readonly Color _bgPanel = SystemColors.Control;
        private readonly Color _bgInput = Color.White;
        private readonly Color _bgHeader = SystemColors.Control;
        private readonly Color _accentBlue = Color.FromArgb(0, 120, 215);
        private readonly Color _accentGreen = Color.FromArgb(16, 124, 16);
        private readonly Color _accentOrange = Color.FromArgb(180, 100, 0);
        private readonly Color _accentRed = Color.FromArgb(200, 50, 50);
        private readonly Color _textPrimary = SystemColors.ControlText;
        private readonly Color _textSecondary = SystemColors.GrayText;
        private readonly Color _borderColor = SystemColors.ControlDark;

        public MainForm()
        {
            InitializeComponent();
            ApplyTheme();
        }

        private void InitializeComponent()
        {
            this.Text = "SQL Test Data Generator";
            this.Size = new Size(1400, 900);
            this.MinimumSize = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 10F);
            this.DoubleBuffered = true;

            // ═══ Header Panel ═══
            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(16, 8, 16, 8)
            };

            _titleLabel = new Label
            {
                Text = "⚡ SQL Test Data Generator",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 14)
            };

            _connectionStatusLabel = new Label
            {
                Text = "● Not Connected",
                Font = new Font("Segoe UI", 9F),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };

            _connectBtn = CreateButton("🔌 Connect", _accentBlue, 90);
            _connectBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _connectBtn.Click += ConnectBtn_Click;

            _disconnectBtn = CreateButton("✖ Disconnect", _accentRed, 100);
            _disconnectBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _disconnectBtn.Visible = false;
            _disconnectBtn.Click += DisconnectBtn_Click;

            _headerPanel.Controls.AddRange(new Control[] { _titleLabel, _connectionStatusLabel, _connectBtn, _disconnectBtn });
            _headerPanel.Resize += HeaderPanel_Resize;

            // ═══ Toolbar Panel ═══
            _toolbarPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                Padding = new Padding(16, 8, 16, 8)
            };

            _analyzeBtn = CreateButton("🔍 Analyze SQL", _accentBlue, 130);
            _analyzeBtn.Location = new Point(16, 8);
            _analyzeBtn.Click += AnalyzeBtn_Click;

            _generateBtn = CreateButton("⚡ Generate Data", _accentGreen, 140);
            _generateBtn.Location = new Point(160, 8);
            _generateBtn.Enabled = false;
            _generateBtn.Click += GenerateBtn_Click;

            var startIdLabel = new Label
            {
                Text = "Start ID:",
                AutoSize = true,
                Location = new Point(320, 13),
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9F)
            };

            _startIdInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 9999999,
                Value = 90000,
                Increment = 1000,
                Location = new Point(385, 9),
                Width = 90,
                Font = new Font("Segoe UI", 9F),
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                BorderStyle = BorderStyle.FixedSingle
            };

            var rowsPerTableLabel = new Label
            {
                Text = "Rows/Table:",
                AutoSize = true,
                Location = new Point(490, 13),
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9F)
            };

            _rowsPerTableInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000,
                Value = 1,
                Increment = 1,
                Location = new Point(565, 9),
                Width = 70,
                Font = new Font("Segoe UI", 9F),
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                BorderStyle = BorderStyle.FixedSingle
            };

            _toolbarPanel.Controls.AddRange(new Control[] { _analyzeBtn, _generateBtn, startIdLabel, _startIdInput, rowsPerTableLabel, _rowsPerTableInput });

            // ═══ Main Content ═══
            _mainSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 350,
                BackColor = _borderColor,
                SplitterWidth = 3
            };

            // Top: SQL Input + Analysis
            _topSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 500,
                BackColor = _borderColor,
                SplitterWidth = 3
            };

            // SQL Input Panel
            var sqlPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var sqlLabel = new Label
            {
                Text = "📝 SQL Input",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = _accentBlue,
                Padding = new Padding(4, 6, 0, 0)
            };
            _sqlInput = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 10F),
                AcceptsReturn = true,
                AcceptsTab = true
            };
            sqlPanel.Controls.Add(_sqlInput);
            sqlPanel.Controls.Add(sqlLabel);

            // Analysis Panel
            var analysisPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var analysisLabel = new Label
            {
                Text = "📊 Analysis Result",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = _accentGreen,
                Padding = new Padding(4, 6, 0, 0)
            };

            _scenarioList = new CheckedListBox
            {
                Dock = DockStyle.Bottom,
                Height = 130,
                Font = new Font("Segoe UI", 9F),
                CheckOnClick = true,
                BorderStyle = BorderStyle.None,
            };

            var scenarioLabel = new Label
            {
                Text = "☑ Scenarios to generate:",
                Dock = DockStyle.Bottom,
                Height = 22,
                Font = new Font("Segoe UI Semibold", 9F),
                ForeColor = _accentOrange,
                Padding = new Padding(4, 4, 0, 0)
            };

            _analysisOutput = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = true,
                Font = new Font("Cascadia Code, Consolas", 9.5F),
            };

            analysisPanel.Controls.Add(_analysisOutput);
            analysisPanel.Controls.Add(scenarioLabel);
            analysisPanel.Controls.Add(_scenarioList);
            analysisPanel.Controls.Add(analysisLabel);

            _topSplitter.Panel1.Controls.Add(sqlPanel);
            _topSplitter.Panel2.Controls.Add(analysisPanel);

            // Bottom: Split into Clean INSERT (left) + Full Log (right)
            _bottomSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 500,
                BackColor = SystemColors.ControlDark,
                SplitterWidth = 3
            };

            // Left: Clean INSERT only
            var cleanPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var cleanLabel = new Label
            {
                Text = "📜 INSERT Scripts",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = _accentOrange,
                Padding = new Padding(4, 6, 0, 0)
            };
            _scriptCleanOutput = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 9.5F),
            };
            cleanPanel.Controls.Add(_scriptCleanOutput);
            cleanPanel.Controls.Add(cleanLabel);

            // Right: Full log (with comments, transactions)
            var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var logLabel = new Label
            {
                Text = "📋 Full Log",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = _accentBlue,
                Padding = new Padding(4, 6, 0, 0)
            };
            _scriptOutput = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 9.5F),
            };
            logPanel.Controls.Add(_scriptOutput);
            logPanel.Controls.Add(logLabel);

            _bottomSplitter.Panel1.Controls.Add(cleanPanel);
            _bottomSplitter.Panel2.Controls.Add(logPanel);

            _mainSplitter.Panel1.Controls.Add(_topSplitter);
            _mainSplitter.Panel2.Controls.Add(_bottomSplitter);

            // ═══ Bottom Toolbar ═══
            _bottomToolbar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(16, 8, 16, 8)
            };

            _copyBtn = CreateButton("📋 Copy INSERT", _accentBlue, 120);
            _copyBtn.Location = new Point(16, 8);
            _copyBtn.Click += CopyBtn_Click;

            _copyCleanupBtn = CreateButton("🧹 Copy Cleanup", _accentOrange, 130);
            _copyCleanupBtn.Location = new Point(150, 8);
            _copyCleanupBtn.Click += CopyCleanupBtn_Click;

            _saveBtn = CreateButton("💾 Save .sql", _accentGreen, 110);
            _saveBtn.Location = new Point(294, 8);
            _saveBtn.Click += SaveBtn_Click;

            _statusLabel = new Label
            {
                Text = "Ready",
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textSecondary
            };

            _bottomToolbar.Controls.AddRange(new Control[] { _copyBtn, _copyCleanupBtn, _saveBtn, _statusLabel });
            _bottomToolbar.Resize += BottomToolbar_Resize;

            // ═══ Add to form ═══
            this.Controls.Add(_mainSplitter);
            this.Controls.Add(_bottomToolbar);
            this.Controls.Add(_toolbarPanel);
            this.Controls.Add(_headerPanel);
        }

        private void ApplyTheme()
        {
            this.BackColor = SystemColors.Control;
            this.ForeColor = SystemColors.ControlText;

            _headerPanel.BackColor = SystemColors.Control;
            _toolbarPanel.BackColor = SystemColors.Control;
            _bottomToolbar.BackColor = SystemColors.Control;

            _titleLabel.ForeColor = SystemColors.ControlText;
            _connectionStatusLabel.ForeColor = _accentRed;

            _sqlInput.BackColor = SystemColors.Window;
            _sqlInput.ForeColor = SystemColors.WindowText;
            _sqlInput.BorderStyle = BorderStyle.FixedSingle;

            _analysisOutput.BackColor = SystemColors.Window;
            _analysisOutput.ForeColor = SystemColors.WindowText;
            _analysisOutput.BorderStyle = BorderStyle.FixedSingle;

            _scriptCleanOutput.BackColor = SystemColors.Window;
            _scriptCleanOutput.ForeColor = SystemColors.WindowText;
            _scriptCleanOutput.BorderStyle = BorderStyle.FixedSingle;

            _scriptOutput.BackColor = SystemColors.Window;
            _scriptOutput.ForeColor = SystemColors.WindowText;
            _scriptOutput.BorderStyle = BorderStyle.FixedSingle;

            _bottomSplitter.Panel1.BackColor = SystemColors.Control;
            _bottomSplitter.Panel2.BackColor = SystemColors.Control;

            _scenarioList.BackColor = SystemColors.Window;
            _scenarioList.ForeColor = SystemColors.WindowText;
            _scenarioList.BorderStyle = BorderStyle.FixedSingle;

            _mainSplitter.BackColor = SystemColors.ControlDark;
            _mainSplitter.Panel1.BackColor = SystemColors.Control;
            _mainSplitter.Panel2.BackColor = SystemColors.Control;
            _topSplitter.BackColor = SystemColors.ControlDark;
            _topSplitter.Panel1.BackColor = SystemColors.Control;
            _topSplitter.Panel2.BackColor = SystemColors.Control;

            _startIdInput.BackColor = SystemColors.Window;
            _startIdInput.ForeColor = SystemColors.WindowText;
            _rowsPerTableInput.BackColor = SystemColors.Window;
            _rowsPerTableInput.ForeColor = SystemColors.WindowText;
        }

        // ═══════════════════════════════════════════════════════════════
        // Event Handlers
        // ═══════════════════════════════════════════════════════════════

        private void HeaderPanel_Resize(object? sender, EventArgs e)
        {
            _connectionStatusLabel.Location = new Point(_headerPanel.Width - _connectionStatusLabel.Width - 250, 18);
            _connectBtn.Location = new Point(_headerPanel.Width - 220, 14);
            _disconnectBtn.Location = new Point(_headerPanel.Width - 120, 14);
        }

        private void BottomToolbar_Resize(object? sender, EventArgs e)
        {
            _statusLabel.Location = new Point(_bottomToolbar.Width - _statusLabel.Width - 20, 15);
        }

        private async void ConnectBtn_Click(object? sender, EventArgs e)
        {
            using var form = new ConnectionForm();
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _connectionManager = new DatabaseConnectionManager();
                var (success, message) = await _connectionManager.ConnectAsync(
                    form.ServerName, form.DatabaseName,
                    form.Username, form.Password,
                    form.UseWindowsAuth);

                if (success)
                {
                    _schemaIntrospector = new SchemaIntrospector(() => _connectionManager.CreateNewConnection());
                    _connectionStatusLabel.Text = $"● Connected: {form.ServerName}/{form.DatabaseName}";
                    _connectionStatusLabel.ForeColor = Color.Green;
                    _connectBtn.Visible = false;
                    _disconnectBtn.Visible = true;
                    SetStatus("Database connected successfully.");
                }
                else
                {
                    MessageBox.Show(message, "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DisconnectBtn_Click(object? sender, EventArgs e)
        {
            _connectionManager?.Disconnect();
            _connectionManager = null;
            _schemaIntrospector = null;
            _connectionStatusLabel.Text = "● Not Connected";
            _connectionStatusLabel.ForeColor = _accentRed;
            _connectBtn.Visible = true;
            _disconnectBtn.Visible = false;
            SetStatus("Disconnected.");
        }

        private void AnalyzeBtn_Click(object? sender, EventArgs e)
        {
            var sql = _sqlInput.Text.Trim();
            if (string.IsNullOrEmpty(sql))
            {
                MessageBox.Show("Please enter a SQL query.", "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                SetStatus("Analyzing SQL...");
                _currentQuery = _parser.Parse(sql);

                if (_currentQuery.Errors.Any())
                {
                    _analysisOutput.Text = "❌ PARSE ERRORS:\r\n" +
                        string.Join("\r\n", _currentQuery.Errors);
                    _generateBtn.Enabled = false;
                    SetStatus("Parse errors found.");
                    return;
                }

                // Display analysis
                _analysisOutput.Text = _parser.GenerateSummary(_currentQuery);

                // Generate branch scenarios
                var scenarios = _branchAnalyzer.AnalyzeBranches(_currentQuery);

                // Populate scenario list
                _scenarioList.Items.Clear();
                foreach (var s in scenarios)
                {
                    var shouldCheck = s.Type == DataGeneration.Models.ScenarioType.Positive;
                    _scenarioList.Items.Add($"[{s.Type}] {s.Name}", shouldCheck);
                }

                _generateBtn.Enabled = true;
                SetStatus($"Analysis complete: {_currentQuery.Tables.Count} tables, " +
                          $"{_currentQuery.WhereConditions.Count} conditions, " +
                          $"{scenarios.Count} scenarios.");
            }
            catch (Exception ex)
            {
                _analysisOutput.Text = $"❌ Error: {ex.Message}";
                _generateBtn.Enabled = false;
                SetStatus("Analysis failed.");
            }
        }

        private void GenerateBtn_Click(object? sender, EventArgs e)
        {
            if (_currentQuery == null) return;

            try
            {
                SetStatus("Generating test data...");
                _dataEngine.StartId = (int)_startIdInput.Value;
                _dataEngine.RowsPerTable = (int)_rowsPerTableInput.Value;

                // Get schemas from database if connected
                _schemas = null;
                if (_schemaIntrospector != null)
                {
                    try
                    {
                        var tableNames = _currentQuery.Tables.Select(t => t.TableName).Distinct();
                        _schemas = _schemaIntrospector.GetSchemas(tableNames);
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Schema introspection warning: {ex.Message}. Using inferred schemas.");
                    }
                }

                // Get selected scenarios
                var allScenarios = _branchAnalyzer.AnalyzeBranches(_currentQuery);
                var selectedScenarios = new List<DataGeneration.Models.BranchScenario>();
                for (int i = 0; i < _scenarioList.Items.Count && i < allScenarios.Count; i++)
                {
                    if (_scenarioList.GetItemChecked(i))
                        selectedScenarios.Add(allScenarios[i]);
                }

                if (!selectedScenarios.Any())
                {
                    MessageBox.Show("Please select at least one scenario.", "No Scenarios", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Generate data
                if (_schemas != null && _schemas.Any())
                {
                    _currentDataSet = _dataEngine.Generate(_currentQuery, _schemas, selectedScenarios);
                }
                else
                {
                    _currentDataSet = _dataEngine.GenerateWithoutSchema(_currentQuery, selectedScenarios);
                }

                // Generate full INSERT scripts (with comments + transactions)
                _insertGenerator.Schemas = _schemas;
                _insertGenerator.HandleIdentityInsert = _schemas != null;
                _insertGenerator.IncludeComments = true;
                _insertGenerator.WrapInTransaction = true;
                var fullScript = _insertGenerator.GenerateScript(_currentDataSet);

                // Generate clean INSERT scripts (no comments, no transactions)
                _insertGenerator.IncludeComments = false;
                _insertGenerator.WrapInTransaction = false;
                var cleanScript = _insertGenerator.GenerateScript(_currentDataSet);

                _scriptCleanOutput.Text = cleanScript;
                _scriptOutput.Text = fullScript;
                SetStatus($"Generated {selectedScenarios.Count} scenario(s), {_rowsPerTableInput.Value} row(s)/table.");
            }
            catch (Exception ex)
            {
                _scriptCleanOutput.Text = $"-- Error: {ex.Message}";
                _scriptOutput.Text = $"-- Error generating data: {ex.Message}\r\n-- {ex.StackTrace}";
                SetStatus("Generation failed.");
            }
        }

        private void CopyBtn_Click(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_scriptCleanOutput.Text))
            {
                Clipboard.SetText(_scriptCleanOutput.Text);
                SetStatus("Clean INSERT script copied to clipboard!");
            }
        }

        private void CopyCleanupBtn_Click(object? sender, EventArgs e)
        {
            if (_currentDataSet == null) return;

            var cleanup = _cleanupGenerator.GenerateCleanupScript(_currentDataSet);
            Clipboard.SetText(cleanup);
            SetStatus("Cleanup script copied to clipboard!");
        }

        private void SaveBtn_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_scriptOutput.Text)) return;

            using var dialog = new SaveFileDialog
            {
                Filter = "SQL Files|*.sql|All Files|*.*",
                DefaultExt = "sql",
                FileName = $"test_data_{DateTime.Now:yyyyMMdd_HHmmss}.sql"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(dialog.FileName, _scriptOutput.Text);

                // Also save cleanup script
                if (_currentDataSet != null)
                {
                    var cleanupPath = Path.ChangeExtension(dialog.FileName, ".cleanup.sql");
                    File.WriteAllText(cleanupPath, _cleanupGenerator.GenerateCleanupScript(_currentDataSet));
                }

                SetStatus($"Saved to {dialog.FileName}");
            }
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.Location = new Point(_bottomToolbar.Width - _statusLabel.Width - 20, 15);
        }

        // ═══════════════════════════════════════════════════════════════
        // Helper: Create styled button
        // ═══════════════════════════════════════════════════════════════

        private Button CreateButton(string text, Color accentColor, int width)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Standard,
                Font = new Font("Segoe UI", 9F),
                ForeColor = SystemColors.ControlText,
                BackColor = SystemColors.Control,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = true
            };

            return btn;
        }
    }
}
