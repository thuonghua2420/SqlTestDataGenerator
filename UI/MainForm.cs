using Microsoft.Data.SqlClient;
using SqlTestDataGenerator.Database;
using SqlTestDataGenerator.DataGeneration;
using SqlTestDataGenerator.DataGeneration.Models;
using SqlTestDataGenerator.Output;
using SqlTestDataGenerator.Parsing;
using SqlTestDataGenerator.Parsing.Models;
using SqlTestDataGenerator.Schema;
using SqlTestDataGenerator.Schema.Models;
using System.Data;
using System.Drawing.Drawing2D;
using System.Text;

namespace SqlTestDataGenerator.UI
{
    public class MainForm : Form
    {
        // ── Services ──
        private readonly SqlParserService _parser = new();
        private readonly BranchCoverageAnalyzer _branchAnalyzer = new();
        private readonly DataGenerationEngine _dataEngine = new();
        private readonly GeneratedDataSetNormalizer _dataNormalizer = new();
        private readonly InsertScriptGenerator _insertGenerator = new();
        private readonly CleanupScriptGenerator _cleanupGenerator = new();
        private readonly DependencyOrderResolver _orderResolver = new();
        private readonly GeneratedDataDbExecutor _dbExecutor = new();
        private readonly TableCsvExporter _csvExporter = new();
        private readonly TableCsvFolderImporter _csvImporter = new();
        private readonly ConnectionProfileCache _connectionProfileCache = new();
        private TableKeySeedResolver? _tableKeySeedResolver;
        private TableSampleExtractor? _tableSampleExtractor;
        private DatabaseConnectionManager? _connectionManager;
        private SchemaIntrospector? _schemaIntrospector;

        // ── State ──
        private ParsedQuery? _currentQuery;
        private Dictionary<string, TableSchema>? _schemas;
        private DataGeneration.Models.GeneratedDataSet? _currentDataSet;
        private Dictionary<string, Dictionary<string, object?>> _baselineSampleRows = new(StringComparer.OrdinalIgnoreCase);
        private List<DataGeneration.Models.BranchScenario> _availableScenarios = new();
        private List<DirectInsertTableInfo> _lastInsertedTables = new();
        private bool _currentDataSetIsGenerated;

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
        private Button _insertDbBtn = null!;
        private Button _exportCsvBtn = null!;
        private Button _importCsvBtn = null!;
        private Button _browseExportFolderBtn = null!;
        private Button _copyBtn = null!;
        private Button _saveBtn = null!;
        private Button _copyCleanupBtn = null!;

        private Panel _toolbarPanel = null!;
        private Panel _bottomToolbar = null!;
        private TabControl _featureTabs = null!;

        private CheckedListBox _scenarioList = null!;
        private CheckBox _selectAllScenariosCheck = null!;
        private Label _statusLabel = null!;
        private Label _exportFolderLabel = null!;
        private TextBox _exportFolderInput = null!;
        private NumericUpDown _startIdInput = null!;
        private NumericUpDown _rowsPerTableInput = null!;
        private CheckBox _maxLengthMaxValueCheck = null!;
        private bool _suppressScenarioSelectionSync;

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
            Shown += MainForm_Shown;
            LogInfo("Application started.");
        }

        private void InitializeComponent()
        {
            this.Text = "Tool support UTR";
            this.Size = new Size(1400, 900);
            this.MinimumSize = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 10F);
            this.DoubleBuffered = true;

#pragma warning disable CS0162
            BuildHybridLayout();
            return;

            // ═══ Header Panel ═══
            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(16, 8, 16, 8)
            };

            _titleLabel = new Label
            {
                Text = "Tool support UTR",
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

            _insertDbBtn = CreateButton("Insert to DB", _accentOrange, 130);
            _insertDbBtn.Location = new Point(308, 8);
            _insertDbBtn.Enabled = false;
            _insertDbBtn.Click += InsertDbBtn_Click;

            var startIdLabel = new Label
            {
                Text = "Start ID:",
                AutoSize = true,
                Location = new Point(452, 13),
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9F)
            };

            _startIdInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 9999999,
                Value = 90000,
                Increment = 1000,
                Location = new Point(517, 9),
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
                Location = new Point(622, 13),
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9F)
            };

            _rowsPerTableInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 2500,
                Value = 1,
                Increment = 1,
                Location = new Point(697, 9),
                Width = 70,
                Font = new Font("Segoe UI", 9F),
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                BorderStyle = BorderStyle.FixedSingle
            };

            _toolbarPanel.Controls.AddRange(new Control[] { _analyzeBtn, _generateBtn, _insertDbBtn, startIdLabel, _startIdInput, rowsPerTableLabel, _rowsPerTableInput });

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
                Text = "📊 Kết quả phân tích",
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
                Text = "☑ Scenarios:",
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

            // Bottom: Split into executable SQL script (left) + application log (right)
            _bottomSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 500,
                BackColor = SystemColors.ControlDark,
                SplitterWidth = 3
            };

            // Left: executable SQL script preview
            var cleanPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var cleanLabel = new Label
            {
                Text = "📜 Executable SQL Script",
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

            // Right: application log
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
                WordWrap = true,
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
                Height = 90,
                Padding = new Padding(16, 8, 16, 8)
            };

            _copyBtn = CreateButton("📋 Copy SQL", _accentBlue, 120);
            _copyBtn.Location = new Point(16, 8);
            _copyBtn.Click += CopyBtn_Click;

            _copyCleanupBtn = CreateButton("🧹 Copy Cleanup", _accentOrange, 130);
            _copyCleanupBtn.Location = new Point(150, 8);
            _copyCleanupBtn.Click += CopyCleanupBtn_Click;
            _copyCleanupBtn.Visible = false;

            _saveBtn = CreateButton("💾 Save .sql", _accentGreen, 110);
            _saveBtn.Location = new Point(150, 8);
            _saveBtn.Click += SaveBtn_Click;

            _exportFolderLabel = new Label
            {
                Text = "CSV Folder:",
                AutoSize = true,
                Location = new Point(16, 52),
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textPrimary
            };

            _exportFolderInput = new TextBox
            {
                Location = new Point(90, 48),
                Width = 620,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            _exportFolderInput.TextChanged += ExportFolderInput_TextChanged;

            _browseExportFolderBtn = CreateButton("Browse...", _accentBlue, 80);
            _browseExportFolderBtn.Location = new Point(720, 44);
            _browseExportFolderBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseExportFolderBtn.Click += BrowseExportFolderBtn_Click;

            _exportCsvBtn = CreateButton("Export CSV", _accentGreen, 110);
            _exportCsvBtn.Location = new Point(810, 44);
            _exportCsvBtn.Enabled = false;
            _exportCsvBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _exportCsvBtn.Click += ExportCsvBtn_Click;

            _importCsvBtn = CreateButton("Import CSV", _accentOrange, 110);
            _importCsvBtn.Location = new Point(930, 44);
            _importCsvBtn.Enabled = false;
            _importCsvBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _importCsvBtn.Click += ImportCsvBtn_Click;

            _statusLabel = new Label
            {
                Text = "Ready",
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textSecondary
            };

            _bottomToolbar.Controls.AddRange(new Control[]
            {
                _copyBtn, _saveBtn,
                _exportFolderLabel, _exportFolderInput, _browseExportFolderBtn, _exportCsvBtn, _importCsvBtn,
                _statusLabel
            });
            _bottomToolbar.Resize += BottomToolbar_Resize;

            // ═══ Add to form ═══
            this.Controls.Add(_mainSplitter);
            this.Controls.Add(_bottomToolbar);
            this.Controls.Add(_toolbarPanel);
            this.Controls.Add(_headerPanel);
        }

        private void BuildHybridLayout()
        {
            SuspendLayout();
            Controls.Clear();

            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(16, 8, 16, 8),
                BackColor = SystemColors.Control
            };

            _titleLabel = new Label
            {
                Text = "Tool support UTR",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = _textPrimary,
                AutoSize = true,
                Location = new Point(16, 14)
            };

            _connectionStatusLabel = new Label
            {
                Text = "● Not Connected",
                Font = new Font("Segoe UI", 9F),
                ForeColor = _accentRed,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _connectBtn = CreateLegacyButton("🔌 Connect", 90);
            _connectBtn.Click += ConnectBtn_Click;

            _disconnectBtn = CreateLegacyButton("✖ Disconnect", 100);
            _disconnectBtn.Visible = false;
            _disconnectBtn.Click += DisconnectBtn_Click;

            _headerPanel.Controls.AddRange(new Control[] { _titleLabel, _connectionStatusLabel, _connectBtn, _disconnectBtn });
            _headerPanel.Resize += HeaderPanel_Resize;

            _toolbarPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                Padding = new Padding(16, 8, 16, 8),
                BackColor = SystemColors.Control
            };

            _analyzeBtn = CreateLegacyButton("🔍 Analyze SQL", 126);
            _analyzeBtn.Location = new Point(16, 8);
            _analyzeBtn.Click += AnalyzeBtn_Click;

            _generateBtn = CreateLegacyButton("⚡ Generate Data", 138);
            _generateBtn.Location = new Point(160, 8);
            _generateBtn.Enabled = false;
            _generateBtn.Click += GenerateBtn_Click;

            _insertDbBtn = CreateLegacyButton("Insert to DB", 130);
            _insertDbBtn.Location = new Point(316, 8);
            _insertDbBtn.Enabled = false;
            _insertDbBtn.Click += InsertDbBtn_Click;

            var rowsPerTableLabel = new Label
            {
                Text = "Rows/Table:",
                AutoSize = true,
                Location = new Point(462, 13),
                ForeColor = _textPrimary,
                Font = new Font("Segoe UI", 9F)
            };

            _rowsPerTableInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 2500,
                Value = 1,
                Increment = 1,
                Location = new Point(537, 9),
                Width = 72,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.White,
                ForeColor = _textPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };

            _maxLengthMaxValueCheck = new CheckBox
            {
                Text = "Maxlength/MaxValue",
                AutoSize = true,
                Location = new Point(628, 12),
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textPrimary,
                BackColor = SystemColors.Control
            };
            _maxLengthMaxValueCheck.CheckedChanged += MaxLengthMaxValueCheck_CheckedChanged;

            _toolbarPanel.Controls.AddRange(new Control[]
            {
                _analyzeBtn, _generateBtn, _insertDbBtn, rowsPerTableLabel, _rowsPerTableInput, _maxLengthMaxValueCheck
            });

            void CenterToolbarControls()
            {
                foreach (var control in new Control[]
                {
                    _analyzeBtn,
                    _generateBtn,
                    _insertDbBtn,
                    rowsPerTableLabel,
                    _rowsPerTableInput,
                    _maxLengthMaxValueCheck
                })
                {
                    control.Top = Math.Max(0, (_toolbarPanel.ClientSize.Height - control.Height) / 2);
                }
            }

            CenterToolbarControls();
            _toolbarPanel.Resize += (_, _) => CenterToolbarControls();

            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 0, 16, 0),
                BackColor = SystemColors.Control
            };

            var workspaceLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = SystemColors.Control
            };
            workspaceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71F));
            workspaceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29F));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62F));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));

            _sqlInput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 10F),
                AcceptsReturn = true,
                AcceptsTab = true,
                PlaceholderText = "Nhập câu SQL..."
            };

            _analysisOutput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = true,
                Font = new Font("Cascadia Code, Consolas", 9.5F)
            };

            _scenarioList = new CheckedListBox
            {
                Font = new Font("Segoe UI", 9F),
                CheckOnClick = true,
                IntegralHeight = false
            };
            _scenarioList.ItemCheck += ScenarioList_ItemCheck;

            _selectAllScenariosCheck = new CheckBox
            {
                Text = "All",
                AutoSize = true,
                Enabled = false,
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textPrimary,
                BackColor = Color.White,
                Margin = new Padding(0, 4, 0, 0)
            };
            _selectAllScenariosCheck.CheckedChanged += SelectAllScenariosCheck_CheckedChanged;

            _scriptCleanOutput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 9.5F)
            };

            _scriptOutput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = true,
                Font = new Font("Cascadia Code, Consolas", 9.5F)
            };

            var sqlCard = CreateCard("SQL Input", _accentBlue, CreateSurface(_sqlInput, new Padding(10)));
            sqlCard.Margin = new Padding(0, 0, 12, 12);

            var analysisCard = CreateCard("Kết quả phân tích", _accentGreen, CreateSurface(_analysisOutput, new Padding(10)));
            analysisCard.Margin = new Padding(0, 0, 0, 12);

            var scenarioCard = CreateCard("Scenarios", _accentOrange, CreateSurface(_scenarioList, new Padding(10)), _selectAllScenariosCheck);
            scenarioCard.Margin = new Padding(0, 0, 0, 12);

            var rightTopLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));
            rightTopLayout.Controls.Add(analysisCard, 0, 0);
            rightTopLayout.Controls.Add(scenarioCard, 0, 1);

            var scriptCard = CreateCard("Executable SQL Script", Color.FromArgb(111, 84, 214), CreateSurface(_scriptCleanOutput, new Padding(10)));
            scriptCard.Margin = new Padding(0, 0, 12, 0);

            var logCard = CreateCard("Full Log", _accentBlue, CreateSurface(_scriptOutput, new Padding(10)));
            logCard.Margin = new Padding(0);

            workspaceLayout.Controls.Add(sqlCard, 0, 0);
            workspaceLayout.Controls.Add(rightTopLayout, 1, 0);
            workspaceLayout.Controls.Add(scriptCard, 0, 1);
            workspaceLayout.Controls.Add(logCard, 1, 1);
            contentPanel.Controls.Add(workspaceLayout);

            _bottomToolbar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 90,
                Padding = new Padding(16, 8, 16, 8),
                BackColor = SystemColors.Control
            };

            _copyBtn = CreateLegacyButton("📋 Copy SQL", 120);
            _copyBtn.Location = new Point(16, 8);
            _copyBtn.Click += CopyBtn_Click;

            _saveBtn = CreateLegacyButton("💾 Save .sql", 110);
            _saveBtn.Location = new Point(150, 8);
            _saveBtn.Click += SaveBtn_Click;

            _exportFolderLabel = new Label
            {
                Text = "CSV Folder:",
                AutoSize = true,
                Location = new Point(16, 52),
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textPrimary
            };

            _exportFolderInput = new TextBox
            {
                Location = new Point(90, 48),
                Width = 620,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            _exportFolderInput.TextChanged += ExportFolderInput_TextChanged;

            _browseExportFolderBtn = CreateLegacyButton("Browse...", 80);
            _browseExportFolderBtn.Location = new Point(720, 44);
            _browseExportFolderBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseExportFolderBtn.Click += BrowseExportFolderBtn_Click;

            _exportCsvBtn = CreateLegacyButton("Export CSV", 110);
            _exportCsvBtn.Location = new Point(810, 44);
            _exportCsvBtn.Enabled = false;
            _exportCsvBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _exportCsvBtn.Click += ExportCsvBtn_Click;

            _importCsvBtn = CreateLegacyButton("Import CSV", 110);
            _importCsvBtn.Location = new Point(930, 44);
            _importCsvBtn.Enabled = false;
            _importCsvBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _importCsvBtn.Click += ImportCsvBtn_Click;

            _statusLabel = new Label
            {
                Text = "Ready",
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textSecondary
            };

            _bottomToolbar.Controls.AddRange(new Control[]
            {
                _copyBtn, _saveBtn, _exportFolderLabel, _exportFolderInput, _browseExportFolderBtn, _exportCsvBtn, _importCsvBtn, _statusLabel
            });
            _bottomToolbar.Resize += BottomToolbar_Resize;

            var createDataPage = new TabPage("Create Data")
            {
                BackColor = SystemColors.Control,
                Padding = new Padding(0)
            };
            var createDataContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Control,
                Padding = new Padding(0)
            };
            createDataContainer.Controls.Add(contentPanel);
            createDataContainer.Controls.Add(_bottomToolbar);
            createDataContainer.Controls.Add(_toolbarPanel);
            createDataPage.Controls.Add(createDataContainer);

            var supportToolsPage = new TabPage("Format")
            {
                BackColor = SystemColors.Control,
                Padding = new Padding(0)
            };
            supportToolsPage.Controls.Add(new SupportToolsPanel { Dock = DockStyle.Fill });

            _featureTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Padding = new Point(16, 5)
            };
            _featureTabs.TabPages.Add(createDataPage);
            _featureTabs.TabPages.Add(supportToolsPage);

            Controls.Add(_featureTabs);
            Controls.Add(_headerPanel);

            HeaderPanel_Resize(this, EventArgs.Empty);
            BottomToolbar_Resize(this, EventArgs.Empty);
            ResumeLayout();
        }

        private void BuildModernLayout()
        {
            SuspendLayout();
            Controls.Clear();

            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 120,
                Padding = new Padding(16, 16, 16, 8),
                BackColor = Color.White
            };

            var headerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White
            };
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            var heroLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White
            };
            heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var appIconPanel = new Panel
            {
                BackColor = _accentBlue,
                Size = new Size(52, 52),
                Margin = new Padding(0, 0, 14, 0)
            };
            var appIconLabel = new Label
            {
                Text = "⚡",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Symbol", 18F, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter
            };
            appIconPanel.Controls.Add(appIconLabel);

            _titleLabel = new Label
            {
                Text = "Tool support UTR",
                Font = new Font("Segoe UI Semibold", 18F),
                ForeColor = _textPrimary,
                AutoSize = true,
                Margin = new Padding(0)
            };

            var subtitleLabel = new Label
            {
                Text = "Generate test data from your SQL scripts",
                Font = new Font("Segoe UI", 10F),
                ForeColor = _textSecondary,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };

            var titleStack = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White
            };
            titleStack.Controls.Add(_titleLabel);
            titleStack.Controls.Add(subtitleLabel);

            var brandFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White,
                Anchor = AnchorStyles.Left
            };
            brandFlow.Controls.Add(appIconPanel);
            brandFlow.Controls.Add(titleStack);

            _connectionStatusLabel = new Label
            {
                Text = "● Not Connected",
                Font = new Font("Segoe UI", 10F),
                ForeColor = _accentRed,
                AutoSize = true,
                Margin = new Padding(0, 10, 14, 0),
                BackColor = Color.White
            };

            _connectBtn = CreateButton("Connect", _accentBlue, 104);
            _connectBtn.Margin = new Padding(0);
            _connectBtn.Click += ConnectBtn_Click;

            _disconnectBtn = CreateButton("Disconnect", _accentRed, 112);
            _disconnectBtn.Margin = new Padding(0);
            _disconnectBtn.Visible = false;
            _disconnectBtn.Click += DisconnectBtn_Click;

            var statusFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White,
                Anchor = AnchorStyles.Right
            };
            statusFlow.Controls.Add(_connectionStatusLabel);
            statusFlow.Controls.Add(_connectBtn);
            statusFlow.Controls.Add(_disconnectBtn);

            heroLayout.Controls.Add(brandFlow, 0, 0);
            heroLayout.Controls.Add(statusFlow, 1, 0);

            _analyzeBtn = CreateButton("Analyze SQL", _accentBlue, 132);
            _analyzeBtn.Click += AnalyzeBtn_Click;

            _generateBtn = CreateButton("Generate Data", _accentGreen, 154);
            _generateBtn.Enabled = false;
            _generateBtn.Click += GenerateBtn_Click;

            _insertDbBtn = CreateButton("Insert to DB", _accentOrange, 136);
            _insertDbBtn.Enabled = false;
            _insertDbBtn.Click += InsertDbBtn_Click;

            _rowsPerTableInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 2500,
                Value = 1,
                Increment = 1,
                Width = 92,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0),
                BackColor = Color.White
            };

            var controlsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 2, 0, 0),
                Padding = new Padding(0),
                BackColor = Color.White,
                Anchor = AnchorStyles.Left
            };
            controlsFlow.Controls.Add(_analyzeBtn);
            controlsFlow.Controls.Add(_generateBtn);
            controlsFlow.Controls.Add(_insertDbBtn);
            controlsFlow.Controls.Add(CreateLabeledInputPanel("Rows/Table:", _rowsPerTableInput));

            headerLayout.Controls.Add(heroLayout, 0, 0);
            headerLayout.Controls.Add(controlsFlow, 0, 1);
            _headerPanel.Controls.Add(headerLayout);

            _toolbarPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 8, 16, 16),
                BackColor = Color.FromArgb(248, 250, 252)
            };

            var workspaceLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(248, 250, 252)
            };
            workspaceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71F));
            workspaceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29F));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62F));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));

            _sqlInput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 10F),
                AcceptsReturn = true,
                AcceptsTab = true,
                PlaceholderText = "Nhập câu SQL..."
            };

            _analysisOutput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = true,
                Font = new Font("Cascadia Code, Consolas", 9.5F)
            };

            _scenarioList = new CheckedListBox
            {
                Font = new Font("Segoe UI", 9F),
                CheckOnClick = true,
                IntegralHeight = false
            };
            _scenarioList.ItemCheck += ScenarioList_ItemCheck;

            _selectAllScenariosCheck = new CheckBox
            {
                Text = "All",
                AutoSize = true,
                Enabled = false,
                Font = new Font("Segoe UI", 9F),
                ForeColor = _textPrimary,
                BackColor = Color.White,
                Margin = new Padding(0, 4, 0, 0)
            };
            _selectAllScenariosCheck.CheckedChanged += SelectAllScenariosCheck_CheckedChanged;

            _scriptCleanOutput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Cascadia Code, Consolas", 9.5F)
            };

            _scriptOutput = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = true,
                Font = new Font("Cascadia Code, Consolas", 9.5F)
            };

            var sqlCard = CreateCard("SQL Input", _accentBlue, CreateSurface(_sqlInput, new Padding(10)));
            sqlCard.Margin = new Padding(0, 0, 12, 12);

            var analysisCard = CreateCard("Kết quả phân tích", _accentGreen, CreateSurface(_analysisOutput, new Padding(10)));
            analysisCard.Margin = new Padding(0, 0, 0, 12);

            var scenarioCard = CreateCard("Scenarios", _accentOrange, CreateSurface(_scenarioList, new Padding(10)), _selectAllScenariosCheck);
            scenarioCard.Margin = new Padding(0);

            var rightTopLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));
            rightTopLayout.Controls.Add(analysisCard, 0, 0);
            rightTopLayout.Controls.Add(scenarioCard, 0, 1);

            var scriptCard = CreateCard("Executable SQL Script", Color.FromArgb(111, 84, 214), CreateSurface(_scriptCleanOutput, new Padding(10)));
            scriptCard.Margin = new Padding(0, 0, 12, 0);

            var logCard = CreateCard("Full Log", _accentBlue, CreateSurface(_scriptOutput, new Padding(10)));
            logCard.Margin = new Padding(0);

            workspaceLayout.Controls.Add(sqlCard, 0, 0);
            workspaceLayout.Controls.Add(rightTopLayout, 1, 0);
            workspaceLayout.Controls.Add(scriptCard, 0, 1);
            workspaceLayout.Controls.Add(logCard, 1, 1);

            _bottomToolbar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 100,
                Padding = new Padding(0, 12, 0, 0),
                BackColor = Color.FromArgb(248, 250, 252)
            };

            _copyBtn = CreateButton("Copy SQL", _accentBlue, 106);
            _copyBtn.Click += CopyBtn_Click;

            _saveBtn = CreateButton("Save .sql", _accentGreen, 106);
            _saveBtn.Click += SaveBtn_Click;

            _exportFolderLabel = new Label
            {
                Text = "CSV Folder:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = _textPrimary,
                Margin = new Padding(0, 10, 10, 0),
                BackColor = Color.FromArgb(248, 250, 252)
            };

            _exportFolderInput = new TextBox
            {
                Font = new Font("Segoe UI", 9.5F),
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "Select folder to save CSV files..."
            };
            _exportFolderInput.TextChanged += ExportFolderInput_TextChanged;

            _browseExportFolderBtn = CreateButton("Browse...", _accentBlue, 110);
            _browseExportFolderBtn.Click += BrowseExportFolderBtn_Click;

            _exportCsvBtn = CreateButton("Export CSV", _accentGreen, 118);
            _exportCsvBtn.Enabled = false;
            _exportCsvBtn.Click += ExportCsvBtn_Click;

            _importCsvBtn = CreateButton("Import CSV", _accentOrange, 118);
            _importCsvBtn.Enabled = false;
            _importCsvBtn.Click += ImportCsvBtn_Click;

            _statusLabel = new Label
            {
                Text = "Ready",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = _textSecondary,
                Anchor = AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0, 10, 0, 0),
                BackColor = Color.FromArgb(248, 250, 252)
            };

            var footerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(248, 250, 252)
            };
            footerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            footerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            var actionsRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(248, 250, 252)
            };
            actionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var actionsLeftFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(248, 250, 252)
            };
            actionsLeftFlow.Controls.Add(_copyBtn);
            actionsLeftFlow.Controls.Add(_saveBtn);

            actionsRow.Controls.Add(actionsLeftFlow, 0, 0);
            actionsRow.Controls.Add(_statusLabel, 1, 0);

            var csvRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(248, 250, 252)
            };
            csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _exportFolderInput.Dock = DockStyle.Fill;
            _exportFolderInput.Margin = new Padding(0);
            _browseExportFolderBtn.Margin = new Padding(12, 0, 0, 0);
            _exportCsvBtn.Margin = new Padding(12, 0, 0, 0);
            _importCsvBtn.Margin = new Padding(12, 0, 0, 0);

            csvRow.Controls.Add(_exportFolderLabel, 0, 0);
            csvRow.Controls.Add(_exportFolderInput, 1, 0);
            csvRow.Controls.Add(_browseExportFolderBtn, 2, 0);
            csvRow.Controls.Add(_exportCsvBtn, 3, 0);
            csvRow.Controls.Add(_importCsvBtn, 4, 0);

            footerLayout.Controls.Add(actionsRow, 0, 0);
            footerLayout.Controls.Add(csvRow, 0, 1);
            _bottomToolbar.Controls.Add(footerLayout);

            _toolbarPanel.Controls.Add(workspaceLayout);
            _toolbarPanel.Controls.Add(_bottomToolbar);

            Controls.Add(_toolbarPanel);
            Controls.Add(_headerPanel);
            ResumeLayout();
        }

        private void ApplyTheme()
        {
            BackColor = SystemColors.Control;
            ForeColor = _textPrimary;

            _headerPanel.BackColor = SystemColors.Control;
            _toolbarPanel.BackColor = SystemColors.Control;
            _bottomToolbar.BackColor = SystemColors.Control;

            _titleLabel.ForeColor = _textPrimary;
            _connectionStatusLabel.ForeColor = _accentRed;

            _sqlInput.BackColor = Color.White;
            _sqlInput.ForeColor = _textPrimary;
            _sqlInput.BorderStyle = BorderStyle.None;

            _analysisOutput.BackColor = Color.White;
            _analysisOutput.ForeColor = _textPrimary;
            _analysisOutput.BorderStyle = BorderStyle.None;

            _scriptCleanOutput.BackColor = Color.White;
            _scriptCleanOutput.ForeColor = _textPrimary;
            _scriptCleanOutput.BorderStyle = BorderStyle.None;

            _scriptOutput.BackColor = Color.White;
            _scriptOutput.ForeColor = _textPrimary;
            _scriptOutput.BorderStyle = BorderStyle.None;

            _exportFolderInput.BackColor = SystemColors.Window;
            _exportFolderInput.ForeColor = _textPrimary;
            _exportFolderInput.BorderStyle = BorderStyle.FixedSingle;

            _scenarioList.BackColor = Color.White;
            _scenarioList.ForeColor = _textPrimary;
            _scenarioList.BorderStyle = BorderStyle.None;

            _rowsPerTableInput.BackColor = Color.White;
            _rowsPerTableInput.ForeColor = _textPrimary;
            if (_maxLengthMaxValueCheck != null)
            {
                _maxLengthMaxValueCheck.ForeColor = _textPrimary;
                _maxLengthMaxValueCheck.BackColor = SystemColors.Control;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Event Handlers
        // ═══════════════════════════════════════════════════════════════

        private void HeaderPanel_Resize(object? sender, EventArgs e)
        {
            if (_headerPanel == null ||
                _connectionStatusLabel == null ||
                _connectBtn == null ||
                _disconnectBtn == null)
            {
                return;
            }

            var activeButton = _disconnectBtn.Visible ? _disconnectBtn : _connectBtn;
            var buttonTop = Math.Max(0, (_headerPanel.ClientSize.Height - activeButton.Height) / 2);
            var buttonLeft = Math.Max(
                _headerPanel.Padding.Left,
                _headerPanel.ClientSize.Width - _headerPanel.Padding.Right - activeButton.Width);

            _connectBtn.Location = new Point(buttonLeft, buttonTop);
            _disconnectBtn.Location = new Point(buttonLeft, buttonTop);

            var statusTextSize = TextRenderer.MeasureText(
                _connectionStatusLabel.Text,
                _connectionStatusLabel.Font,
                new Size(int.MaxValue, activeButton.Height),
                TextFormatFlags.NoPadding);
            var statusWidth = Math.Min(
                Math.Max(160, statusTextSize.Width + 4),
                Math.Max(160, buttonLeft - _headerPanel.Padding.Left - 16));

            _connectionStatusLabel.Size = new Size(statusWidth, activeButton.Height);
            _connectionStatusLabel.Location = new Point(
                Math.Max(_headerPanel.Padding.Left, buttonLeft - statusWidth - 14),
                buttonTop);
        }

        private void BottomToolbar_Resize(object? sender, EventArgs e)
        {
            _statusLabel.Location = new Point(_bottomToolbar.Width - _statusLabel.Width - 20, 15);

            var exportButtonTop = 44;
            _importCsvBtn.Location = new Point(_bottomToolbar.Width - _importCsvBtn.Width - 20, exportButtonTop);
            _exportCsvBtn.Location = new Point(_importCsvBtn.Left - _exportCsvBtn.Width - 10, exportButtonTop);
            _browseExportFolderBtn.Location = new Point(_exportCsvBtn.Left - _browseExportFolderBtn.Width - 10, exportButtonTop);

            var inputLeft = _exportFolderLabel.Left + _exportFolderLabel.Width + 8;
            _exportFolderInput.Location = new Point(inputLeft, 48);
            _exportFolderInput.Width = Math.Max(220, _browseExportFolderBtn.Left - inputLeft - 10);
        }

        private async void MainForm_Shown(object? sender, EventArgs e)
        {
            Shown -= MainForm_Shown;

            if (!_connectionProfileCache.TryLoad(out var profile, out var message))
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    LogWarn(message);
                }

                return;
            }

            SetStatus("Auto-connecting to cached database...");
            LogInfo($"Auto-connecting to cached database {profile.Server}/{profile.Database}.");
            var success = await ConnectToDatabaseAsync(
                profile,
                cacheOnSuccess: false,
                showErrorDialog: false,
                sourceLabel: "cached database");

            if (!success)
            {
                SetStatus("Cached database auto-login failed. Please connect manually.");
            }
        }

        private async void ConnectBtn_Click(object? sender, EventArgs e)
        {
            _connectionProfileCache.TryLoad(out var cachedProfile, out _);
            using var form = new ConnectionForm(cachedProfile);
            if (form.ShowDialog(this) != DialogResult.OK)
                return;

            var profile = new ConnectionProfile
            {
                Server = form.ServerName,
                Database = form.DatabaseName,
                Username = form.Username,
                Password = form.Password,
                UseWindowsAuth = form.UseWindowsAuth
            };

            await ConnectToDatabaseAsync(
                profile,
                cacheOnSuccess: true,
                showErrorDialog: true,
                sourceLabel: "database");
        }

        private void DisconnectBtn_Click(object? sender, EventArgs e)
        {
            _connectionManager?.Disconnect();
            _connectionManager = null;
            _schemaIntrospector = null;
            _tableKeySeedResolver = null;
            _tableSampleExtractor = null;
            _connectionProfileCache.Clear();
            _baselineSampleRows.Clear();
            _currentDataSetIsGenerated = false;
            ClearLastInsertedTables();
            _connectionStatusLabel.Text = "● Not Connected";
            _connectionStatusLabel.ForeColor = _accentRed;
            _connectBtn.Visible = true;
            _disconnectBtn.Visible = false;
            HeaderPanel_Resize(_headerPanel, EventArgs.Empty);
            SetStatus("Disconnected.");
            LogInfo("Disconnected from database.");
            UpdateDbInsertButtonState();
        }

        private async Task<bool> ConnectToDatabaseAsync(
            ConnectionProfile profile,
            bool cacheOnSuccess,
            bool showErrorDialog,
            string sourceLabel)
        {
            var connectionManager = new DatabaseConnectionManager();
            var (success, message) = await connectionManager.ConnectAsync(
                profile.Server,
                profile.Database,
                profile.Username,
                profile.Password,
                profile.UseWindowsAuth);

            if (!success)
            {
                connectionManager.Dispose();
                LogError(message);
                if (showErrorDialog)
                {
                    MessageBox.Show(message, "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return false;
            }

            _connectionManager?.Dispose();
            _connectionManager = connectionManager;
            _schemaIntrospector = new SchemaIntrospector(() => _connectionManager.CreateNewConnection());
            _tableKeySeedResolver = new TableKeySeedResolver(() => _connectionManager.CreateNewConnection());
            _tableSampleExtractor = new TableSampleExtractor(() => _connectionManager.CreateNewConnection());
            _baselineSampleRows.Clear();
            ClearLastInsertedTables();
            _connectionStatusLabel.Text = $"● Connected: {profile.Server}/{profile.Database}";
            _connectionStatusLabel.ForeColor = Color.Green;
            _connectBtn.Visible = false;
            _disconnectBtn.Visible = true;
            HeaderPanel_Resize(_headerPanel, EventArgs.Empty);
            SetStatus("Database connected successfully.");
            LogInfo($"Connected to {sourceLabel} {profile.Server}/{profile.Database}.");

            if (cacheOnSuccess)
            {
                try
                {
                    _connectionProfileCache.Save(profile);
                    LogInfo("Cached database login profile for next startup.");
                }
                catch (Exception ex)
                {
                    LogWarn($"Connected, but failed to cache login profile: {ex.Message}");
                }
            }

            UpdateDbInsertButtonState();
            return true;
        }

        private void AnalyzeBtn_Click(object? sender, EventArgs e)
        {
            var sql = _sqlInput.Text.Trim();
            if (string.IsNullOrEmpty(sql))
            {
                LogWarn("Analyze requested without SQL input.");
                MessageBox.Show("Please enter a SQL query.", "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                ClearLastInsertedTables();
                _currentDataSet = null;
                _currentDataSetIsGenerated = false;
                _scriptCleanOutput.Clear();
                UpdateDbInsertButtonState();
                SetStatus("Analyzing SQL...");
                LogInfo("Starting SQL analysis.");
                _currentQuery = _parser.Parse(sql);

                if (_currentQuery.Errors.Any())
                {
                    _analysisOutput.Text = "❌ PARSE ERRORS:\r\n" +
                        string.Join("\r\n", _currentQuery.Errors);
                    _availableScenarios.Clear();
                    _scenarioList.Items.Clear();
                    _generateBtn.Enabled = false;
                    ResetScenarioSelectAllCheck();
                    _currentDataSet = null;
                    _currentDataSetIsGenerated = false;
                    UpdateDbInsertButtonState();
                    SetStatus("Parse errors found.");
                    LogWarn($"SQL analysis found {_currentQuery.Errors.Count} parse error(s).");
                    foreach (var error in _currentQuery.Errors)
                    {
                        LogWarn(error);
                    }
                    return;
                }

                // Display analysis
                _analysisOutput.Text = _parser.GenerateSummary(_currentQuery);

                // Generate branch scenarios
                _availableScenarios = _branchAnalyzer.AnalyzeBranches(_currentQuery);

                // Populate scenario list
                _scenarioList.Items.Clear();
                foreach (var s in _availableScenarios)
                {
                    var shouldCheck = s.Type == DataGeneration.Models.ScenarioType.Positive;
                    _scenarioList.Items.Add(FormatScenarioListItem(s), shouldCheck);
                }

                _generateBtn.Enabled = true;
                UpdateScenarioSelectAllCheckState();
                SetStatus($"Analysis complete: {_currentQuery.Tables.Count} tables, " +
                          $"{_currentQuery.WhereConditions.Count} conditions, " +
                          $"{_availableScenarios.Count} scenarios.");
                LogInfo(
                    $"Analysis complete: {_currentQuery.Tables.Count} table(s), {_currentQuery.WhereConditions.Count} WHERE condition(s), {_availableScenarios.Count} scenario(s).");
            }
            catch (Exception ex)
            {
                _analysisOutput.Text = $"❌ Error: {ex.Message}";
                _availableScenarios.Clear();
                _scenarioList.Items.Clear();
                _generateBtn.Enabled = false;
                ResetScenarioSelectAllCheck();
                _currentDataSet = null;
                _currentDataSetIsGenerated = false;
                UpdateDbInsertButtonState();
                SetStatus("Analysis failed.");
                LogError($"SQL analysis failed: {BuildErrorChain(ex)}");
            }
        }

        private static string FormatScenarioListItem(BranchScenario scenario)
        {
            return scenario.Name;
        }

        private void GenerateBtn_Click(object? sender, EventArgs e)
        {
            if (_currentQuery == null)
            {
                LogWarn("Generate requested without a parsed query.");
                return;
            }

            try
            {
                ClearLastInsertedTables();
                SetStatus("Generating test data...");
                _dataEngine.RowsPerTable = (int)_rowsPerTableInput.Value;
                _dataEngine.TableSeedStarts = null;
                _dataEngine.SampleRowsByTable = null;
                _dataEngine.UseMaxLengthMaxValueMode = _maxLengthMaxValueCheck.Checked;
                _dataEngine.ShuffleGeneratedStringCharacters = true;
                LogInfo($"Starting data generation with {_dataEngine.RowsPerTable} row(s)/table, mode = {(_maxLengthMaxValueCheck.Checked ? "Maxlength/MaxValue" : "Sample-based")}, value shuffle = enabled.");

                // Get schemas from database if connected
                _schemas = null;
                if (_schemaIntrospector != null)
                {
                    try
                    {
                        var tableNames = GetAllReferencedTableNames(_currentQuery);
                        _schemas = LoadSchemaClosure(tableNames);
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Schema introspection warning: {ex.Message}. Using inferred schemas.");
                        LogWarn($"Schema introspection warning: {BuildErrorChain(ex)}");
                    }
                }

                if (_schemas != null &&
                    _schemas.Any() &&
                    _tableKeySeedResolver != null)
                {
                    try
                    {
                        _dataEngine.TableSeedStarts = _tableKeySeedResolver.ResolveNextIds(_schemas.Values);
                        LogInfo($"Resolved database-backed next IDs for {_dataEngine.TableSeedStarts.Count} table(s).");
                    }
                    catch (Exception ex)
                    {
                        _dataEngine.TableSeedStarts = null;
                        LogWarn($"Failed to resolve database-backed next IDs. Falling back to internal seeds. {BuildErrorChain(ex)}");
                    }
                }
                else
                {
                    LogInfo("Database-backed next IDs unavailable. Using internal fallback seeds.");
                }

                if (_schemas != null &&
                    _schemas.Any() &&
                    _tableSampleExtractor != null)
                {
                    try
                    {
                        var cachedSamples = EnsureBaselineSampleRows(_schemas.Values);
                        if (_maxLengthMaxValueCheck.Checked)
                        {
                            LogInfo($"Baseline sample cache ready for {cachedSamples.Count} table(s); current generation uses Maxlength/MaxValue mode.");
                        }
                        else if (cachedSamples.Count > 0)
                        {
                            _dataEngine.SampleRowsByTable = cachedSamples;
                            LogInfo($"Using cached baseline sample row(s) for {cachedSamples.Count} table(s).");
                        }
                    }
                    catch (Exception ex)
                    {
                        _dataEngine.SampleRowsByTable = null;
                        LogWarn($"Failed to load sample rows from database. Falling back to synthetic defaults. {BuildErrorChain(ex)}");
                    }
                }
                else if (!_maxLengthMaxValueCheck.Checked)
                {
                    LogWarn("Sample-based generation requested without a connected database. Falling back to synthetic defaults.");
                }

                // Get selected scenarios
                var selectedScenarios = new List<DataGeneration.Models.BranchScenario>();
                for (int i = 0; i < _scenarioList.Items.Count && i < _availableScenarios.Count; i++)
                {
                    if (_scenarioList.GetItemChecked(i))
                        selectedScenarios.Add(_availableScenarios[i]);
                }

                if (!selectedScenarios.Any())
                {
                    LogWarn("Generate requested without any selected scenario.");
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

                if (_currentDataSet == null)
                    throw new InvalidOperationException("Data generation returned no dataset.");

                _currentDataSetIsGenerated = true;

                if (_schemas != null && _schemas.Any())
                {
                    _dataNormalizer.Normalize(_currentDataSet, _schemas);
                }

                RefreshInsertScriptPreview("generated data");
                UpdateDbInsertButtonState();
                SetStatus($"Generated {selectedScenarios.Count} scenario(s), {_rowsPerTableInput.Value} row(s)/table.");
                LogInfo($"Generated {selectedScenarios.Count} scenario(s) with {_rowsPerTableInput.Value} row(s)/table.");
            }
            catch (Exception ex)
            {
                _currentDataSet = null;
                _currentDataSetIsGenerated = false;
                UpdateDbInsertButtonState();
                _scriptCleanOutput.Text = $"-- Error: {ex.Message}";
                SetStatus("Generation failed.");
                LogError($"Data generation failed: {BuildErrorChain(ex)}");
            }
        }

        private async void InsertDbBtn_Click(object? sender, EventArgs e)
        {
            if (_currentDataSet == null)
            {
                LogWarn("Direct insert requested without generated data.");
                MessageBox.Show("Please generate data first.", "No Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_connectionManager == null || !_connectionManager.IsConnected)
            {
                LogWarn("Direct insert requested while database is not connected.");
                MessageBox.Show("Please connect to database first.", "Not Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_currentQuery == null)
            {
                LogWarn("Direct insert requested without an analyzed query.");
                MessageBox.Show("No analyzed query found.", "Missing Query", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (_schemaIntrospector != null)
                {
                    var tableNames = GetAllReferencedTableNames(_currentQuery);
                    _schemas = LoadSchemaClosure(tableNames);
                }
            }
            catch (Exception ex)
            {
                LogError($"Schema load failed before direct insert: {BuildErrorChain(ex)}");
                MessageBox.Show($"Cannot read schema from database:\r\n{ex.Message}",
                    "Schema Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_schemas == null || !_schemas.Any())
            {
                LogWarn("Direct insert blocked because schema metadata is missing.");
                MessageBox.Show("Schema metadata is missing. Please connect database and generate data again.",
                    "Schema Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var generatedTables = _currentDataSet.Scenarios
                .SelectMany(s => s.TableRows.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!generatedTables.Any())
            {
                LogWarn("Direct insert blocked because no generated table rows were found.");
                MessageBox.Show("No generated table rows found to insert.",
                    "No Rows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"This will DELETE existing rows from {generatedTables.Count} generated table(s), " +
                "and can also clear dependent child tables (FK) to avoid conflicts.\r\n" +
                "Then it will INSERT newly generated rows.\r\n\r\nDo you want to continue?",
                "Confirm Direct Insert",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                LogInfo("Direct insert was canceled by the user.");
                return;
            }

            try
            {
                ClearLastInsertedTables();
                _insertDbBtn.Enabled = false;
                SetStatus("Inserting generated data to database...");
                LogInfo($"Starting direct insert for {generatedTables.Count} generated table(s).");

                using var conn = _connectionManager.CreateNewConnection();
                var result = await _dbExecutor.ClearAndInsertAsync(conn, _currentDataSet, _schemas);
                _lastInsertedTables = result.InsertedTables
                    .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool? hasQueryRows = null;
                try
                {
                    hasQueryRows = await QueryReturnsRowsAsync(conn, _currentQuery.OriginalSql);
                }
                catch
                {
                    // Query verification is best-effort only. Data insert already succeeded.
                }

                SetStatus($"Inserted {result.RowsInserted} row(s) into {result.TablesInserted} table(s).");
                LogInfo(
                    $"Direct insert completed: {result.RowsInserted} row(s) into {result.TablesInserted} table(s), {result.RowsDeleted} row(s) deleted, query returns rows = {FormatQueryRowsResult(hasQueryRows)}.");
                MessageBox.Show(
                    $"Direct insert completed.\r\n\r\n" +
                    $"- Generated tables: {result.GeneratedTables}\r\n" +
                    $"- Planned tables: {result.PlannedTables}\r\n" +
                    $"- Synthesized ancestor tables: {result.SynthesizedAncestorTables}\r\n" +
                    $"- Tables cleared: {result.TablesCleared}\r\n" +
                    $"- Dependent tables auto-cleared: {result.DependentTablesCleared}\r\n" +
                    $"- Rows deleted: {result.RowsDeleted}\r\n" +
                    $"- Tables inserted: {result.TablesInserted}\r\n" +
                    $"- Rows inserted: {result.RowsInserted}\r\n" +
                    $"- FK clear fallback used: {(result.UsedConstraintDisableFallback ? "YES" : "NO")}\r\n" +
                    $"- FK insert bypass used: {(result.UsedInsertConstraintBypass ? "YES" : "NO")}\r\n" +
                    $"- Query returns rows: {FormatQueryRowsResult(hasQueryRows)}",
                    "Insert Completed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Direct insert failed.");
                LogError($"Direct insert failed: {BuildErrorChain(ex)}");
                MessageBox.Show(
                    $"Direct insert failed:\r\n{BuildErrorChain(ex)}",
                    "Insert Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                UpdateDbInsertButtonState();
            }
        }

        private void ExportFolderInput_TextChanged(object? sender, EventArgs e)
        {
            UpdateDbInsertButtonState();
        }

        private void BrowseExportFolderBtn_Click(object? sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select folder to save exported CSV files",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrWhiteSpace(_exportFolderInput.Text) && Directory.Exists(_exportFolderInput.Text))
            {
                dialog.SelectedPath = _exportFolderInput.Text;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _exportFolderInput.Text = dialog.SelectedPath;
            }
        }

        private async void ExportCsvBtn_Click(object? sender, EventArgs e)
        {
            if (_connectionManager == null || !_connectionManager.IsConnected)
            {
                LogWarn("CSV export requested while database is not connected.");
                MessageBox.Show("Please connect to database first.", "Not Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!_lastInsertedTables.Any())
            {
                LogWarn("CSV export requested before any successful direct insert.");
                MessageBox.Show("No successful direct insert found to export yet.", "Nothing To Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_exportFolderInput.Text))
            {
                LogWarn("CSV export requested without a target folder.");
                MessageBox.Show("Please enter a folder path to save CSV files.", "Folder Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _exportCsvBtn.Enabled = false;
                SetStatus("Exporting inserted tables to CSV...");

                var folderPath = Path.GetFullPath(_exportFolderInput.Text.Trim());
                LogInfo($"Starting CSV export to folder {folderPath}.");
                using var conn = _connectionManager.CreateNewConnection();
                var result = await _csvExporter.ExportAsync(conn, _lastInsertedTables, folderPath, _schemas);

                SetStatus($"Exported {result.ExportedTables} CSV file(s).");
                LogInfo($"CSV export completed: {result.ExportedTables} file(s), {result.ExportedRows} row(s), folder {folderPath}.");
                MessageBox.Show(
                    $"CSV export completed.\r\n\r\n" +
                    $"- Tables exported: {result.ExportedTables}\r\n" +
                    $"- Rows exported: {result.ExportedRows}\r\n" +
                    $"- Folder: {folderPath}",
                    "Export Completed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("CSV export failed.");
                LogError($"CSV export failed: {BuildErrorChain(ex)}");
                MessageBox.Show(
                    $"CSV export failed:\r\n{BuildErrorChain(ex)}",
                    "Export Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                UpdateDbInsertButtonState();
            }
        }

        private async void ImportCsvBtn_Click(object? sender, EventArgs e)
        {
            if (_connectionManager == null || !_connectionManager.IsConnected)
            {
                LogWarn("CSV import requested while database is not connected.");
                MessageBox.Show("Please connect to database first.", "Not Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_schemaIntrospector == null)
            {
                LogWarn("CSV import requested without schema introspector.");
                MessageBox.Show("Schema introspector is not available. Please reconnect to the database.", "Schema Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_exportFolderInput.Text))
            {
                LogWarn("CSV import requested without a source folder.");
                MessageBox.Show("Please enter a folder path that contains CSV files.", "Folder Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                ClearLastInsertedTables();
                _importCsvBtn.Enabled = false;
                SetStatus("Reading CSV files...");

                var folderPath = Path.GetFullPath(_exportFolderInput.Text.Trim());
                LogInfo($"Starting CSV import from folder {folderPath}.");
                var csvFiles = _csvImporter.DiscoverTableFiles(folderPath);
                LogInfo($"Discovered {csvFiles.Count} CSV file(s) for import.");
                _schemas = LoadSchemaClosure(csvFiles.Select(f => f.TableName));

                var importData = await _csvImporter.LoadFolderAsync(folderPath, _schemas);
                _currentDataSet = importData.DataSet;
                _currentDataSetIsGenerated = false;
                _dataNormalizer.Normalize(_currentDataSet, _schemas);
                RefreshInsertScriptPreview("imported CSV data");
                LogInfo($"Loaded {importData.CsvFilesRead} CSV file(s) and parsed {importData.ParsedRows} row(s).");

                SetStatus("Importing CSV data to database...");
                using var conn = _connectionManager.CreateNewConnection();
                var result = await _dbExecutor.ClearAndInsertAsync(conn, _currentDataSet, _schemas);
                _lastInsertedTables = result.InsertedTables
                    .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                SetStatus($"Imported {importData.ParsedRows} CSV row(s) into {result.TablesInserted} table(s).");
                LogInfo(
                    $"CSV import completed: {importData.ParsedRows} parsed row(s), {result.TablesInserted} table(s) inserted, {result.RowsDeleted} row(s) deleted.");
                MessageBox.Show(
                    $"CSV import completed.\r\n\r\n" +
                    $"- CSV files read: {importData.CsvFilesRead}\r\n" +
                    $"- CSV rows parsed: {importData.ParsedRows}\r\n" +
                    $"- Planned tables: {result.PlannedTables}\r\n" +
                    $"- Tables cleared: {result.TablesCleared}\r\n" +
                    $"- Rows deleted: {result.RowsDeleted}\r\n" +
                    $"- Tables inserted: {result.TablesInserted}\r\n" +
                    $"- Rows inserted: {result.RowsInserted}\r\n" +
                    $"- FK clear fallback used: {(result.UsedConstraintDisableFallback ? "YES" : "NO")}\r\n" +
                    $"- FK insert bypass used: {(result.UsedInsertConstraintBypass ? "YES" : "NO")}\r\n" +
                    $"- Folder: {folderPath}",
                    "Import Completed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("CSV import failed.");
                LogError($"CSV import failed: {BuildErrorChain(ex)}");
                MessageBox.Show(
                    $"CSV import failed:\r\n{BuildErrorChain(ex)}",
                    "Import Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                UpdateDbInsertButtonState();
            }
        }

        private void CopyBtn_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_scriptCleanOutput.Text))
            {
                LogWarn("Copy SQL requested with no script preview available.");
                return;
            }

            Clipboard.SetText(_scriptCleanOutput.Text);
            SetStatus("Executable SQL script copied to clipboard.");
            LogInfo("Copied executable SQL script to clipboard.");
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
            if (string.IsNullOrEmpty(_scriptCleanOutput.Text))
            {
                LogWarn("Save SQL requested with no script preview available.");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "SQL Files|*.sql|All Files|*.*",
                DefaultExt = "sql",
                FileName = $"test_data_{DateTime.Now:yyyyMMdd_HHmmss}.sql"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    File.WriteAllText(dialog.FileName, _scriptCleanOutput.Text);
                    SetStatus($"Saved to {dialog.FileName}");
                    LogInfo($"Saved executable SQL script to {dialog.FileName}.");
                }
                catch (Exception ex)
                {
                    SetStatus("Save failed.");
                    LogError($"Saving executable SQL script failed: {BuildErrorChain(ex)}");
                    MessageBox.Show(
                        $"Saving SQL script failed:\r\n{BuildErrorChain(ex)}",
                        "Save Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text;
        }

        private void RefreshInsertScriptPreview(string sourceLabel)
        {
            if (_currentDataSet == null)
            {
                _scriptCleanOutput.Clear();
                return;
            }

            _insertGenerator.Schemas = _schemas;
            _insertGenerator.HandleIdentityInsert = _schemas != null;
            _insertGenerator.IncludeComments = true;
            _insertGenerator.WrapInTransaction = false;

            _cleanupGenerator.SchemaName = _insertGenerator.SchemaName;

            var resetScript = _cleanupGenerator.GenerateResetScript(_currentDataSet, includeComments: true).Trim();
            var insertScript = _insertGenerator.GenerateScript(_currentDataSet).Trim();
            _scriptCleanOutput.Text = BuildExecutableInsertScript(resetScript, insertScript);

            LogInfo($"Prepared executable reset+insert script from {sourceLabel}.");
        }

        private static string BuildExecutableInsertScript(string resetScript, string insertScript)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- Executable reset + insert script");
            sb.AppendLine("SET NOCOUNT ON;");
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(resetScript))
            {
                sb.AppendLine(resetScript);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(insertScript))
            {
                sb.AppendLine(insertScript);
                sb.AppendLine();
            }

            sb.AppendLine("COMMIT TRANSACTION;");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            sb.AppendLine("    THROW;");
            sb.AppendLine("END CATCH;");
            return sb.ToString();
        }

        private void LogInfo(string message) => AppendLog("INFO", message);

        private void LogWarn(string message) => AppendLog("WARN", message);

        private void LogError(string message) => AppendLog("ERROR", message);

        private void AppendLog(string level, string message)
        {
            var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();
                if (line.Length == 0)
                    continue;

                _scriptOutput.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level,-5} {line}{Environment.NewLine}");
            }
        }

        private void ClearLastInsertedTables()
        {
            _lastInsertedTables.Clear();
            UpdateDbInsertButtonState();
        }

        private void MaxLengthMaxValueCheck_CheckedChanged(object? sender, EventArgs e)
        {
            if (!_currentDataSetIsGenerated || _currentDataSet == null)
                return;

            _currentDataSet = null;
            _currentDataSetIsGenerated = false;
            _scriptCleanOutput.Clear();
            UpdateDbInsertButtonState();

            var modeLabel = _maxLengthMaxValueCheck.Checked ? "Maxlength/MaxValue" : "sample-based";
            SetStatus($"Generation mode changed to {modeLabel}. Generate data again to apply the change.");
            LogInfo($"Generation mode changed to {modeLabel}; stale generated dataset was cleared.");
        }

        private void SelectAllScenariosCheck_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressScenarioSelectionSync || _scenarioList.Items.Count == 0)
                return;

            _suppressScenarioSelectionSync = true;
            try
            {
                for (int i = 0; i < _scenarioList.Items.Count; i++)
                {
                    _scenarioList.SetItemChecked(i, _selectAllScenariosCheck.Checked);
                }
            }
            finally
            {
                _suppressScenarioSelectionSync = false;
            }

            UpdateScenarioSelectAllCheckState();

            if (_selectAllScenariosCheck.Checked)
            {
                SetStatus($"Selected all {_scenarioList.Items.Count} scenario(s).");
                LogInfo($"Selected all {_scenarioList.Items.Count} scenario(s) for generation.");
            }
            else
            {
                SetStatus("Cleared all scenario selections.");
                LogInfo("Cleared all scenario selections.");
            }
        }

        private void ScenarioList_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_suppressScenarioSelectionSync)
                return;

            BeginInvoke(new Action(UpdateScenarioSelectAllCheckState));
        }

        private void UpdateScenarioSelectAllCheckState()
        {
            if (_selectAllScenariosCheck == null)
                return;

            var hasItems = _scenarioList.Items.Count > 0;
            var allChecked = hasItems && Enumerable.Range(0, _scenarioList.Items.Count).All(_scenarioList.GetItemChecked);

            _suppressScenarioSelectionSync = true;
            try
            {
                _selectAllScenariosCheck.Enabled = hasItems;
                _selectAllScenariosCheck.Checked = allChecked;
            }
            finally
            {
                _suppressScenarioSelectionSync = false;
            }
        }

        private void ResetScenarioSelectAllCheck()
        {
            if (_selectAllScenariosCheck == null)
                return;

            _suppressScenarioSelectionSync = true;
            try
            {
                _selectAllScenariosCheck.Enabled = false;
                _selectAllScenariosCheck.Checked = false;
            }
            finally
            {
                _suppressScenarioSelectionSync = false;
            }
        }

        private Dictionary<string, Dictionary<string, object?>> EnsureBaselineSampleRows(IEnumerable<TableSchema> schemas)
        {
            if (_tableSampleExtractor == null)
                return new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

            var schemaList = schemas.ToList();
            var missingSchemas = schemaList
                .Where(schema => !_baselineSampleRows.ContainsKey(schema.TableName))
                .ToList();

            if (missingSchemas.Count > 0)
            {
                var loadedSamples = _tableSampleExtractor.LoadSamples(missingSchemas);
                foreach (var entry in loadedSamples)
                {
                    _baselineSampleRows[entry.Key] = new Dictionary<string, object?>(entry.Value, StringComparer.OrdinalIgnoreCase);
                }
            }

            var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var schema in schemaList)
            {
                if (_baselineSampleRows.TryGetValue(schema.TableName, out var sample))
                {
                    result[schema.TableName] = new Dictionary<string, object?>(sample, StringComparer.OrdinalIgnoreCase);
                }
            }

            return result;
        }

        private void UpdateDbInsertButtonState()
        {
            _insertDbBtn.Enabled = _currentDataSet != null &&
                                   _connectionManager?.IsConnected == true;
            _importCsvBtn.Enabled = _connectionManager?.IsConnected == true &&
                                    !string.IsNullOrWhiteSpace(_exportFolderInput.Text);
            _exportCsvBtn.Enabled = _connectionManager?.IsConnected == true &&
                                    _lastInsertedTables.Any() &&
                                    !string.IsNullOrWhiteSpace(_exportFolderInput.Text);
        }

        private static HashSet<string> GetAllReferencedTableNames(ParsedQuery query)
        {
            var tableNames = query.Tables
                .Select(t => t.TableName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var subquery in query.Subqueries)
            {
                CollectSubqueryTables(subquery, tableNames);
            }

            return tableNames;
        }

        private static void CollectSubqueryTables(SubqueryInfo subquery, HashSet<string> tableNames)
        {
            foreach (var t in subquery.Tables)
            {
                tableNames.Add(t.TableName);
            }

            foreach (var nested in subquery.NestedSubqueries)
            {
                CollectSubqueryTables(nested, tableNames);
            }
        }

        private Dictionary<string, TableSchema> LoadSchemaClosure(IEnumerable<string> seedTables)
        {
            if (_schemaIntrospector == null)
                throw new InvalidOperationException("Schema introspector is not available.");

            var result = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(seedTables
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            while (queue.Count > 0)
            {
                var tableName = queue.Dequeue();
                if (result.ContainsKey(tableName))
                    continue;

                var schema = _schemaIntrospector.GetTableSchema(tableName);
                result[tableName] = schema;

                foreach (var fk in schema.ForeignKeys)
                {
                    if (!result.ContainsKey(fk.ReferencedTable))
                    {
                        queue.Enqueue(fk.ReferencedTable);
                    }
                }
            }

            return result;
        }

        private static async Task<bool> QueryReturnsRowsAsync(SqlConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
            return await reader.ReadAsync();
        }

        private static string FormatQueryRowsResult(bool? hasRows)
        {
            return hasRows switch
            {
                true => "YES",
                false => "NO",
                _ => "UNKNOWN (cannot execute verification query)"
            };
        }

        private static string BuildErrorChain(Exception ex)
        {
            var messages = new List<string>();
            var current = ex;
            while (current != null)
            {
                messages.Add(current.Message);
                current = current.InnerException;
            }

            return string.Join("\r\n--> ", messages);
        }

        // ═══════════════════════════════════════════════════════════════
        // Helper: Create styled button
        // ═══════════════════════════════════════════════════════════════

        private Control CreateLabeledInputPanel(string labelText, Control input)
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(12, 2, 0, 0),
                Padding = new Padding(0),
                BackColor = Color.White
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = _textPrimary,
                Margin = new Padding(0, 8, 8, 0),
                BackColor = Color.White
            };

            input.Margin = new Padding(0);
            panel.Controls.Add(label, 0, 0);
            panel.Controls.Add(input, 1, 0);
            return panel;
        }

        private Panel CreateCard(string title, Color accentColor, Control content, Control? headerAccessory = null)
        {
            var borderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(222, 226, 234),
                Padding = new Padding(1),
                Margin = new Padding(0)
            };

            var innerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(12),
                Margin = new Padding(0)
            };

            var headerPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 30,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White
            };
            headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var headerLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = _textPrimary,
                Padding = new Padding(0, 2, 0, 0),
                BackColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };

            content.Dock = DockStyle.Fill;
            content.Margin = new Padding(0);

            headerPanel.Controls.Add(headerLabel, 0, 0);
            if (headerAccessory != null)
            {
                headerAccessory.Anchor = AnchorStyles.Right;
                headerAccessory.Margin = new Padding(0);
                headerPanel.Controls.Add(headerAccessory, 1, 0);
            }

            innerPanel.Controls.Add(content);
            innerPanel.Controls.Add(headerPanel);
            borderPanel.Controls.Add(innerPanel);
            return borderPanel;
        }

        private Panel CreateSurface(Control content, Padding padding)
        {
            var borderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(228, 232, 240),
                Padding = new Padding(1),
                Margin = new Padding(0)
            };

            var innerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = padding,
                Margin = new Padding(0)
            };

            content.Dock = DockStyle.Fill;
            content.Margin = new Padding(0);

            innerPanel.Controls.Add(content);
            borderPanel.Controls.Add(innerPanel);
            return borderPanel;
        }

        private Button CreateLegacyButton(string text, int width)
        {
            return new Button
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
        }

        private Button CreateButton(string text, Color accentColor, int width)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(width, 40),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.5F),
                ForeColor = Color.White,
                BackColor = accentColor,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 0, 12, 0)
            };
            btn.FlatAppearance.BorderColor = accentColor;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(accentColor, 0.12f);
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(accentColor, 0.08f);
            btn.EnabledChanged += (_, _) =>
            {
                if (btn.Enabled)
                {
                    btn.ForeColor = Color.White;
                    btn.BackColor = accentColor;
                    btn.FlatAppearance.BorderColor = accentColor;
                }
                else
                {
                    btn.ForeColor = Color.FromArgb(168, 174, 186);
                    btn.BackColor = Color.FromArgb(249, 250, 251);
                    btn.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 230);
                }
            };

            return btn;
        }
    }
}
