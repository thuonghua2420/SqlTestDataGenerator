namespace SqlTestDataGenerator.UI;

public class DeleteFolderDialog : Form
{
    private Label lblPrompt = null!;
    private TextBox txtFolderName = null!;
    private Button btnOK = null!;
    private Button btnCancel = null!;

    private static readonly Color ClrText = Color.FromArgb(30, 30, 30);
    private static readonly Font  UiFont  = new Font("Segoe UI", 9.5f);

    public string FolderName => txtFolderName.Text;

    public DeleteFolderDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = "Xác nhận xóa";
        this.Size = new Size(400, 200);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.StartPosition = FormStartPosition.CenterParent;
        this.BackColor = Color.White;

        lblPrompt = new Label
        {
            Text = "Nhập tên folder cần xóa:",
            AutoSize = true,
            Location = new Point(20, 20),
            Font = UiFont,
            ForeColor = ClrText
        };

        txtFolderName = new TextBox
        {
            Location = new Point(20, 50),
            Width = 345,
            Font = UiFont,
            BorderStyle = BorderStyle.FixedSingle
        };

        btnOK = new Button
        {
            Text = "Xác nhận",
            DialogResult = DialogResult.OK,
            Location = new Point(190, 105),
            Width = 90,
            Height = 34,
            BackColor = Color.White,
            ForeColor = ClrText,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont,
            Cursor = Cursors.Hand
        };
        ApplyButtonStyle(btnOK);

        btnCancel = new Button
        {
            Text = "Hủy",
            DialogResult = DialogResult.Cancel,
            Location = new Point(290, 105),
            Width = 80,
            Height = 34,
            BackColor = Color.White,
            ForeColor = ClrText,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont,
            Cursor = Cursors.Hand
        };
        ApplyButtonStyle(btnCancel);
        
        this.AcceptButton = btnOK;
        this.CancelButton = btnCancel;

        this.Controls.AddRange(new Control[] { lblPrompt, txtFolderName, btnOK, btnCancel });
    }

    private static void ApplyButtonStyle(Button btn)
    {
        btn.FlatStyle = FlatStyle.Standard;
        btn.Font = new Font("Segoe UI", 9F);
        btn.ForeColor = Color.Black;
        btn.BackColor = SystemColors.Control;
        btn.TextAlign = ContentAlignment.MiddleCenter;
        btn.UseVisualStyleBackColor = true;
    }
}

