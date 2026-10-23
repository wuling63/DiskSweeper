using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 對話框共用的外框：上方漸層標題列（圖示、標題、說明），中間白色圓角卡片放內容，
    /// 下方膠囊按鈕（確定用主色、取消淺色）。子類別把控制項加進 Body。
    /// </summary>
    public class DialogBase : Form
    {
        private const int HeaderHeight = 68;

        private readonly DialogHeader _header = new DialogHeader { Dock = DockStyle.Top, Height = HeaderHeight };
        public readonly Panel Body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 12), BackColor = Theme.Surface };
        protected readonly RoundButton BtnOk = new RoundButton { Text = Lang.T("確定"), Size = new Size(110, 42), DialogResult = DialogResult.OK, Margin = new Padding(6, 0, 0, 0) };
        protected readonly RoundButton BtnCancel = new RoundButton
        {
            Text = Lang.T("取消"), Size = new Size(110, 42), DialogResult = DialogResult.Cancel, Style = RoundButton.Kind.Light, Font = Theme.Ui,
            Margin = new Padding(6, 0, 0, 0)
        };

        /// <summary>標題列第二行的說明。</summary>
        public string Subtitle { get { return _header.Subtitle; } set { _header.Subtitle = value; } }

        /// <summary>標題列左邊圓圈裡的符號（Segoe MDL2 Assets 字型的字元）。</summary>
        public string Glyph { get { return _header.Glyph; } set { _header.Glyph = value; } }

        public DialogBase(string title, Size size, bool cancel = true)
        {
            Text = title;
            _header.Title = title;
            Font = Theme.Ui;
            Icon = AppIcon.Get();
            BackColor = Theme.Background;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(size.Width + 28, size.Height + HeaderHeight + 24);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(14, 10, 14, 0),
                BackColor = Theme.Background
            };
            if (cancel) bottom.Controls.Add(BtnCancel);
            bottom.Controls.Add(BtnOk);

            // 白卡片：外層留邊，內層是 Body。
            var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(2) };
            card.Controls.Add(Body);
            var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 0), BackColor = Theme.Background };
            holder.Controls.Add(card);

            Controls.Add(holder);
            Controls.Add(bottom);
            Controls.Add(_header);
            AcceptButton = BtnOk;
            CancelButton = cancel ? (IButtonControl)BtnCancel : BtnOk;
            Load += delegate { Theme.Apply(this); };
        }

        /// <summary>側邊的操作按鈕（加入、移除…）：淺色膠囊。</summary>
        protected static RoundButton SideButton(string text)
        {
            return new RoundButton
            {
                Text = text, Size = new Size(124, 36), Style = RoundButton.Kind.Light, Font = Theme.Ui,
                Margin = new Padding(0, 0, 0, 6)
            };
        }
    }

    /// <summary>對話框上方的漸層標題列。</summary>
    public class DialogHeader : Control
    {
        private string _subtitle, _glyph = "\uE713";

        public string Title { get; set; }
        public string Subtitle { get { return _subtitle; } set { _subtitle = value; Invalidate(); } }
        public string Glyph { get { return _glyph; } set { _glyph = value; Invalidate(); } }

        public DialogHeader()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = ClientRectangle;
            if (rect.Width <= 0) return;

            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Theme.Accent, Color.FromArgb(14, 94, 112), 15f))
            {
                g.FillRectangle(brush, rect);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillEllipse(b, Width - 170, -70, 200, 200);

            var circle = new Rectangle(18, (Height - 40) / 2, 40, 40);
            using (var b = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillEllipse(b, circle);
            using (var f = new Font("Segoe MDL2 Assets", 14f))
            {
                TextRenderer.DrawText(g, _glyph, f, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            bool two = !string.IsNullOrEmpty(_subtitle);
            TextRenderer.DrawText(g, Title, Theme.Title, new Point(70, two ? 13 : (Height - Theme.Title.Height) / 2), Color.White);
            if (two)
            {
                TextRenderer.DrawText(g, _subtitle, Theme.Small, new Rectangle(71, 38, Width - 90, 20), Color.FromArgb(225, 255, 255, 255),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>忽略清單：掃描時跳過的檔案與資料夾。</summary>
    public class IgnoreListDialog : DialogBase
    {
        private readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };

        public List<string> Result { get { return _list.Items.Cast<string>().ToList(); } }

        public IgnoreListDialog(IEnumerable<string> items) : base(Lang.T("忽略清單"), new Size(640, 400))
        {
            Glyph = "\uE8F8";
            Subtitle = Lang.T("這些檔案與資料夾，掃描時會跳過");
            foreach (string s in items) _list.Items.Add(s);

            var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 132, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8, 0, 0, 0) };
            var addFile = SideButton(Lang.T("加入檔案…"));
            var addFolder = SideButton(Lang.T("加入資料夾…"));
            var remove = SideButton(Lang.T("移除"));
            side.Controls.AddRange(new Control[] { addFile, addFolder, remove });

            var hint = new Label
            {
                Dock = DockStyle.Top, Height = 26,
                Text = Lang.T("清單裡的檔案，以及資料夾底下的所有檔案，掃描時都會跳過。"),
                ForeColor = Color.FromArgb(90, 90, 100)
            };

            Body.Controls.Add(_list);
            Body.Controls.Add(side);
            Body.Controls.Add(hint);

            addFile.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Multiselect = true, Title = Lang.T("選擇要忽略的檔案") })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    foreach (string f in dlg.FileNames) AddUnique(f);
                }
            };
            addFolder.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = Lang.T("選擇要忽略的資料夾") })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) AddUnique(dlg.SelectedPath);
                }
            };
            remove.Click += delegate
            {
                foreach (var item in _list.SelectedItems.Cast<object>().ToList()) _list.Items.Remove(item);
            };
        }

        private void AddUnique(string path)
        {
            if (!_list.Items.Cast<string>().Any(s => s.Equals(path, StringComparison.OrdinalIgnoreCase))) _list.Items.Add(path);
        }
    }

    /// <summary>自訂要清理的資料夾（含檔名樣式與是否含子資料夾）。</summary>
    public class CustomFoldersDialog : DialogBase
    {
        private readonly ListView _list = new ThemedListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, CheckBoxes = true
        };

        public List<CustomFolder> Result
        {
            get
            {
                return _list.Items.Cast<ListViewItem>().Select(i => new CustomFolder
                {
                    Path = i.Text,
                    Pattern = i.SubItems[1].Text,
                    Recursive = i.Checked
                }).ToList();
            }
        }

        public CustomFoldersDialog(IEnumerable<CustomFolder> folders) : base(Lang.T("自訂義檔案和資料夾"), new Size(720, 420))
        {
            Glyph = "\uE8B7";
            Subtitle = Lang.T("加入自己要清理的資料夾與檔名樣式");
            _list.Columns.Add(Lang.T("資料夾（勾選＝含子資料夾）"), 440);
            _list.Columns.Add(Lang.T("檔名樣式"), 160);
            foreach (var f in folders) Add(f.Path, f.Pattern, f.Recursive);

            var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 132, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8, 0, 0, 0) };
            var add = SideButton(Lang.T("加入資料夾…"));
            var pattern = SideButton(Lang.T("修改樣式…"));
            var remove = SideButton(Lang.T("移除"));
            side.Controls.AddRange(new Control[] { add, pattern, remove });

            var hint = new Label
            {
                Dock = DockStyle.Top, Height = 44,
                ForeColor = Color.FromArgb(90, 90, 100),
                Text = Lang.T("掃描時會列出這些資料夾中符合樣式的檔案（例如 *.tmp；多個用分號分隔）。") + Environment.NewLine
                     + Lang.T("磁碟根目錄、Windows、Program Files、使用者資料夾本身不能加入。")
            };

            Body.Controls.Add(_list);
            Body.Controls.Add(side);
            Body.Controls.Add(hint);

            add.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = Lang.T("選擇要清理的資料夾") })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (Cleaner.IsProtected(dlg.SelectedPath))
                    {
                        Msg.Show(this, Lang.T("這個位置是系統或使用者的重要資料夾，不能整個加入清理。"), Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Add(dlg.SelectedPath, "*", true);
                }
            };
            pattern.Click += delegate
            {
                if (_list.SelectedItems.Count == 0) return;
                var item = _list.SelectedItems[0];
                string text = Prompt.Show(this, Lang.T("檔名樣式"), Lang.T("例如 *.tmp 或 *.log;*.bak"), item.SubItems[1].Text);
                if (text != null) item.SubItems[1].Text = string.IsNullOrWhiteSpace(text) ? "*" : text.Trim();
            };
            remove.Click += delegate
            {
                foreach (ListViewItem item in _list.SelectedItems.Cast<ListViewItem>().ToList()) _list.Items.Remove(item);
            };
        }

        private void Add(string path, string pattern, bool recursive)
        {
            var item = new ListViewItem(path) { Checked = recursive };
            item.SubItems.Add(string.IsNullOrWhiteSpace(pattern) ? "*" : pattern);
            _list.Items.Add(item);
        }
    }

    /// <summary>清理歷史記錄。</summary>
    public class HistoryDialog : DialogBase
    {
        public bool Cleared { get; private set; }

        public HistoryDialog(List<HistoryEntry> history) : base(Lang.T("清理歷史記錄"), new Size(560, 420), false)
        {
            Glyph = "\uE81C";
            Subtitle = Lang.T("每次清理的時間、檔案數與釋放的空間");
            var list = new ThemedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            list.Columns.Add(Lang.T("時間"), 170);
            list.Columns.Add(Lang.T("刪除檔案"), 100, HorizontalAlignment.Right);
            list.Columns.Add(Lang.T("釋放空間"), 110, HorizontalAlignment.Right);
            list.Columns.Add(Lang.T("無法刪除"), 90, HorizontalAlignment.Right);

            foreach (var h in history.OrderByDescending(h => h.Time))
            {
                var item = new ListViewItem(h.Time.ToString("yyyy/MM/dd HH:mm:ss"));
                item.SubItems.Add(h.Files.ToString("N0"));
                item.SubItems.Add(Format.Size(h.Bytes));
                item.SubItems.Add(h.Failed.ToString("N0"));
                list.Items.Add(item);
            }

            var summary = new Label
            {
                Dock = DockStyle.Top, Height = 26,
                ForeColor = Color.FromArgb(90, 90, 100),
                Text = string.Format(Lang.T("共 {0} 次，累計釋放 {1}"), history.Count, Format.Size(history.Sum(h => h.Bytes)))
            };

            var clear = new RoundButton { Text = Lang.T("清除記錄"), Size = new Size(110, 38), Style = RoundButton.Kind.Light, Font = Theme.Ui, Dock = DockStyle.Bottom };
            clear.Click += delegate
            {
                if (Msg.Show(this, Lang.T("確定要清除所有清理記錄？"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                history.Clear();
                list.Items.Clear();
                summary.Text = Lang.T("共 0 次");
                Cleared = true;
            };

            Body.Controls.Add(list);
            Body.Controls.Add(summary);
            Body.Controls.Add(clear);
        }
    }

    /// <summary>選項：暫存保留時間、回收筒、開發專案資料夾、排程。</summary>
    public class OptionsDialog : DialogBase
    {
        private readonly NumericUpDown _hours = new NumericUpDown { Minimum = 0, Maximum = 720, Width = 70 };
        private readonly CheckBox _recycle = new CheckBox { AutoSize = true, Text = Lang.T("清理時送進資源回收筒（刪錯了救得回來，但空間要等清空回收筒才會釋放）") };
        private readonly ListBox _roots = new ListBox { IntegralHeight = false, Height = 96, Width = 470 };
        private readonly CheckBox _auto = new CheckBox { AutoSize = true, Text = Lang.T("每週自動清理：") };
        private readonly ComboBox _day = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        private readonly DateTimePicker _time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
        private readonly CheckBox _low = new CheckBox { AutoSize = true, Text = Lang.T("每天 09:00 檢查系統磁碟，剩不到 10% 時通知") };
        private readonly ComboBox _appearance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly ComboBox _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly CheckBox _minTray = new CheckBox { AutoSize = true, Text = Lang.T("最小化時縮到系統匣") };
        private readonly CheckBox _closeTray = new CheckBox { AutoSize = true, Text = Lang.T("關閉視窗時縮到系統匣（從系統匣選單「結束」才真的關閉）") };
        private readonly CheckBox _backup = new CheckBox { AutoSize = true, Text = Lang.T("清理前把要刪的檔案備份成 zip（超過 200 MB 的檔案不備份）") };
        private readonly NumericUpDown _backupDays = new NumericUpDown { Minimum = 1, Maximum = 90, Width = 60 };

        private static readonly string[] AppearanceCodes = { "system", "light", "dark" };
        private static readonly string[] LanguageCodes = { "zh", "en" };

        public int MinAgeHours { get { return (int)_hours.Value; } }
        public bool UseRecycleBin { get { return _recycle.Checked; } }
        public List<string> DevRoots { get { return _roots.Items.Cast<string>().ToList(); } }
        public bool AutoClean { get { return _auto.Checked; } }
        public DayOfWeek AutoCleanDay { get { return (DayOfWeek)_day.SelectedIndex; } }
        public string AutoCleanTime { get { return _time.Value.ToString("HH:mm"); } }
        public bool LowSpaceCheck { get { return _low.Checked; } }
        public string Appearance { get { return AppearanceCodes[Math.Max(0, _appearance.SelectedIndex)]; } }
        public string Language { get { return LanguageCodes[Math.Max(0, _language.SelectedIndex)]; } }
        public bool MinimizeToTray { get { return _minTray.Checked; } }
        public bool CloseToTray { get { return _closeTray.Checked; } }
        public bool BackupBeforeClean { get { return _backup.Checked; } }
        public int BackupDays { get { return (int)_backupDays.Value; } }

        public OptionsDialog(Settings settings) : base(Lang.T("選項"), new Size(640, 560))
        {
            Glyph = "\uE713";
            Subtitle = Lang.T("清理方式、開發專案資料夾與排程");
            _hours.Value = Math.Max(0, Math.Min(720, settings.MinAgeHours));
            _recycle.Checked = settings.UseRecycleBin;
            foreach (string r in settings.DevRoots) _roots.Items.Add(r);
            _day.Items.AddRange(new object[] { Lang.T("星期日"), Lang.T("星期一"), Lang.T("星期二"), Lang.T("星期三"), Lang.T("星期四"), Lang.T("星期五"), Lang.T("星期六") });
            _day.SelectedIndex = (int)settings.AutoCleanDay;
            DateTime t;
            _time.Value = DateTime.Today.Add(DateTime.TryParse(settings.AutoCleanTime, out t) ? t.TimeOfDay : new TimeSpan(21, 0, 0));
            _auto.Checked = settings.AutoClean;
            _low.Checked = settings.LowSpaceCheck;
            _appearance.Items.AddRange(new object[] { Lang.T("跟隨系統"), Lang.T("淺色"), Lang.T("深色") });
            _appearance.SelectedIndex = Math.Max(0, Array.IndexOf(AppearanceCodes, settings.Appearance));
            _language.Items.AddRange(new object[] { Lang.T("中文（繁體）"), "English" });
            _language.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, settings.Language));
            _minTray.Checked = settings.MinimizeToTray;
            _closeTray.Checked = settings.CloseToTray;
            _backup.Checked = settings.BackupBeforeClean;
            _backupDays.Value = Math.Max(1, Math.Min(90, settings.BackupDays));

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            // 長的勾選框（英文介面特別長）限制寬度讓它換行，不要被切掉。
            // CheckBox 不會自己換行：關掉 AutoSize、固定寬度，高度照文字量算。
            foreach (var box in new[] { _recycle, _backup, _closeTray, _low })
            {
                var size = TextRenderer.MeasureText(box.Text, Theme.Ui, new Size(545, 0), TextFormatFlags.WordBreak);
                box.AutoSize = false;
                box.Size = new Size(575, Math.Max(24, size.Height + 6));
            }

            flow.Controls.Add(Title(Lang.T("清理")));
            flow.Controls.Add(Row(new Label { Text = Lang.T("暫存類只清超過"), AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, _hours,
                new Label { Text = Lang.T("小時沒變動的檔案（0＝不限）"), AutoSize = true, Margin = new Padding(3, 6, 3, 0) }));
            flow.Controls.Add(Hint(Lang.T("套用在系統暫存、使用者暫存、錯誤報告、系統日誌；執行中的程式常把檔案放在暫存資料夾，留一段時間比較安全。")));
            flow.Controls.Add(_recycle);
            flow.Controls.Add(Row(_backup));
            flow.Controls.Add(Row(new Label { Text = Lang.T("備份保留"), AutoSize = true, Margin = new Padding(20, 6, 3, 0) }, _backupDays,
                new Label { Text = Lang.T("天（送進資源回收筒時不另外備份）"), AutoSize = true, Margin = new Padding(3, 6, 3, 0) }));

            flow.Controls.Add(Title(Lang.T("外觀與語言")));
            flow.Controls.Add(Row(new Label { Text = Lang.T("外觀"), AutoSize = true, Margin = new Padding(3, 6, 8, 0) }, _appearance,
                new Label { Text = Lang.T("語言"), AutoSize = true, Margin = new Padding(24, 6, 8, 0) }, _language));
            flow.Controls.Add(Hint(Lang.T("外觀立即套用；語言重新啟動程式後生效。")));

            flow.Controls.Add(Title(Lang.T("系統匣")));
            flow.Controls.Add(_minTray);
            flow.Controls.Add(_closeTray);
            flow.Controls.Add(Hint(Lang.T("縮到系統匣後，右鍵圖示可以「快速清理」（範圍跟每週自動清理一樣），雙擊圖示開回視窗。")));

            flow.Controls.Add(Title(Lang.T("開發專案資料夾")));
            flow.Controls.Add(Hint(Lang.T("「Python __pycache__」「.mypy_cache…」只在這些資料夾底下找（會跳過 .git、node_modules）。")));
            var add = new Button { Text = Lang.T("加入…"), Width = 80, Height = 28 };
            var remove = new Button { Text = Lang.T("移除"), Width = 80, Height = 28 };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            buttons.Controls.Add(add);
            buttons.Controls.Add(remove);
            flow.Controls.Add(Row(_roots, buttons));

            flow.Controls.Add(Title(Lang.T("排程（Windows 工作排程器）")));
            flow.Controls.Add(Row(_auto, _day, _time));
            flow.Controls.Add(Hint(Lang.T("用左邊勾選的項目清理，但不清空資源回收筒、不碰隱私類、不跑 DISM 與 Windows.old；以一般權限執行，結果記在歷史記錄。")));
            flow.Controls.Add(_low);

            Body.Controls.Add(flow);

            add.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = Lang.T("選擇開發專案所在的資料夾（例如 D:\\Projects）") })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (!_roots.Items.Cast<string>().Any(r => r.Equals(dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _roots.Items.Add(dlg.SelectedPath);
                    }
                }
            };
            remove.Click += delegate { if (_roots.SelectedItem != null) _roots.Items.Remove(_roots.SelectedItem); };
        }

        private static Label Title(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
        }

        private static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = false, Width = 570, Height = 36, ForeColor = Color.FromArgb(90, 90, 100) };
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.AddRange(controls);
            return row;
        }
    }

    /// <summary>單行輸入。</summary>
    public static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string hint, string value)
        {
            using (var form = new DialogBase(title, new Size(420, 130)))
            {
                var box = new TextBox { Dock = DockStyle.Top, Text = value };
                var label = new Label { Dock = DockStyle.Top, Height = 24, Text = hint };
                form.Glyph = "\uE70F";
                form.Body.Controls.Add(box);
                form.Body.Controls.Add(label);
                return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
