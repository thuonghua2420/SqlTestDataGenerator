namespace SqlTestDataGenerator.UI
{
    /// <summary>
    /// Database connection dialog form.
    /// </summary>
    public class ConnectionForm : Form
    {
        public string ServerName { get; private set; } = "";
        public string DatabaseName { get; private set; } = "";
        public string Username { get; private set; } = "";
        public string Password { get; private set; } = "";
        public bool UseWindowsAuth { get; private set; } = true;

        // Controls
        private TextBox _serverInput = null!;
        private TextBox _databaseInput = null!;
        private RadioButton _winAuthRadio = null!;
        private RadioButton _sqlAuthRadio = null!;
        private TextBox _usernameInput = null!;
        private TextBox _passwordInput = null!;
        private Label _usernameLabel = null!;
        private Label _passwordLabel = null!;
        private Button _testBtn = null!;
        private Button _connectBtn = null!;
        private Button _cancelBtn = null!;
        private Label _statusLabel = null!;

        // Colors
        private readonly Color _accentGreen = Color.Green;
        private readonly Color _textSecondary = SystemColors.GrayText;

        public ConnectionForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Connect to Database";
            this.Size = new Size(450, 420);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = SystemColors.Control;
            this.ForeColor = SystemColors.ControlText;
            this.Font = new Font("Segoe UI", 10F);

            int y = 20;
            int inputX = 140;
            int inputWidth = 270;

            // Server
            AddLabel("Server:", 20, y);
            _serverInput = AddTextBox(inputX, y, inputWidth);
            _serverInput.Text = ".";
            y += 38;

            // Database
            AddLabel("Database:", 20, y);
            _databaseInput = AddTextBox(inputX, y, inputWidth);
            y += 45;

            // Authentication
            _winAuthRadio = new RadioButton
            {
                Text = "Windows Authentication",
                Location = new Point(20, y),
                AutoSize = true,
                Checked = true,
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9.5F)
            };
            _winAuthRadio.CheckedChanged += AuthRadio_CheckedChanged;
            this.Controls.Add(_winAuthRadio);
            y += 28;

            _sqlAuthRadio = new RadioButton
            {
                Text = "SQL Server Authentication",
                Location = new Point(20, y),
                AutoSize = true,
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9.5F)
            };
            this.Controls.Add(_sqlAuthRadio);
            y += 38;

            // Username
            _usernameLabel = AddLabel("Username:", 20, y);
            _usernameInput = AddTextBox(inputX, y, inputWidth);
            _usernameInput.Enabled = false;
            y += 38;

            // Password
            _passwordLabel = AddLabel("Password:", 20, y);
            _passwordInput = AddTextBox(inputX, y, inputWidth);
            _passwordInput.UseSystemPasswordChar = true;
            _passwordInput.Enabled = false;
            y += 45;

            // Status
            _statusLabel = new Label
            {
                Text = "",
                Location = new Point(20, y),
                Size = new Size(400, 20),
                ForeColor = _textSecondary,
                Font = new Font("Segoe UI", 9F)
            };
            this.Controls.Add(_statusLabel);
            y += 30;

            // Buttons
            _testBtn = CreateStyledButton("Test", Color.Blue, 80);
            _testBtn.Location = new Point(140, y);
            _testBtn.Click += TestBtn_Click;

            _connectBtn = CreateStyledButton("Connect", _accentGreen, 95);
            _connectBtn.Location = new Point(230, y);
            _connectBtn.Click += ConnectBtn_Click;

            _cancelBtn = CreateStyledButton("Cancel", Color.Gray, 80);
            _cancelBtn.Location = new Point(335, y);
            _cancelBtn.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

            this.Controls.AddRange(new Control[] { _testBtn, _connectBtn, _cancelBtn });
            this.AcceptButton = _connectBtn;
            this.CancelButton = _cancelBtn;
        }

        private void AuthRadio_CheckedChanged(object? sender, EventArgs e)
        {
            bool sqlAuth = _sqlAuthRadio.Checked;
            _usernameInput.Enabled = sqlAuth;
            _passwordInput.Enabled = sqlAuth;
        }

        private async void TestBtn_Click(object? sender, EventArgs e)
        {
            _statusLabel.Text = "Testing connection...";
            _statusLabel.ForeColor = _textSecondary;

            using var connMgr = new Database.DatabaseConnectionManager();
            var (success, message) = await connMgr.ConnectAsync(
                _serverInput.Text, _databaseInput.Text,
                _usernameInput.Text, _passwordInput.Text,
                _winAuthRadio.Checked);

            _statusLabel.Text = success ? "✓ Connection successful!" : $"✗ {message}";
            _statusLabel.ForeColor = success ? _accentGreen : Color.Red;
        }

        private void ConnectBtn_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_serverInput.Text) || string.IsNullOrWhiteSpace(_databaseInput.Text))
            {
                _statusLabel.Text = "Server and Database are required.";
                _statusLabel.ForeColor = Color.Red;
                return;
            }

            ServerName = _serverInput.Text.Trim();
            DatabaseName = _databaseInput.Text.Trim();
            Username = _usernameInput.Text.Trim();
            Password = _passwordInput.Text;
            UseWindowsAuth = _winAuthRadio.Checked;

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private Label AddLabel(string text, int x, int y)
        {
            var label = new Label
            {
                Text = text,
                Location = new Point(x, y + 4),
                AutoSize = true,
                ForeColor = SystemColors.ControlText,
                Font = new Font("Segoe UI", 9.5F)
            };
            this.Controls.Add(label);
            return label;
        }

        private TextBox AddTextBox(int x, int y, int width)
        {
            var textBox = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 28),
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10F)
            };
            this.Controls.Add(textBox);
            return textBox;
        }

        private Button CreateStyledButton(string text, Color accentColor, int width)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Standard,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = SystemColors.ControlText,
                BackColor = SystemColors.Control,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = true
            };
            return btn;
        }
    }
}
