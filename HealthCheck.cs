using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    public enum HealthLevel { Ok, Info, Warn, Bad }

    public class HealthItem
    {
        public string Title;
        public string Detail;
        public HealthLevel Level;
        public string Action;        // 處理按鈕的文字；null＝沒有
        public string ActionKey;     // 交給主視窗的動作代號
        public Rectangle ActionRect;
    }

    /// <summary>
    /// 電腦健康檢查：把各工具的結果整理成一份清單與分數。
    /// 每項：良好不扣分、提示（Info）不扣分、注意（Warn）扣 6、需要處理（Bad）扣 15。
    /// </summary>
    public static class HealthCheck
    {
        public static int Score(IEnumerable<HealthItem> items)
        {
            int score = 100;
            foreach (var i in items)
            {
                if (i.Level == HealthLevel.Warn) score -= 6;
                else if (i.Level == HealthLevel.Bad) score -= 15;
            }
            return Math.Max(0, score);
        }

        /// <summary>依序執行每一項檢查，每完成一項就回報（畫面可以逐項出現）。</summary>
        public static void Run(Settings settings, IProgress<HealthItem> report, IProgress<string> status, CancellationToken token)
        {
            Action<Func<HealthItem>> step = f =>
            {
                if (token.IsCancellationRequested) return;
                HealthItem item;
                try { item = f(); }
                catch (Exception ex) { item = new HealthItem { Title = Lang.T("檢查失敗"), Detail = ex.Message, Level = HealthLevel.Info }; }
                if (item != null) report.Report(item);
            };

            status.Report(Lang.T("檢查磁碟空間…"));
            step(SystemDrive);
            step(OtherDrives);
            status.Report(Lang.T("掃描可清理的檔案…"));
            step(() => Junk(settings, token));
            step(RecycleBinItem);
            status.Report(Lang.T("檢查開機啟動項目…"));
            step(StartupItem);
            status.Report(Lang.T("檢查磁碟健康…"));
            step(DiskHealthItem);
            status.Report(Lang.T("檢查休眠檔與分頁檔…"));
            step(HibernateItem);
            step(PageFileItem);
            step(RestorePointItem);
            status.Report(Lang.T("檢查 WSL／Docker 虛擬磁碟…"));
            step(VirtualDiskItem);
            status.Report(Lang.T("檢查安裝殘留（要十幾秒）…"));
            step(() => LeftoverItem(token));
        }

        private static DriveInfo SystemDriveInfo()
        {
            return new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }

        private static HealthItem SystemDrive()
        {
            var d = SystemDriveInfo();
            double free = (double)d.AvailableFreeSpace / d.TotalSize;
            return new HealthItem
            {
                Title = Lang.T("系統磁碟空間"),
                Detail = string.Format(Lang.T("{0} 剩 {1}（{2:0}%）"), d.Name.TrimEnd('\\'), Format.Size(d.AvailableFreeSpace), free * 100),
                Level = free < 0.10 ? HealthLevel.Bad : free < 0.20 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = free < 0.20 ? Lang.T("大檔案分析") : null,
                ActionKey = "analyze:" + d.RootDirectory.FullName
            };
        }

        private static HealthItem OtherDrives()
        {
            string sys = SystemDriveInfo().Name;
            var low = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.Name != sys && d.TotalSize > 2L << 30
                                                       && (double)d.AvailableFreeSpace / d.TotalSize < 0.10).ToList();
            return new HealthItem
            {
                Title = Lang.T("其他磁碟空間"),
                Detail = low.Count == 0 ? Lang.T("其他磁碟空間都夠")
                    : string.Join("、", low.Select(d => string.Format(Lang.T("{0} 剩 {1}"), d.Name.TrimEnd('\\'), Format.Size(d.AvailableFreeSpace)))),
                Level = low.Count == 0 ? HealthLevel.Ok : HealthLevel.Warn,
                Action = low.Count == 0 ? null : Lang.T("大檔案分析"),
                ActionKey = low.Count == 0 ? null : "analyze:" + low[0].RootDirectory.FullName
            };
        }

        /// <summary>用使用者勾選的安全項目（同快速清理的範圍）掃一次，看有多少垃圾。</summary>
        private static HealthItem Junk(Settings settings, CancellationToken token)
        {
            var items = Cleaner.BuiltIn(settings.DevRoots).Where(i => i.SafeForAuto).ToList();
            var options = new ScanOptions { MinAgeHours = settings.MinAgeHours, Ignore = settings.Ignore };
            foreach (var i in items)
            {
                bool on;
                if (!settings.Checked.TryGetValue(i.Id, out on)) on = i.DefaultChecked;
                if (on) Cleaner.Scan(i, options, token, delegate { });
            }
            long bytes = items.Sum(i => i.FoundBytes);
            var top = items.Where(i => i.FoundBytes > 0).OrderByDescending(i => i.FoundBytes).Take(3).ToList();
            return new HealthItem
            {
                Title = Lang.T("可清理的垃圾檔案"),
                Detail = bytes == 0 ? Lang.T("很乾淨") : string.Format(Lang.T("{0}，最多的是 {1}"), Format.Size(bytes),
                    string.Join("、", top.Select(i => i.Name + " " + Format.Size(i.FoundBytes)))),
                Level = bytes > 5L << 30 ? HealthLevel.Bad : bytes > 1L << 30 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = bytes > 0 ? Lang.T("掃瞄清理") : null,
                ActionKey = "scan"
            };
        }

        private static HealthItem RecycleBinItem()
        {
            long size, count;
            RecycleBin.Query(out size, out count);
            return new HealthItem
            {
                Title = Lang.T("資源回收筒"),
                Detail = count == 0 ? Lang.T("是空的") : string.Format(Lang.T("{0:N0} 個項目，{1}"), count, Format.Size(size)),
                Level = size > 2L << 30 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = size > 2L << 30 ? Lang.T("掃瞄清理") : null,
                ActionKey = "scan"
            };
        }

        private static HealthItem StartupItem()
        {
            var entries = StartupManager.Load();
            int on = entries.Count(e => e.Enabled), missing = entries.Count(e => e.Missing);
            return new HealthItem
            {
                Title = Lang.T("開機啟動項目"),
                Detail = string.Format(Lang.T("{0} 個啟用中{1}"), on, missing > 0 ? string.Format(Lang.T("，{0} 個指向不存在的程式"), missing) : string.Empty),
                Level = on > 20 ? HealthLevel.Bad : on > 12 || missing > 0 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = on > 12 || missing > 0 ? Lang.T("管理啟動項目") : null,
                ActionKey = "startup"
            };
        }

        private static HealthItem DiskHealthItem()
        {
            var disks = DiskHealth.Load();
            var bad = disks.Where(d => d.Health == 2 || (d.Wear ?? 0) >= 90).ToList();
            var warn = disks.Where(d => d.Health == 1 || (d.Wear ?? 0) >= 70).Except(bad).ToList();
            return new HealthItem
            {
                Title = Lang.T("磁碟健康"),
                Detail = bad.Count + warn.Count == 0 ? string.Format(Lang.T("{0} 顆磁碟都健康"), disks.Count)
                    : string.Join("、", bad.Concat(warn).Select(d => d.Name + " " + d.HealthText)) + Lang.T("：請盡快備份重要資料"),
                Level = bad.Count > 0 ? HealthLevel.Bad : warn.Count > 0 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = Lang.T("查看"),
                ActionKey = "smart"
            };
        }

        private static HealthItem HibernateItem()
        {
            string path = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "hiberfil.sys");
            var info = new FileInfo(path);
            long size = info.Exists ? info.Length : 0;
            return new HealthItem
            {
                Title = Lang.T("休眠檔（hiberfil.sys）"),
                Detail = size == 0 ? Lang.T("沒有休眠檔（休眠已關閉）") : string.Format(Lang.T("{0}；不用休眠的話可以關掉"), Format.Size(size)),
                Level = size > 8L << 30 ? HealthLevel.Info : HealthLevel.Ok,
                Action = size > 0 ? Lang.T("在清單中勾選") : null,
                ActionKey = "select:win.hiberfil"
            };
        }

        private static HealthItem PageFileItem()
        {
            long mb = 0;
            using (var searcher = new ManagementObjectSearcher("SELECT AllocatedBaseSize FROM Win32_PageFileUsage"))
            {
                foreach (ManagementObject o in searcher.Get()) mb += Convert.ToInt64(o["AllocatedBaseSize"]);
            }
            return new HealthItem
            {
                Title = Lang.T("分頁檔（pagefile.sys）"),
                Detail = string.Format(Lang.T("{0}，由 Windows 自動管理；空間很緊時可在「系統 → 進階系統設定 → 效能」調小，不建議關閉"), Format.Size(mb << 20)),
                Level = HealthLevel.Ok
            };
        }

        private static HealthItem RestorePointItem()
        {
            long used = 0, max = 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT UsedSpace, MaxSpace FROM Win32_ShadowStorage"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        used += Convert.ToInt64(o["UsedSpace"]);
                        max += Convert.ToInt64(o["MaxSpace"]);
                    }
                }
            }
            catch (Exception)
            {
                return new HealthItem { Title = Lang.T("系統還原點"), Detail = Lang.T("需要系統管理員權限才讀得到"), Level = HealthLevel.Info };
            }
            return new HealthItem
            {
                Title = Lang.T("系統還原點"),
                Detail = string.Format(Lang.T("佔用 {0}（上限 {1}）"), Format.Size(used), Format.Size(max)),
                Level = used > 20L << 30 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = used > 20L << 30 ? Lang.T("管理還原點") : null,
                ActionKey = "restore"
            };
        }

        private static HealthItem VirtualDiskItem()
        {
            var disks = VirtualDisks.Find();
            long total = disks.Sum(d => d.Size);
            return new HealthItem
            {
                Title = Lang.T("WSL／Docker 虛擬磁碟"),
                Detail = disks.Count == 0 ? Lang.T("沒有") : string.Format(Lang.T("{0} 個，共 {1}；壓縮可以把 Linux 裡刪掉的空間還給 Windows"), disks.Count, Format.Size(total)),
                Level = total > 20L << 30 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = disks.Count > 0 ? Lang.T("壓縮") : null,
                ActionKey = "vhdx"
            };
        }

        private static HealthItem LeftoverItem(CancellationToken token)
        {
            var found = Leftovers.Find(token, delegate { });
            long total = found.Sum(f => f.Size);
            return new HealthItem
            {
                Title = Lang.T("安裝殘留"),
                Detail = found.Count == 0 ? Lang.T("沒有找到") : string.Format(Lang.T("{0} 個可能的殘留資料夾，共 {1}（推測，請確認後再刪）"), found.Count, Format.Size(total)),
                Level = total > 1L << 30 ? HealthLevel.Warn : HealthLevel.Ok,
                Action = found.Count > 0 ? Lang.T("查看") : null,
                ActionKey = "leftovers"
            };
        }
    }

    /// <summary>分數環＋評語。</summary>
    public class ScoreGauge : Control
    {
        private int _score = -1;
        private float _shown;
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer { Interval = 15 };
        public string Caption { get; set; }
        private static readonly Font GradeFont = new Font("Microsoft JhengHei UI", 16f, FontStyle.Bold);

        public ScoreGauge()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _anim.Tick += delegate
            {
                _shown += (_score - _shown) * 0.12f;
                if (Math.Abs(_score - _shown) < 0.5f) { _shown = _score; _anim.Stop(); }
                Invalidate();
            };
        }

        public void SetScore(int score)
        {
            _score = score;
            _anim.Start();
        }

        public static Color ColorOf(int score)
        {
            return score >= 90 ? Theme.Accent : score >= 70 ? Theme.Amber : Theme.Red;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ring = new RectangleF(22, 14, Height - 28, Height - 28);
            using (var pen = new Pen(Theme.HeaderRow, 14f)) g.DrawEllipse(pen, ring);
            if (_score >= 0)
            {
                using (var pen = new Pen(ColorOf((int)Math.Round(_shown)), 14f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(pen, ring, -90, 360f * _shown / 100f);
                }
            }
            string text = _score < 0 ? "…" : ((int)Math.Round(_shown)).ToString();
            TextRenderer.DrawText(g, text, Theme.Number, Rectangle.Round(ring), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            float x = ring.Right + 30;
            string grade = _score < 0 ? Lang.T("檢查中…") : _score >= 90 ? Lang.T("狀況很好") : _score >= 70 ? Lang.T("還可以，有幾項可以處理") : Lang.T("需要整理");
            TextRenderer.DrawText(g, grade, GradeFont, new Point((int)x, (int)ring.Top + 22),
                _score < 0 ? Theme.TextMuted : ColorOf(_score));
            TextRenderer.DrawText(g, Caption ?? string.Empty, Theme.Ui, new Rectangle((int)x, (int)ring.Top + 62, Width - (int)x - 20, 40), Theme.TextMuted,
                TextFormatFlags.WordBreak);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>檢查結果清單：左邊狀態圓點、標題與說明、右邊處理按鈕（文字連結樣式）。</summary>
    public class HealthList : ScrollableControl
    {
        private readonly List<HealthItem> _items = new List<HealthItem>();
        private HealthItem _hover;
        private const int RowHeight = 62;
        public event Action<string> ActionClicked;

        public HealthList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
        }

        public void Clear() { _items.Clear(); AutoScrollMinSize = Size.Empty; Invalidate(); }

        public void Add(HealthItem item)
        {
            _items.Add(item);
            AutoScrollMinSize = new Size(0, _items.Count * RowHeight + 10);
            Invalidate();
        }

        public IReadOnlyList<HealthItem> Items { get { return _items; } }

        private static Color LevelColor(HealthLevel l)
        {
            return l == HealthLevel.Bad ? Theme.Red : l == HealthLevel.Warn ? Theme.Amber : l == HealthLevel.Info ? Theme.Blue : Theme.Accent;
        }

        private static string LevelText(HealthLevel l)
        {
            return l == HealthLevel.Bad ? Lang.T("需要處理") : l == HealthLevel.Warn ? Lang.T("注意") : l == HealthLevel.Info ? Lang.T("提示") : Lang.T("良好");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 不用 TranslateTransform：TextRenderer 預設會忽略它，捲動後文字會跟底色錯開。
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                int y = i * RowHeight + 6 + AutoScrollPosition.Y;
                var row = new Rectangle(8, y, Width - 16 - SystemInformation.VerticalScrollBarWidth, RowHeight - 6);
                if (i % 2 == 1) Theme.FillRound(g, Theme.SurfaceAlt, row, 8);

                Color c = LevelColor(it.Level);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, row.Left + 14, row.Top + 18, 14, 14);
                TextRenderer.DrawText(g, it.Title, Theme.UiBold, new Point(row.Left + 40, row.Top + 7), Theme.Text);

                var tag = TextRenderer.MeasureText(LevelText(it.Level), Theme.SmallBold);
                var tagRect = new RectangleF(row.Left + 40 + TextRenderer.MeasureText(it.Title, Theme.UiBold).Width + 4, row.Top + 7, tag.Width + 12, 20);
                Theme.FillRound(g, Theme.Tint(c), tagRect, 10);
                TextRenderer.DrawText(g, LevelText(it.Level), Theme.SmallBold, Rectangle.Round(tagRect), Theme.TintText(c), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                int right = row.Right - 12;
                it.ActionRect = Rectangle.Empty;
                if (it.Action != null && it.ActionKey != null)
                {
                    var size = TextRenderer.MeasureText(it.Action, Theme.UiBold);
                    var r = new Rectangle(right - size.Width - 24, row.Top + 13, size.Width + 24, 30);
                    Theme.FillRound(g, it == _hover ? Theme.Accent : Theme.Tint(Theme.Accent), r, 15);
                    TextRenderer.DrawText(g, it.Action, Theme.UiBold, r, it == _hover ? Color.White : Theme.TintText(Theme.Accent),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    it.ActionRect = r;
                    right = r.Left - 10;
                }
                TextRenderer.DrawText(g, it.Detail ?? string.Empty, Theme.Ui, new Rectangle(row.Left + 40, row.Top + 30, right - row.Left - 40, 22), Theme.TextMuted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = _items.FirstOrDefault(i => i.ActionRect.Contains(e.Location));
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            if (hit != _hover) { _hover = hit; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var hit = _items.FirstOrDefault(i => i.ActionRect.Contains(e.Location));
            var handler = ActionClicked;
            if (hit != null && handler != null) handler(hit.ActionKey);
        }

        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
    }

    public class HealthForm : ToolWindow
    {
        private readonly ScoreGauge _gauge = new ScoreGauge { Dock = DockStyle.Top, Height = 150 };
        private readonly HealthList _list = new HealthList { Dock = DockStyle.Fill };
        private readonly RoundButton _again;
        private readonly Settings _settings;
        private CancellationTokenSource _cts;

        public HealthForm(Settings settings, Action<string> action) : base(Lang.T("電腦健康檢查"), Lang.T("一次看完磁碟空間、垃圾檔案、開機項目、磁碟健康等狀況，每項都可以直接處理"), "", new Size(900, 680))
        {
            _settings = settings;
            _again = Primary(Lang.T("重新檢查"), 120);
            var top = new CardPanel { Dock = DockStyle.Top, Height = 154, Padding = new Padding(2) };
            top.Controls.Add(_gauge);
            var spacer = new Panel { Dock = DockStyle.Top, Height = 10 };

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(spacer);
            Content.Controls.Add(top);
            Content.Controls.Add(Toolbar(_again));

            _list.ActionClicked += key => action(key);
            _again.Click += delegate { Start(); };
            Load += delegate { Start(); };
            FormClosing += delegate { if (_cts != null) _cts.Cancel(); };
        }

        private async void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _again.Enabled = false;
            _list.Clear();
            _gauge.SetScore(-1);
            _gauge.Caption = string.Empty;

            var report = new Progress<HealthItem>(i =>
            {
                _list.Add(i);
                _gauge.Caption = string.Format(Lang.T("已完成 {0} 項檢查…"), _list.Items.Count);
                _gauge.Invalidate();
            });
            var status = new Progress<string>(t => Status.Text = t);
            await Task.Run(() => HealthCheck.Run(_settings, report, status, token));
            await Task.Delay(100);   // 讓最後一筆 Progress 回報先進來

            int score = HealthCheck.Score(_list.Items);
            int bad = _list.Items.Count(i => i.Level == HealthLevel.Bad), warn = _list.Items.Count(i => i.Level == HealthLevel.Warn);
            _gauge.Caption = string.Format(Lang.T("{0} 項檢查：{1} 項需要處理、{2} 項注意"), _list.Items.Count, bad, warn);
            _gauge.SetScore(score);
            Status.Text = Lang.T("檢查完成");
            _again.Enabled = true;
            _cts = null;
        }
    }
}
