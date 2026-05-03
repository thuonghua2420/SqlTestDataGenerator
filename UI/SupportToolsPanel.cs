using ClosedXML.Excel;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace SqlTestDataGenerator.UI;

public class SupportToolsPanel : UserControl
{
    // ── Shared ──
    private TextBox txtPathFolder = null!;
    private TabControl tabControl = null!;

    // ── Tab 1 ──
    private TabPage tabUTR = null!;
    private TextBox txtOldName = null!;
    private TextBox txtNewName = null!;
    private RichTextBox rtbLog1 = null!;

    // ── Tab 2 ──
    private TabPage tabExcel = null!;
    private CheckBox chkFontSize = null!;
    private TextBox txtFontSize = null!;
    private ComboBox cmbFontName = null!;
    private TextBox txtZoom = null!;
    private Button btnFormatExcel = null!;
    private TextBox txtImgScale = null!;
    private Button btnFormatEvd = null!;
    private RichTextBox rtbLog2 = null!;

    // ── Color palette ──
    private static readonly Color ClrPrimary = Color.FromArgb(0, 120, 212);
    private static readonly Color ClrDanger  = Color.FromArgb(196, 43, 28);
    private static readonly Color ClrSuccess = Color.FromArgb(16, 124, 16);
    private static readonly Color ClrError   = Color.FromArgb(164, 38, 44);
    private static readonly Color ClrInfo    = Color.FromArgb(0, 90, 158);
    private static readonly Color ClrMuted   = Color.FromArgb(96, 96, 96);
    private static readonly Color ClrBg      = Color.FromArgb(243, 243, 243);
    private static readonly Color ClrSurface = Color.White;
    private static readonly Color ClrBorder  = Color.FromArgb(210, 210, 210);
    private static readonly Color ClrText    = Color.FromArgb(30, 30, 30);
    private static readonly Font  UiFont     = new Font("Segoe UI", 9.5f);
    private static readonly Font  MonoFont   = new Font("Consolas", 9f);

    public SupportToolsPanel()
    {
        InitializeComponent();
    }

    // ─────────────────────────────────────────────────────────
    //  UI factories
    // ─────────────────────────────────────────────────────────
    private static Button Btn(string text, int x, int y, int w = 130, int h = 32, bool isPrimary = false, Color? accentColor = null)
    {
        return new Button
        {
            Text = text, Location = new Point(x, y), Size = new Size(w, h),
            FlatStyle = FlatStyle.Standard,
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.Black,
            BackColor = SystemColors.Control,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = true
        };
    }

    private static Label Lbl(string text, int x, int y)
        => new Label { Text = text, AutoSize = true, Location = new Point(x, y),
                       Font = UiFont, ForeColor = ClrText };

    private static TextBox Txt(int x, int y, int w, string val = "")
        => new TextBox { Location = new Point(x, y), Width = w, Text = val,
                         Font = UiFont, BorderStyle = BorderStyle.FixedSingle };

    private static Panel Card(int x, int y, int w, int h)
        => new Panel { Location = new Point(x, y), Size = new Size(w, h),
                       BackColor = ClrSurface, BorderStyle = BorderStyle.FixedSingle,
                       Padding = new Padding(14) };

    private static RichTextBox LogBox(int x, int y, int w, int h)
        => new RichTextBox
        {
            Location = new Point(x, y), Size = new Size(w, h),
            ReadOnly = true, BackColor = ClrSurface, ForeColor = ClrText,
            Font = MonoFont, ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false, BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

    // ─────────────────────────────────────────────────────────
    //  InitializeComponent
    // ─────────────────────────────────────────────────────────
    private void InitializeComponent()
    {
        this.Dock = DockStyle.Fill;
        this.BackColor = ClrBg;
        this.Font = UiFont;

        // ── Header bar (white strip) ──────────────────────────
        var header = new Panel
        {
            Dock = DockStyle.Top, Height = 52,
            BackColor = ClrSurface, Padding = new Padding(14, 0, 14, 0)
        };
        header.Paint += (_, e) =>
            e.Graphics.DrawLine(new Pen(ClrBorder), 0, header.Height - 1, header.Width, header.Height - 1);

        var lblPath = Lbl("Path folder sản phẩm:", 14, 0);
        lblPath.Top = (header.Height - lblPath.PreferredHeight) / 2;

        txtPathFolder = new TextBox
        {
            Font = UiFont, BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        PositionHeaderControls(header, lblPath, txtPathFolder, out var btnBrowse);
        btnBrowse.Click += BtnBrowse_Click;

        header.Controls.AddRange(new Control[] { lblPath, txtPathFolder, btnBrowse });
        header.Resize += (_, _) => PositionHeaderControls(header, lblPath, txtPathFolder, out _);

        // ── TabControl ────────────────────────────────────────
        tabControl = new TabControl
        {
            Dock = DockStyle.Fill, Font = UiFont, Padding = new Point(18, 6)
        };

        tabUTR   = new TabPage("  Support UTR  ")  { BackColor = ClrBg, Padding = new Padding(0) };
        tabExcel = new TabPage("  Format Excel  ") { BackColor = ClrBg, Padding = new Padding(0) };

        BuildTab1();
        BuildTab2();

        tabControl.TabPages.Add(tabUTR);
        tabControl.TabPages.Add(tabExcel);

        this.Controls.Add(tabControl);
        this.Controls.Add(header);
    }

    private static void PositionHeaderControls(Panel hdr, Label lbl, TextBox txt, out Button browse)
    {
        int cy = (hdr.Height - txt.Height) / 2;
        const int inputX = 195;
        const int browseW = 88;
        int browseX = hdr.Width - browseW - 14;
        int txtW = browseX - inputX - 8;

        lbl.Top = (hdr.Height - lbl.PreferredHeight) / 2;
        txt.Location = new Point(inputX, cy);
        txt.Width = Math.Max(txtW, 100);

        browse = hdr.Controls.OfType<Button>().FirstOrDefault()
                 ?? Btn("Chọn...", 0, 0, 88, 32);
        browse.Location = new Point(browseX, (hdr.Height - browse.Height) / 2);
        browse.Width = browseW;
    }

    // ─────────────────────────────────────────────────────────
    //  Tab 1 – Support UTR
    // ─────────────────────────────────────────────────────────
    private void BuildTab1()
    {
        const int pad = 12, lx = 14, ix = 195, iw = 430;

        // Input card
        var card = Card(pad, pad, 100, 145); // Tăng chiều cao để không bị khuất nút
        card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var lblOld = Lbl("Tên trước khi thay đổi:", lx, 16);
        txtOldName = Txt(ix, 13, iw);

        var lblNew = Lbl("Tên sau khi thay đổi:", lx, 50);
        txtNewName = Txt(ix, 47, iw);

        var btnFolder = Btn("Đổi tên Folder", lx, 82, 170);
        var btnFile   = Btn("Đổi tên File",   lx + 185, 82, 150);
        var btnDel    = Btn("Xóa Folder",     lx + 350, 82, 140);

        btnFolder.Click += BtnRenameFolder_Click;
        btnFile.Click   += BtnRenameFile_Click;
        btnDel.Click    += BtnDeleteFolder_Click;

        card.Controls.AddRange(new Control[] { lblOld, txtOldName, lblNew, txtNewName, btnFolder, btnFile, btnDel });

        // Log section label
        var lblLog = Lbl("Kết quả thực thi:", pad, pad + card.Height + 10);

        // Log box
        rtbLog1 = LogBox(pad, pad + card.Height + 32, 100, 300);

        tabUTR.Controls.AddRange(new Control[] { card, lblLog, rtbLog1 });

        tabUTR.Resize += (_, _) =>
        {
            int w = tabUTR.ClientSize.Width - pad * 2;
            card.Width = w;
            lblLog.Top = pad + card.Height + 10;
            rtbLog1.Location = new Point(pad, pad + card.Height + 32);
            rtbLog1.Size = new Size(w, tabUTR.ClientSize.Height - rtbLog1.Top - pad);
        };
    }

    // ─────────────────────────────────────────────────────────
    //  Tab 2 – Format Excel
    // ─────────────────────────────────────────────────────────
    private void BuildTab2()
    {
        const int pad = 12, lx = 14, ix = 140, iw = 200, rowH = 36;

        // Input card — 3 rows + separator + section label + 1 row for image scale
        var card = Card(pad, pad, 100, 230); // Tăng chiều cao để không bị khuất
        card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        int y = 13;

        // Row 1: Font Size (checkbox để enable/disable)
        chkFontSize = new CheckBox
        {
            Text = "Font Size:", Location = new Point(lx, y + 2),
            AutoSize = true, Font = UiFont, ForeColor = ClrText, Checked = false
        };
        txtFontSize = Txt(ix, y, iw, "11");
        txtFontSize.Enabled = false;
        chkFontSize.CheckedChanged += (_, _) => txtFontSize.Enabled = chkFontSize.Checked;
        y += rowH;

        // Row 2: Font Name
        var lblFn = Lbl("Font Name:", lx, y + 3);
        cmbFontName = new ComboBox
        {
            Location = new Point(ix, y), Width = iw,
            DropDownStyle = ComboBoxStyle.DropDown, Font = UiFont,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        foreach (var ff in FontFamily.Families) cmbFontName.Items.Add(ff.Name);
        cmbFontName.Text = "Meiryo UI";
        y += rowH;

        // Row 3: Zoom
        var lblZm = Lbl("Zoom (%):", lx, y + 3);
        txtZoom = Txt(ix, y, iw, "100");
        y += rowH;

        // Separator
        var sep = new Panel
        {
            Location = new Point(lx, y + 4), Height = 1,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = ClrBorder
        };
        card.Resize += (_, _) => sep.Width = card.ClientSize.Width - lx * 2;
        y += 18;

        // Row 4 & 5: Image Size (for Format EVD)
        var lblImgSec = Lbl("Image Scale:", lx, y + 3);
        lblImgSec.Font = new Font(UiFont, FontStyle.Bold);
        y += rowH;

        var lblScale = Lbl("Scale (%):", lx, y + 3);
        txtImgScale = Txt(ix, y, 70, "60");
        var lblScaleNote = Lbl("% — áp dụng cho cả Width & Height", ix + 78, y + 3);
        lblScaleNote.ForeColor = ClrMuted;

        card.Controls.AddRange(new Control[]
        {
            chkFontSize, txtFontSize, lblFn, cmbFontName, lblZm, txtZoom,
            sep, lblImgSec, lblScale, txtImgScale, lblScaleNote
        });

        // Buttons row (below card)
        btnFormatExcel = Btn("Format Excel", pad, pad + card.Height + 12, 180);
        btnFormatExcel.Click += BtnFormatExcel_Click;

        btnFormatEvd = Btn("Format EVD", pad + 190, pad + card.Height + 12, 170);
        btnFormatEvd.Click += BtnFormatEvd_Click;

        // Log
        var lblLog = Lbl("Kết quả thực thi:", pad, pad + card.Height + 68);
        rtbLog2 = LogBox(pad, pad + card.Height + 92, 100, 300);

        tabExcel.Controls.AddRange(new Control[] { card, btnFormatExcel, btnFormatEvd, lblLog, rtbLog2 });

        tabExcel.Resize += (_, _) =>
        {
            int w = tabExcel.ClientSize.Width - pad * 2;
            card.Width = w;
            int btnY = pad + card.Height + 12;
            btnFormatExcel.Top = btnY;
            btnFormatEvd.Top  = btnY;
            lblLog.Top = pad + card.Height + 68;
            rtbLog2.Location = new Point(pad, pad + card.Height + 92);
            rtbLog2.Size = new Size(w, tabExcel.ClientSize.Height - rtbLog2.Top - pad);
        };
    }

    // ─────────────────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────────────────
    private void BtnBrowse_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Chọn path folder sản phẩm" };
        if (dlg.ShowDialog() == DialogResult.OK)
            txtPathFolder.Text = dlg.SelectedPath;
    }

    private bool GetValidPath(out string path)
    {
        path = txtPathFolder.Text.Trim();
        if (Directory.Exists(path)) return true;
        MessageBox.Show("Đường dẫn không tồn tại hoặc chưa được nhập!", "Lỗi",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    private void Log(RichTextBox rtb, string message, Color? color = null)
    {
        if (InvokeRequired) { Invoke(() => Log(rtb, message, color)); return; }
        Color c;
        if (color.HasValue)        c = color.Value;
        else if (message.TrimStart().StartsWith("✓")) c = ClrSuccess;
        else if (message.TrimStart().StartsWith("✗")) c = ClrError;
        else if (message.StartsWith("Hoàn thành") || message.StartsWith("Xong")) c = ClrInfo;
        else c = ClrMuted;

        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionColor = c;
        rtb.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        rtb.ScrollToCaret();
    }

    // ─────────────────────────────────────────────────────────
    //  Tab 1 handlers
    // ─────────────────────────────────────────────────────────
    private void BtnRenameFolder_Click(object? sender, EventArgs e)
    {
        if (!GetValidPath(out var root)) return;

        string oldN = txtOldName.Text.Trim();
        string newN = txtNewName.Text.Trim();

        if (string.IsNullOrEmpty(oldN))
        {
            MessageBox.Show("Vui lòng nhập tên trước khi thay đổi!", "Thiếu thông tin",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        rtbLog1.Clear();
        Log(rtbLog1, $"Đổi tên folder: \"{oldN}\" → \"{newN}\"");
        int count = 0;

        try
        {
            var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
            Array.Sort(dirs, (a, b) => b.Length.CompareTo(a.Length));

            foreach (var dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (!name.Contains(oldN)) continue;

                string newName = name.Replace(oldN, newN);
                string newPath = Path.Combine(Path.GetDirectoryName(dir)!, newName);
                try
                {
                    Directory.Move(dir, newPath);
                    Log(rtbLog1, $"  ✓ {name}  →  {newName}");
                    count++;
                }
                catch (Exception ex)
                {
                    Log(rtbLog1, $"  ✗ {name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) { Log(rtbLog1, $"  ✗ Lỗi hệ thống: {ex.Message}"); }

        Log(rtbLog1, $"Xong! Đã đổi tên {count} folder.", ClrInfo);
        MessageBox.Show($"Đã đổi tên {count} folder!", "Hoàn thành",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BtnRenameFile_Click(object? sender, EventArgs e)
    {
        if (!GetValidPath(out var root)) return;

        string oldN = txtOldName.Text.Trim();
        string newN = txtNewName.Text.Trim();

        if (string.IsNullOrEmpty(oldN))
        {
            MessageBox.Show("Vui lòng nhập tên trước khi thay đổi!", "Thiếu thông tin",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        rtbLog1.Clear();
        Log(rtbLog1, $"Đổi tên file: \"{oldN}\" → \"{newN}\"");
        int count = 0;

        try
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!name.Contains(oldN)) continue;

                string newName = name.Replace(oldN, newN);
                string newPath = Path.Combine(Path.GetDirectoryName(file)!, newName);
                try
                {
                    File.Move(file, newPath);
                    Log(rtbLog1, $"  ✓ {name}  →  {newName}");
                    count++;
                }
                catch (Exception ex) { Log(rtbLog1, $"  ✗ {name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log(rtbLog1, $"  ✗ Lỗi hệ thống: {ex.Message}"); }

        Log(rtbLog1, $"Xong! Đã đổi tên {count} file.", ClrInfo);
        MessageBox.Show($"Đã đổi tên {count} file!", "Hoàn thành",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BtnDeleteFolder_Click(object? sender, EventArgs e)
    {
        if (!GetValidPath(out var root)) return;

        using var dlg = new DeleteFolderDialog();
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string target = dlg.FolderName.Trim();
        if (string.IsNullOrEmpty(target)) return;

        if (MessageBox.Show(
                $"Xóa TẤT CẢ folder có tên \"{target}\"?\n\nHành động này KHÔNG THỂ hoàn tác!",
                "Xác nhận xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        rtbLog1.Clear();
        Log(rtbLog1, $"Xóa folder: \"{target}\"");
        int count = 0;

        try
        {
            var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
            Array.Sort(dirs, (a, b) => b.Length.CompareTo(a.Length));

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                if (!Path.GetFileName(dir).Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Directory.Delete(dir, recursive: true);
                    Log(rtbLog1, $"  ✓ Xóa: {dir}");
                    count++;
                }
                catch (Exception ex) { Log(rtbLog1, $"  ✗ {dir}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log(rtbLog1, $"  ✗ Lỗi hệ thống: {ex.Message}"); }

        Log(rtbLog1, $"Xong! Đã xóa {count} folder.", ClrInfo);
        MessageBox.Show($"Đã xóa {count} folder!", "Hoàn thành",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ─────────────────────────────────────────────────────────
    //  Tab 2 handler
    // ─────────────────────────────────────────────────────────
    private async void BtnFormatExcel_Click(object? sender, EventArgs e)
    {
        if (!GetValidPath(out var root)) return;

        double fontSize = 0;
        if (chkFontSize.Checked)
        {
            string fontSizeStr = txtFontSize.Text.Trim().Replace(',', '.');
            if (!double.TryParse(fontSizeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out fontSize) || fontSize <= 0)
            {
                MessageBox.Show("Font size không hợp lệ! Nhập số dương (ví dụ: 11 hoặc 11.5).",
                    "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (!int.TryParse(txtZoom.Text.Trim(), out int zoom) || zoom < 10 || zoom > 400)
        {
            MessageBox.Show("Zoom phải là số nguyên từ 10 đến 400.",
                "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string fontName = cmbFontName.Text.Trim();

        rtbLog2.Clear();
        bool applyFontSize = chkFontSize.Checked;
        string sizeLabel = applyFontSize ? fontSize.ToString() : "giữ nguyên";
        Log(rtbLog2, $"Bắt đầu format Excel... Font: \"{fontName}\" | Size: {sizeLabel} | Zoom: {zoom}%");

        btnFormatExcel.Enabled = false;
        try { await Task.Run(() => FormatExcelFiles(root, fontSize, applyFontSize, fontName, zoom)); }
        finally { btnFormatExcel.Enabled = true; }
    }

    private async void BtnFormatEvd_Click(object? sender, EventArgs e)
    {
        if (!GetValidPath(out var root)) return;

        string scaleStr = txtImgScale.Text.Trim().Replace(',', '.');
        if (!double.TryParse(scaleStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double scale)
            || scale <= 0 || scale > 500)
        {
            MessageBox.Show("Scale không hợp lệ! Nhập số dương từ 1 đến 500 (%).",
                "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        rtbLog2.Clear();
        Log(rtbLog2, $"Format EVD — Scale: {scale}% (Width & Height), gap: 3 rows");

        btnFormatEvd.Enabled = false;
        try { await Task.Run(() => FormatEvdFiles(root, scale)); }
        finally { btnFormatEvd.Enabled = true; }
    }

    private void FormatEvdFiles(string root, double scalePct)
    {
        var ssNs  = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var rNs   = XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        var pkgNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");
        var xdrNs = XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing");

        var files = Directory.GetFiles(root, "*.xls?", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Log(rtbLog2, $"Tìm thấy {files.Length} file.");
        int success = 0, failed = 0;

        foreach (var file in files)
        {
            Log(rtbLog2, $"Xử lý: {Path.GetRelativePath(root, file)}");
            try
            {
                int drawingsProcessed = 0;
                using (var zip = new ZipArchive(
                    File.Open(file, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
                {

                    var sharedStrings = EvdLoadSharedStrings(zip, ssNs);

                    var wbEntry = zip.GetEntry("xl/workbook.xml");
                    if (wbEntry == null) { Log(rtbLog2, "  ⚠ Bỏ qua: không tìm thấy workbook.xml"); continue; }
                    XDocument wbDoc; using (var s = wbEntry.Open()) wbDoc = XDocument.Load(s);

                    var sheetRIds = wbDoc.Descendants(ssNs + "sheet")
                        .Select(e => e.Attribute(rNs + "id")?.Value ?? "").ToList();

                    var wbRels = zip.GetEntry("xl/_rels/workbook.xml.rels");
                    var ridToSheet = new Dictionary<string, string>();
                    if (wbRels != null)
                    {
                        XDocument rd; using (var s = wbRels.Open()) rd = XDocument.Load(s);
                        foreach (var r in rd.Descendants(pkgNs + "Relationship"))
                        {
                            string? id = r.Attribute("Id")?.Value, tgt = r.Attribute("Target")?.Value;
                            if (id != null && tgt != null)
                                ridToSheet[id] = tgt.StartsWith("/") ? tgt.TrimStart('/') : "xl/" + tgt;
                        }
                    }

                    foreach (var rid in sheetRIds)
                    {
                        if (!ridToSheet.TryGetValue(rid, out var sheetPath)) continue;
                        var sheetEntry = zip.GetEntry(sheetPath);
                        if (sheetEntry == null) continue;

                        string relsPath = sheetPath.Replace("xl/worksheets/", "xl/worksheets/_rels/") + ".rels";
                        var sheetRelsEntry = zip.GetEntry(relsPath);
                        if (sheetRelsEntry == null) continue;

                        XDocument sheetRels; using (var s = sheetRelsEntry.Open()) sheetRels = XDocument.Load(s);

                        string? drawPath = null;
                        foreach (var r in sheetRels.Descendants(pkgNs + "Relationship"))
                        {
                            if (r.Attribute("Type")?.Value?.Contains("drawing") != true) continue;
                            string? tgt = r.Attribute("Target")?.Value;
                            if (tgt == null) continue;
                            drawPath = tgt.StartsWith("..") ? ResolvePath(sheetPath, tgt) : "xl/" + tgt.TrimStart('/');
                            break;
                        }
                        if (drawPath == null) continue;

                        var drawEntry = zip.GetEntry(drawPath);
                        if (drawEntry == null) continue;

                        XDocument sheetDoc; using (var s = sheetEntry.Open()) sheetDoc = XDocument.Load(s);
                        var colW = EvdColWidths(sheetDoc, ssNs);
                        var rowH = EvdRowHeights(sheetDoc, ssNs);

                        XDocument drawDoc; using (var s = drawEntry.Open()) drawDoc = XDocument.Load(s);

                        var (drawMod, sheetMod) = EvdProcessDrawing(zip, drawPath, drawDoc, xdrNs,
                            sheetDoc, ssNs, sharedStrings, scalePct, colW, rowH);

                        if (drawMod)
                        {
                            using var dws = drawEntry.Open();
                            dws.SetLength(0);
                            drawDoc.Save(dws);
                            drawingsProcessed++;
                        }

                        if (sheetMod)
                        {
                            using var sws = sheetEntry.Open();
                            sws.SetLength(0);
                            sheetDoc.Save(sws);
                        }
                    }
                }

                ResetExcelView(file, zoomScale: 100);
                Log(rtbLog2, drawingsProcessed > 0
                    ? $"  ✓ Xử lý {drawingsProcessed} drawing(s)"
                    : "  ✓ Không có ảnh cần xử lý");
                success++;
            }
            catch (Exception ex) { Log(rtbLog2, $"  ✗ {ex.Message}"); failed++; }
        }

        Log(rtbLog2, $"Hoàn thành EVD! Thành công: {success} | Lỗi: {failed}", ClrInfo);
        Invoke(() => MessageBox.Show(
            $"Format EVD hoàn thành!\n\nThành công: {success}\nLỗi: {failed}",
            "Kết quả", MessageBoxButtons.OK, MessageBoxIcon.Information));
    }

    // ── EVD drawing processor ─────────────────────────────────
    private static (bool drawingModified, bool sheetModified) EvdProcessDrawing(
        ZipArchive zip, string drawingPath,
        XDocument drawDoc, XNamespace xdr,
        XDocument sheetDoc, XNamespace ssNs, string[] sharedStrings,
        double scalePct,
        Dictionary<int, long> colW, Dictionary<int, long> rowH)
    {
        var aNs   = XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/main");
        var rNs   = XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        var pkgNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");

        string drawRelsPath = drawingPath.Replace("xl/drawings/", "xl/drawings/_rels/") + ".rels";
        var ridToImg = new Dictionary<string, string>();
        var drawRelsEntry = zip.GetEntry(drawRelsPath);
        if (drawRelsEntry != null)
        {
            XDocument dr; using (var s = drawRelsEntry.Open()) dr = XDocument.Load(s);
            foreach (var rel in dr.Descendants(pkgNs + "Relationship"))
            {
                string? id = rel.Attribute("Id")?.Value, tgt = rel.Attribute("Target")?.Value;
                if (id == null || tgt == null) continue;
                ridToImg[id] = tgt.StartsWith("..") ? ResolvePath(drawingPath, tgt) : "xl/" + tgt.TrimStart('/');
            }
        }

        var anchors = drawDoc.Root!.Elements()
            .Where(e => (e.Name.LocalName == "twoCellAnchor" || e.Name.LocalName == "oneCellAnchor")
                     && e.Descendants(xdr + "pic").Any())
            .ToList();
        if (anchors.Count == 0) return (false, false);

        const long DefColEmu = 609600L;
        const long DefRowEmu = 190500L;
        const int  Gap       = 3;
        const int  SecondImageCol = 19;

        (long w, long h) GetImgSize(XElement anchor)
        {
            string? rId = anchor.Descendants(xdr + "pic").First()
                .Descendants(aNs + "blip").FirstOrDefault()?.Attribute(rNs + "embed")?.Value;
            (long nCx, long nCy) = rId != null && ridToImg.TryGetValue(rId, out var ip)
                ? EvdNaturalSizeEmu(zip, ip) : (0L, 0L);
            if (nCx <= 0 || nCy <= 0) (nCx, nCy) = EvdCurrentSizeEmu(anchor, xdr, aNs);
            return ((long)(nCx * scalePct / 100.0), (long)(nCy * scalePct / 100.0));
        }

        // Layout group; returns row index after last image + gap
        int LayoutGroup(IEnumerable<XElement> group, int targetCol, int startRow)
        {
            int curRow = startRow;
            foreach (var anchor in group.OrderBy(a => EvdFromRow(a, xdr)))
            {
                var (imgW, imgH) = GetImgSize(anchor);
                if (imgW <= 0 || imgH <= 0) continue;
                EvdSetAnchor(anchor, xdr, targetCol, 0, curRow, 0, imgW, imgH, colW, rowH, DefColEmu, DefRowEmu);
                curRow += EvdRowSpan(curRow, imgH, rowH, DefRowEmu) + Gap;
            }
            return curRow;
        }

        bool sheetModified = false;
        var markers = EvdReadZoneMarkers(sheetDoc, ssNs, sharedStrings);
        bool hasZones = markers.ContainsKey("input") && markers.ContainsKey("step") && markers.ContainsKey("output");

        if (hasZones)
        {
            int origInput  = markers["input"];
            int origStep   = markers["step"];
            int origOutput = markers["output"];

            // Classify anchors into zones by their current row position (before any layout)
            var zone1 = anchors.Where(a => { int r = EvdFromRow(a, xdr); return r > origInput  && r < origStep;  }).ToList();
            var zone2 = anchors.Where(a => { int r = EvdFromRow(a, xdr); return r >= origStep   && r < origOutput; }).ToList();
            var zone3 = anchors.Where(a => EvdFromRow(a, xdr) >= origOutput).ToList();

            int adjStep   = origStep;
            int adjOutput = origOutput;

            // Zone 1 — Input
            if (zone1.Count > 0)
            {
                var z1g1 = zone1.Where(a => EvdFromCol(a, xdr) <= 8).ToList();
                var z1g2 = zone1.Where(a => EvdFromCol(a, xdr) >  8).ToList();
                int endG1 = LayoutGroup(z1g1, 1,  origInput + 2);
                int endG2 = LayoutGroup(z1g2, SecondImageCol, origInput + 2);
                int curRow1 = Math.Max(endG1, endG2);

                int excess = adjStep - curRow1;
                if (excess > 0)
                {
                    EvdDeleteSheetRows(sheetDoc, ssNs, curRow1, adjStep - 1);
                    adjStep   -= excess;
                    adjOutput -= excess;
                    sheetModified = true;
                }
            }

            // Zone 2 — Step
            if (zone2.Count > 0)
            {
                var z2g1 = zone2.Where(a => EvdFromCol(a, xdr) <= 8).ToList();
                var z2g2 = zone2.Where(a => EvdFromCol(a, xdr) >  8).ToList();
                int endG1 = LayoutGroup(z2g1, 1,  adjStep + 2);
                int endG2 = LayoutGroup(z2g2, SecondImageCol, adjStep + 2);
                int curRow2 = Math.Max(endG1, endG2);

                int excess = adjOutput - curRow2;
                if (excess > 0)
                {
                    EvdDeleteSheetRows(sheetDoc, ssNs, curRow2, adjOutput - 1);
                    adjOutput -= excess;
                    sheetModified = true;
                }
            }

            // Zone 3 — Output (no deletion after last zone)
            if (zone3.Count > 0)
            {
                var z3g1 = zone3.Where(a => EvdFromCol(a, xdr) <= 8).ToList();
                var z3g2 = zone3.Where(a => EvdFromCol(a, xdr) >  8).ToList();
                LayoutGroup(z3g1, 1,  adjOutput + 2);
                LayoutGroup(z3g2, SecondImageCol, adjOutput + 2);
            }
        }
        else
        {
            // Fallback: original single-group behaviour (no zone markers found)
            var g1 = anchors.Where(a => EvdFromCol(a, xdr) <= 8).OrderBy(a => EvdFromRow(a, xdr)).ToList();
            var g2 = anchors.Where(a => EvdFromCol(a, xdr) >  8).OrderBy(a => EvdFromRow(a, xdr)).ToList();

            if (g1.Count > 0) LayoutGroup(g1, 1,  EvdFromRow(g1[0], xdr));
            if (g2.Count > 0) LayoutGroup(g2, SecondImageCol, EvdFromRow(g2[0], xdr));
        }

        return (true, sheetModified);
    }

    // Đọc kích thước gốc của ảnh từ file ảnh bên trong ZIP (EMU theo DPI thực)
    private static (long cx, long cy) EvdNaturalSizeEmu(ZipArchive zip, string imgPath)
    {
        try
        {
            var entry = zip.GetEntry(imgPath);
            if (entry == null) return (0, 0);

            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            ms.Position = 0;

            using var img = System.Drawing.Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            float dpiX = img.HorizontalResolution > 0 ? img.HorizontalResolution : 96f;
            float dpiY = img.VerticalResolution   > 0 ? img.VerticalResolution   : 96f;
            long cx = (long)(img.Width  * 914400L / dpiX);
            long cy = (long)(img.Height * 914400L / dpiY);
            return (cx, cy);
        }
        catch { return (0, 0); }
    }

    // Fallback: lấy kích thước hiển thị hiện tại từ <a:ext> bên trong <xdr:spPr>
    private static (long cx, long cy) EvdCurrentSizeEmu(XElement anchor, XNamespace xdr, XNamespace aNs)
    {
        var ext = anchor.Descendants(aNs + "ext").FirstOrDefault();
        if (ext == null) return (0, 0);
        long cx = long.TryParse(ext.Attribute("cx")?.Value, out var c)  ? c  : 0;
        long cy = long.TryParse(ext.Attribute("cy")?.Value, out var c2) ? c2 : 0;
        return (cx, cy);
    }

    private static int EvdFromCol(XElement anchor, XNamespace xdr)
        => int.TryParse(anchor.Element(xdr + "from")?.Element(xdr + "col")?.Value, out var v) ? v : 0;

    private static int EvdFromRow(XElement anchor, XNamespace xdr)
        => int.TryParse(anchor.Element(xdr + "from")?.Element(xdr + "row")?.Value, out var v) ? v : 0;

    private static void EvdSetAnchor(XElement anchor, XNamespace xdr,
        int fCol, long fColOff, int fRow, long fRowOff,
        long imgW, long imgH,
        Dictionary<int, long> colW, Dictionary<int, long> rowH,
        long defColEmu, long defRowEmu)
    {
        // Set <xdr:from>
        var fromEl = anchor.Element(xdr + "from")!;
        fromEl.Element(xdr + "col")!.Value    = fCol.ToString();
        fromEl.Element(xdr + "colOff")!.Value = fColOff.ToString();
        fromEl.Element(xdr + "row")!.Value    = fRow.ToString();
        fromEl.Element(xdr + "rowOff")!.Value = fRowOff.ToString();

        if (anchor.Name.LocalName == "twoCellAnchor")
        {
            var (tCol, tColOff) = EvdEndCell(fCol, fColOff, imgW, colW, defColEmu);
            var (tRow, tRowOff) = EvdEndCell(fRow, fRowOff, imgH, rowH, defRowEmu);
            var toEl = anchor.Element(xdr + "to")!;
            toEl.Element(xdr + "col")!.Value    = tCol.ToString();
            toEl.Element(xdr + "colOff")!.Value = tColOff.ToString();
            toEl.Element(xdr + "row")!.Value    = tRow.ToString();
            toEl.Element(xdr + "rowOff")!.Value = tRowOff.ToString();
        }
        else // oneCellAnchor: dùng <xdr:ext>
        {
            var ext = anchor.Element(xdr + "ext")!;
            ext.SetAttributeValue("cx", imgW);
            ext.SetAttributeValue("cy", imgH);
        }
    }

    // Tính (endIdx, endOffset) từ startIdx + startOffset + sizeEMU, dựa vào bảng kích thước thực
    private static (int idx, long off) EvdEndCell(int startIdx, long startOff, long sizeEmu,
        Dictionary<int, long> sizes, long defSize)
    {
        long rem = sizeEmu;
        int idx = startIdx;
        long firstAvail = (sizes.TryGetValue(idx, out var s0) ? s0 : defSize) - startOff;

        if (rem <= firstAvail) return (idx, startOff + rem);
        rem -= firstAvail;
        idx++;

        while (rem > 0)
        {
            long s = sizes.TryGetValue(idx, out var sv) ? sv : defSize;
            if (rem <= s) return (idx, rem);
            rem -= s;
            idx++;
        }
        return (idx, 0);
    }

    // Tính số row mà ảnh chiếm (để tính vị trí ảnh tiếp theo)
    private static int EvdRowSpan(int fromRow, long heightEmu,
        Dictionary<int, long> rowH, long defRowEmu)
    {
        long rem = heightEmu;
        int row = fromRow;
        while (rem > 0)
        {
            long rh = rowH.TryGetValue(row, out var rv) ? rv : defRowEmu;
            rem -= rh;
            row++;
        }
        return row - fromRow;
    }

    // Đọc col widths từ sheet XML (chuyển từ character-units → EMU)
    private static Dictionary<int, long> EvdColWidths(XDocument sheetDoc, XNamespace ssNs)
    {
        var dict = new Dictionary<int, long>();
        const double Mdw = 7.0;          // Max digit width Calibri 11pt ≈ 7 px
        const long PxToEmu = 9525L;

        foreach (var col in sheetDoc.Descendants(ssNs + "col"))
        {
            if (!double.TryParse(col.Attribute("width")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out double width)) continue;
            int min = int.TryParse(col.Attribute("min")?.Value, out var mn) ? mn - 1 : -1;
            int max = int.TryParse(col.Attribute("max")?.Value, out var mx) ? mx - 1 : -1;
            if (min < 0 || max < 0) continue;

            long emu = (long)(width * Mdw * PxToEmu);
            for (int i = min; i <= max; i++) dict[i] = emu;
        }
        return dict;
    }

    // Đọc row heights từ sheet XML (chuyển từ points → EMU)
    private static Dictionary<int, long> EvdRowHeights(XDocument sheetDoc, XNamespace ssNs)
    {
        var dict = new Dictionary<int, long>();
        const long PtToEmu = 12700L;

        foreach (var row in sheetDoc.Descendants(ssNs + "row"))
        {
            if (!double.TryParse(row.Attribute("ht")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out double ht)) continue;
            if (!int.TryParse(row.Attribute("r")?.Value, out int r)) continue;
            dict[r - 1] = (long)(ht * PtToEmu); // 0-based
        }
        return dict;
    }

    // Resolve relative path: "xl/worksheets/sheet1.xml" + "../drawings/drawing1.xml" → "xl/drawings/drawing1.xml"
    private static string ResolvePath(string basePath, string relPath)
    {
        var parts = new List<string>(basePath.Split('/'));
        parts.RemoveAt(parts.Count - 1);
        foreach (var seg in relPath.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join("/", parts);
    }

    private static string[] EvdLoadSharedStrings(ZipArchive zip, XNamespace ssNs)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return Array.Empty<string>();
        try
        {
            XDocument doc; using (var s = entry.Open()) doc = XDocument.Load(s);
            return doc.Descendants(ssNs + "si")
                .Select(si => si.Element(ssNs + "t")?.Value
                           ?? string.Concat(si.Descendants(ssNs + "t").Select(t => t.Value)))
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // Find 0-based row indices for "Input", "Step", "Output" markers in column A
    private static Dictionary<string, int> EvdReadZoneMarkers(
        XDocument sheetDoc, XNamespace ssNs, string[] sharedStrings)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rowEl in sheetDoc.Descendants(ssNs + "row"))
        {
            if (!int.TryParse(rowEl.Attribute("r")?.Value, out int r)) continue;
            var cellA = rowEl.Elements(ssNs + "c")
                .FirstOrDefault(c => c.Attribute("r")?.Value?
                    .StartsWith("A", StringComparison.OrdinalIgnoreCase) == true);
            if (cellA == null) continue;

            string? val = null;
            string? t = cellA.Attribute("t")?.Value;
            if (t == "s")
            {
                if (int.TryParse(cellA.Element(ssNs + "v")?.Value, out int idx) && idx < sharedStrings.Length)
                    val = sharedStrings[idx];
            }
            else if (t == "inlineStr")
            {
                val = string.Concat(cellA.Descendants(ssNs + "t").Select(x => x.Value));
            }
            else
            {
                val = cellA.Element(ssNs + "v")?.Value;
            }

            if (val == null) continue;
            string trimmed = val.Trim();
            if (trimmed.Equals("Input",  StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Step",   StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Output", StringComparison.OrdinalIgnoreCase))
            {
                result[trimmed.ToLower()] = r - 1; // convert to 0-based
            }
        }
        return result;
    }

    // Delete 0-based rows [fromRow0, toRow0] from sheet XML and renumber subsequent rows
    private static void EvdDeleteSheetRows(XDocument sheetDoc, XNamespace ssNs, int fromRow0, int toRow0)
    {
        if (toRow0 < fromRow0) return;
        int count = toRow0 - fromRow0 + 1;
        int from1 = fromRow0 + 1, to1 = toRow0 + 1;

        sheetDoc.Descendants(ssNs + "row")
            .Where(r => int.TryParse(r.Attribute("r")?.Value, out int rn) && rn >= from1 && rn <= to1)
            .ToList()
            .ForEach(r => r.Remove());

        foreach (var rowEl in sheetDoc.Descendants(ssNs + "row"))
        {
            if (!int.TryParse(rowEl.Attribute("r")?.Value, out int rn) || rn <= to1) continue;
            int newRn = rn - count;
            rowEl.SetAttributeValue("r", newRn);
            foreach (var cell in rowEl.Elements(ssNs + "c"))
            {
                string? cref = cell.Attribute("r")?.Value;
                if (cref == null) continue;
                int i = 0;
                while (i < cref.Length && char.IsLetter(cref[i])) i++;
                if (i < cref.Length)
                    cell.SetAttributeValue("r", cref[..i] + newRn);
            }
        }
    }

    private void FormatExcelFiles(string root, double fontSize, bool applyFontSize, string fontName, int zoom)
    {
        var files = Directory.GetFiles(root, "*.xls?", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Log(rtbLog2, $"Tìm thấy {files.Length} file Excel (.xlsx, .xlsm).");

        int success = 0, failed = 0;

        foreach (var file in files)
        {
            Log(rtbLog2, $"Xử lý: {Path.GetRelativePath(root, file)}");
            try
            {
                using var wb = new XLWorkbook(file);
                foreach (var ws in wb.Worksheets)
                {
                    ws.SheetView.ZoomScale = zoom;

                    var range = ws.RangeUsed();
                    if (range is null) continue;

                    if (!string.IsNullOrEmpty(fontName))
                        range.Style.Font.FontName = fontName;
                    if (applyFontSize)
                        range.Style.Font.FontSize = fontSize;
                }
                wb.Save();
                ResetExcelView(file);
                Log(rtbLog2, $"  ✓ Xong");
                success++;
            }
            catch (Exception ex)
            {
                Log(rtbLog2, $"  ✗ {ex.Message}");
                failed++;
            }
        }

        Log(rtbLog2, $"Hoàn thành! Thành công: {success} | Lỗi: {failed}", ClrInfo);
        Invoke(() => MessageBox.Show(
            $"Format Excel hoàn thành!\n\nThành công: {success}\nLỗi: {failed}",
            "Kết quả", MessageBoxButtons.OK, MessageBoxIcon.Information));
    }

    // ─────────────────────────────────────────────────────────
    //  Post-process OOXML: cursor A1, scroll top-left, first sheet active
    // ─────────────────────────────────────────────────────────
    private static void ResetExcelView(string filePath, int? zoomScale = null)
    {
        var ssNs  = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var rNs   = XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        var pkgNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");

        using var zip = new ZipArchive(
            File.Open(filePath, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update);

        // 1. workbook.xml — activeTab=0
        var wbEntry = zip.GetEntry("xl/workbook.xml");
        if (wbEntry == null) return;

        XDocument wbDoc;
        using (var s = wbEntry.Open()) wbDoc = XDocument.Load(s);

        foreach (var bv in wbDoc.Descendants(ssNs + "bookView"))
        {
            bv.SetAttributeValue("activeTab", 0);
            bv.Attribute("firstSheet")?.Remove();
        }

        var sheetRIds = wbDoc.Descendants(ssNs + "sheet")
            .Select(e => e.Attribute(rNs + "id")?.Value ?? "").ToList();

        using (var s = wbEntry.Open()) { s.SetLength(0); wbDoc.Save(s); }

        // 2. Relationships — rId → file path
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        var rIdToPath = new Dictionary<string, string>();
        if (relsEntry != null)
        {
            XDocument relsDoc;
            using (var s = relsEntry.Open()) relsDoc = XDocument.Load(s);
            foreach (var rel in relsDoc.Descendants(pkgNs + "Relationship"))
            {
                string? id     = rel.Attribute("Id")?.Value;
                string? target = rel.Attribute("Target")?.Value;
                if (id == null || target == null) continue;
                rIdToPath[id] = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
            }
        }

        // 3. Each worksheet
        for (int i = 0; i < sheetRIds.Count; i++)
        {
            if (!rIdToPath.TryGetValue(sheetRIds[i], out var path)) continue;
            var wsEntry = zip.GetEntry(path);
            if (wsEntry == null) continue;

            XDocument wsDoc;
            using (var s = wsEntry.Open()) wsDoc = XDocument.Load(s);

            bool isFirst = i == 0;

            var sheetViews = wsDoc.Root?.Element(ssNs + "sheetViews");
            if (sheetViews == null)
            {
                sheetViews = new XElement(ssNs + "sheetViews");
                var sheetData = wsDoc.Root?.Element(ssNs + "sheetData");
                if (sheetData != null) sheetData.AddBeforeSelf(sheetViews);
                else wsDoc.Root?.AddFirst(sheetViews);
            }

            if (!sheetViews.Elements(ssNs + "sheetView").Any())
            {
                sheetViews.Add(new XElement(ssNs + "sheetView", new XAttribute("workbookViewId", "0")));
            }

            foreach (var sheetView in sheetViews.Elements(ssNs + "sheetView"))
            {
                if (isFirst) sheetView.SetAttributeValue("tabSelected", 1);
                else         sheetView.Attribute("tabSelected")?.Remove();

                if (zoomScale.HasValue)
                {
                    sheetView.SetAttributeValue("zoomScale", zoomScale.Value);
                    sheetView.SetAttributeValue("zoomScaleNormal", zoomScale.Value);
                    sheetView.SetAttributeValue("zoomScalePageLayoutView", zoomScale.Value);
                    sheetView.SetAttributeValue("zoomScaleSheetLayoutView", zoomScale.Value);
                }

                sheetView.Attribute("topLeftCell")?.Remove(); // scroll về A1

                sheetView.Elements(ssNs + "selection").Remove();
                var sel = new XElement(ssNs + "selection",
                    new XAttribute("activeCell", "A1"),
                    new XAttribute("sqref", "A1"));

                var pane = sheetView.Element(ssNs + "pane");
                if (pane != null) pane.AddAfterSelf(sel);
                else sheetView.AddFirst(sel);
            }

            using var ws = wsEntry.Open();
            ws.SetLength(0);
            wsDoc.Save(ws);
        }
    }
}

