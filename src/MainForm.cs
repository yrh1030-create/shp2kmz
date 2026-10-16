using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ShpToKmz
{
    public class MainForm : Form
    {
        const string AutoName = "（自动识别）";

        ListBox lstFiles;
        Button btnAddFiles, btnAddFolder, btnRemove, btnClear;
        RadioButton rbSeparate, rbMerge;
        CheckBox chkSameDir;
        TextBox txtOutDir, txtMergeName;
        Button btnBrowseOut;
        ComboBox cboNameField, cboCrs, cboEncoding;
        NumericUpDown numCM, numOpacity, numWidth;
        Button btnLineColor, btnFillColor;
        CheckBox chkLabels, chkAttrs;
        Button btnConvert, btnOpenOut;
        ProgressBar progress;
        TextBox txtLog;
        Label lblCM;
        float scale = 1f;
        string lastOutDir;

        public MainForm(string[] initialFiles)
        {
            Text = "SHP 转 KMZ 工具 v" + AppInfo.Version + "  —  由 " + AppInfo.Author + " 开发";
            Font = new Font("Microsoft YaHei UI", 9F);
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size(S(780), S(760));
            MinimumSize = new Size(S(640), S(600));
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            if (initialFiles != null && initialFiles.Length > 0) AddPaths(initialFiles);
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        // ------------------------------------------------------------ 界面
        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(8)) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // 输入文件
            var gIn = new GroupBox { Text = "输入 SHP 文件（可直接把文件或文件夹拖到窗口里）", Dock = DockStyle.Fill };
            var tIn = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            tIn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tIn.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lstFiles = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false, HorizontalScrollbar = true };
            lstFiles.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            var flIn = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            btnAddFiles = Btn("添加文件...", (s, e) => AddFilesDialog());
            btnAddFolder = Btn("添加文件夹...", (s, e) => AddFolderDialog());
            btnRemove = Btn("移除选中", (s, e) => RemoveSelected());
            btnClear = Btn("清空列表", (s, e) => { lstFiles.Items.Clear(); RefreshFields(); });
            flIn.Controls.AddRange(new Control[] { btnAddFiles, btnAddFolder, btnRemove, btnClear });
            tIn.Controls.Add(lstFiles, 0, 0);
            tIn.Controls.Add(flIn, 1, 0);
            gIn.Controls.Add(tIn);
            root.Controls.Add(gIn, 0, 0);

            // 输出
            var gOut = new GroupBox { Text = "输出", Dock = DockStyle.Fill, AutoSize = true };
            var tOut = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            tOut.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tOut.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tOut.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var flMode = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
            rbSeparate = new RadioButton { Text = "每个 SHP 生成一个 KMZ", Checked = true, AutoSize = true };
            rbMerge = new RadioButton { Text = "全部合并为一个 KMZ（每个 SHP 一个图层文件夹）", AutoSize = true };
            rbSeparate.CheckedChanged += (s, e) => UpdateEnabled();
            flMode.Controls.Add(rbSeparate);
            flMode.Controls.Add(rbMerge);
            tOut.Controls.Add(flMode, 0, 0);
            tOut.SetColumnSpan(flMode, 3);
            chkSameDir = new CheckBox { Text = "保存到 SHP 所在文件夹", Checked = true, AutoSize = true };
            chkSameDir.CheckedChanged += (s, e) => UpdateEnabled();
            tOut.Controls.Add(chkSameDir, 0, 1);
            tOut.SetColumnSpan(chkSameDir, 3);
            tOut.Controls.Add(Lbl("输出文件夹："), 0, 2);
            txtOutDir = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right };
            tOut.Controls.Add(txtOutDir, 1, 2);
            btnBrowseOut = Btn("浏览...", (s, e) => BrowseOut());
            tOut.Controls.Add(btnBrowseOut, 2, 2);
            tOut.Controls.Add(Lbl("合并文件名："), 0, 3);
            txtMergeName = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Text = "合并" };
            tOut.Controls.Add(txtMergeName, 1, 3);
            tOut.Controls.Add(Lbl(".kmz"), 2, 3);
            gOut.Controls.Add(tOut);
            root.Controls.Add(gOut, 0, 1);

            // 选项
            var gOpt = new GroupBox { Text = "选项", Dock = DockStyle.Fill, AutoSize = true };
            var tOpt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
            tOpt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tOpt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tOpt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tOpt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            cboNameField = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            cboNameField.Items.Add(AutoName);
            cboNameField.SelectedIndex = 0;
            cboEncoding = Combo("自动检测", "GBK（中文 ArcGIS 常用）", "UTF-8");
            cboEncoding.SelectedIndexChanged += (s, e) => RefreshFields();
            cboCrs = Combo("自动（读取 .prj 文件）", "经纬度（WGS84 / CGCS2000）", "CGCS2000 高斯投影（手动指定中央经线）");
            cboCrs.SelectedIndexChanged += (s, e) => UpdateEnabled();
            numCM = new NumericUpDown { Minimum = 0, Maximum = 180, DecimalPlaces = 1, Increment = 3, Value = 108, Dock = DockStyle.Fill };
            btnLineColor = ColorBtn(Color.Yellow);
            btnFillColor = ColorBtn(Color.Yellow);
            numWidth = new NumericUpDown { Minimum = 0.5m, Maximum = 20, DecimalPlaces = 1, Increment = 0.5m, Value = 2, Dock = DockStyle.Fill };
            numOpacity = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 20, Dock = DockStyle.Fill };
            chkLabels = new CheckBox { Text = "在地图上显示名称标注", Checked = true, AutoSize = true };
            chkAttrs = new CheckBox { Text = "包含属性表（点击要素可查看）", Checked = true, AutoSize = true };

            lblCM = Lbl("中央经线(°)：");
            AddRow(tOpt, 0, "名称字段：", cboNameField, "属性编码：", cboEncoding);
            AddRow(tOpt, 1, "坐标系：", cboCrs, null, numCM);
            tOpt.Controls.Add(lblCM, 2, 1);
            AddRow(tOpt, 2, "边线颜色：", btnLineColor, "填充颜色：", btnFillColor);
            AddRow(tOpt, 3, "边线宽度：", numWidth, "填充不透明度(%)：", numOpacity);
            tOpt.Controls.Add(chkLabels, 1, 4);
            tOpt.Controls.Add(chkAttrs, 3, 4);
            gOpt.Controls.Add(tOpt);
            root.Controls.Add(gOpt, 0, 2);

            // 操作
            var tAct = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            tAct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tAct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tAct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnConvert = new Button { Text = "开始转换", AutoSize = true, MinimumSize = new Size(S(120), S(36)), Font = new Font(Font, FontStyle.Bold) };
            btnConvert.Click += (s, e) => StartConvert();
            btnOpenOut = Btn("打开输出文件夹", (s, e) => OpenOut());
            btnOpenOut.MinimumSize = new Size(S(120), S(36));
            btnOpenOut.Enabled = false;
            progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(S(8), S(10), S(3), S(10)) };
            tAct.Controls.Add(btnConvert, 0, 0);
            tAct.Controls.Add(btnOpenOut, 1, 0);
            tAct.Controls.Add(progress, 2, 0);
            root.Controls.Add(tAct, 0, 3);

            // 日志
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = SystemColors.Window };
            var gLog = new GroupBox { Text = "运行日志", Dock = DockStyle.Fill };
            gLog.Controls.Add(txtLog);
            root.Controls.Add(gLog, 0, 4);

            // 版权信息
            string about = "由 " + AppInfo.Author + " 开发　　版本 " + AppInfo.Version + "　　联系邮箱：";
            var lnkAbout = new LinkLabel { Text = about + AppInfo.Email, AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = SystemColors.GrayText };
            lnkAbout.LinkArea = new LinkArea(about.Length, AppInfo.Email.Length);
            lnkAbout.LinkClicked += (s, e) =>
            {
                try { Process.Start("mailto:" + AppInfo.Email); }
                catch { Clipboard.SetText(AppInfo.Email); MessageBox.Show(this, "邮箱已复制到剪贴板：" + AppInfo.Email, Text); }
            };
            root.Controls.Add(lnkAbout, 0, 5);

            AcceptButton = btnConvert;
            UpdateEnabled();
            Log("把 .shp 文件拖进来，或点“添加文件 / 添加文件夹”，然后点“开始转换”。");
            Log("坐标会自动从投影坐标转换为经纬度（WGS84），可直接在 Google 地球、奥维地图等软件中打开。");
        }

        Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(S(110), S(28)) };
            b.Click += click;
            return b;
        }

        Label Lbl(string text)
        {
            return new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }

        ComboBox Combo(params string[] items)
        {
            var c = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
            return c;
        }

        Button ColorBtn(Color c)
        {
            var b = new Button { BackColor = c, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, MinimumSize = new Size(S(60), S(26)) };
            b.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = b.BackColor, FullOpen = true })
                    if (dlg.ShowDialog(this) == DialogResult.OK) b.BackColor = dlg.Color;
            };
            return b;
        }

        void AddRow(TableLayoutPanel t, int row, string l1, Control c1, string l2, Control c2)
        {
            if (l1 != null) t.Controls.Add(Lbl(l1), 0, row);
            if (c1 != null) t.Controls.Add(c1, 1, row);
            if (l2 != null) t.Controls.Add(Lbl(l2), 2, row);
            if (c2 != null) t.Controls.Add(c2, 3, row);
        }

        void UpdateEnabled()
        {
            txtOutDir.Enabled = btnBrowseOut.Enabled = !chkSameDir.Checked || rbMerge.Checked;
            chkSameDir.Enabled = rbSeparate.Checked;
            txtMergeName.Enabled = rbMerge.Checked;
            numCM.Enabled = lblCM.Enabled = cboCrs.SelectedIndex == 2;
        }

        // ------------------------------------------------------------ 文件列表
        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) AddPaths(paths);
        }

        void AddFilesDialog()
        {
            using (var dlg = new OpenFileDialog { Filter = "Shapefile (*.shp)|*.shp", Multiselect = true, Title = "选择 SHP 文件" })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
        }

        void AddFolderDialog()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择文件夹（会包含所有子文件夹中的 SHP）" })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
        }

        void AddPaths(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string p in paths)
            {
                IEnumerable<string> files;
                if (Directory.Exists(p))
                {
                    try { files = Directory.GetFiles(p, "*.shp", SearchOption.AllDirectories); }
                    catch (Exception ex) { Log("无法读取文件夹 " + p + "：" + ex.Message); continue; }
                }
                else if (File.Exists(p) && p.EndsWith(".shp", StringComparison.OrdinalIgnoreCase)) files = new[] { p };
                else continue;
                foreach (string f in files)
                {
                    string full = Path.GetFullPath(f);
                    if (!lstFiles.Items.Contains(full)) { lstFiles.Items.Add(full); added++; }
                }
            }
            if (added == 0) Log("没有找到新的 .shp 文件。");
            else Log("已添加 " + added + " 个 SHP 文件。");
            if (txtOutDir.Text.Length == 0 && lstFiles.Items.Count > 0)
                txtOutDir.Text = Path.GetDirectoryName((string)lstFiles.Items[0]);
            RefreshFields();
        }

        void RemoveSelected()
        {
            var sel = lstFiles.SelectedItems.Cast<object>().ToList();
            foreach (object o in sel) lstFiles.Items.Remove(o);
            RefreshFields();
        }

        // 名称字段下拉框：取所有文件共有的字段
        void RefreshFields()
        {
            string current = cboNameField.Text;
            List<string> common = null;
            string mode = EncodingMode();
            foreach (object o in lstFiles.Items.Cast<object>().Take(50))
            {
                string dbf = Path.ChangeExtension((string)o, ".dbf");
                if (!File.Exists(dbf)) continue;
                try
                {
                    var names = DbfTable.Read(dbf, mode, true).Fields.Select(f => f.Name).ToList();
                    common = common == null ? names : common.Where(n => names.Contains(n)).ToList();
                }
                catch { }
            }
            cboNameField.BeginUpdate();
            cboNameField.Items.Clear();
            cboNameField.Items.Add(AutoName);
            if (common != null) foreach (string n in common) cboNameField.Items.Add(n);
            cboNameField.EndUpdate();
            if (current != AutoName && cboNameField.Items.Contains(current)) cboNameField.Text = current;
            else cboNameField.SelectedIndex = 0;
        }

        string EncodingMode()
        {
            switch (cboEncoding.SelectedIndex) { case 1: return "gbk"; case 2: return "utf8"; default: return "auto"; }
        }

        void BrowseOut()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择 KMZ 输出文件夹", SelectedPath = txtOutDir.Text })
                if (dlg.ShowDialog(this) == DialogResult.OK) txtOutDir.Text = dlg.SelectedPath;
        }

        void OpenOut()
        {
            if (lastOutDir != null && Directory.Exists(lastOutDir)) Process.Start("explorer.exe", "\"" + lastOutDir + "\"");
        }

        // ------------------------------------------------------------ 转换
        void Log(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), msg); return; }
            txtLog.AppendText(msg + Environment.NewLine);
        }

        void SetBusy(bool busy)
        {
            foreach (Control c in new Control[] { btnConvert, btnAddFiles, btnAddFolder, btnRemove, btnClear, lstFiles })
                c.Enabled = !busy;
            UseWaitCursor = busy;
        }

        void StartConvert()
        {
            var files = lstFiles.Items.Cast<string>().ToList();
            if (files.Count == 0) { MessageBox.Show(this, "请先添加 SHP 文件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            bool merge = rbMerge.Checked;
            bool sameDir = rbSeparate.Checked && chkSameDir.Checked;
            string outDir = txtOutDir.Text.Trim();
            if (!sameDir)
            {
                if (outDir.Length == 0) { MessageBox.Show(this, "请选择输出文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                try { Directory.CreateDirectory(outDir); }
                catch (Exception ex) { MessageBox.Show(this, "无法创建输出文件夹：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }
            string mergeName = txtMergeName.Text.Trim();
            if (merge && mergeName.Length == 0) mergeName = "合并";
            foreach (char ch in Path.GetInvalidFileNameChars()) mergeName = mergeName.Replace(ch, '_');

            var o = new ConvertOptions
            {
                NameField = cboNameField.Text == AutoName ? null : cboNameField.Text.Trim(),
                EncodingMode = EncodingMode(),
                CrsMode = cboCrs.SelectedIndex == 1 ? "geographic" : cboCrs.SelectedIndex == 2 ? "gk" : "auto",
                CentralMeridian = (double)numCM.Value,
                LineColor = btnLineColor.BackColor,
                FillColor = btnFillColor.BackColor,
                FillOpacity = (int)numOpacity.Value,
                LineWidth = (double)numWidth.Value,
                ShowLabels = chkLabels.Checked,
                IncludeAttributes = chkAttrs.Checked
            };

            txtLog.Clear();
            SetBusy(true);
            progress.Maximum = files.Count + (merge ? 1 : 0);
            progress.Value = 0;

            var th = new Thread(() => RunConvert(files, merge, sameDir, outDir, mergeName, o)) { IsBackground = true };
            th.Start();
        }

        void Step()
        {
            if (InvokeRequired) { BeginInvoke(new Action(Step)); return; }
            if (progress.Value < progress.Maximum) progress.Value++;
        }

        void RunConvert(List<string> files, bool merge, bool sameDir, string outDir, string mergeName, ConvertOptions o)
        {
            int ok = 0, fail = 0, features = 0;
            string firstOut = null;
            var sw = Stopwatch.StartNew();
            var layers = new List<Layer>();
            foreach (string f in files)
            {
                Log("▶ " + f);
                try
                {
                    Layer l = Converter.Load(f, o, Log);
                    if (merge) layers.Add(l);
                    else
                    {
                        string dir = sameDir ? Path.GetDirectoryName(f) : outDir;
                        string outPath = Path.Combine(dir, l.Name + ".kmz");
                        features += Converter.WriteKmz(new List<Layer> { l }, outPath, l.Name, o, Log);
                        Log("  ✔ 已保存：" + outPath);
                        if (firstOut == null) firstOut = dir;
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Log("  ✘ 失败：" + ex.Message);
                }
                Step();
            }
            if (merge && layers.Count > 0)
            {
                string outPath = Path.Combine(outDir, mergeName + ".kmz");
                Log("▶ 合并写出 " + layers.Count + " 个图层");
                try
                {
                    features += Converter.WriteKmz(layers, outPath, mergeName, o, Log);
                    Log("  ✔ 已保存：" + outPath);
                    firstOut = outDir;
                }
                catch (Exception ex) { fail++; ok = 0; Log("  ✘ 失败：" + ex.Message); }
                Step();
            }
            sw.Stop();
            Log("");
            Log(string.Format("完成：成功 {0} 个，失败 {1} 个，共 {2} 个要素，用时 {3:0.0} 秒。", ok, fail, features, sw.Elapsed.TotalSeconds));

            BeginInvoke(new Action(() =>
            {
                SetBusy(false);
                lastOutDir = firstOut;
                btnOpenOut.Enabled = firstOut != null;
                if (fail > 0)
                    MessageBox.Show(this, "有 " + fail + " 个文件转换失败，详情请看运行日志。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }));
        }
    }
}
