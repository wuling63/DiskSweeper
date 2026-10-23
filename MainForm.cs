using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace DiskCleaner
{
    public class MainForm : Form
    {
        private enum Phase { Idle, Scanning, Scanned, Cleaning }

        private const int Unchecked = 0, Checked = 1, Mixed = 2;   // 樹狀清單的勾選狀態圖索引

        private static readonly Font UiFont = new Font("Microsoft JhengHei UI", 9.5f);

        private readonly Settings _settings = Settings.Load();
        private readonly bool _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        private List<CleanItem> _items;
        private Phase _phase = Phase.Idle;
        private CancellationTokenSource _cts;
        private string _lastResult;

        private HeaderPanel _header;
        private TreeView _tree;
        private ListView _list;
        private List<FileEntry> _source = new List<FileEntry>();   // 左邊選中的項目的全部結果
        private List<FileEntry> _shown = new List<FileEntry>();    // 套用搜尋與排序後實際顯示的
        private int _sortColumn = -1;
        private bool _sortDesc;
        private SearchBox _search;
        private Banner _lowSpace;
        private DrivesPanel _drives;
        private OverviewPanel _overview;
        private CardPanel _overviewCard;
        private long _maxShown = 1;                                    // 大小欄長條的比例基準
        private long _lastFreed;                                       // 上次清理釋放的空間（橫幅大字用）
        private readonly System.Windows.Forms.Timer _live = new System.Windows.Forms.Timer { Interval = 120 };   // 掃描中即時更新容量
        private ToolStripStatusLabel _status, _statusRight;

        private NotifyIcon _tray;
        private bool _exiting;            // 從系統匣選單「結束」：真的關閉，不再縮回系統匣
        private bool _trayTipShown;

        public MainForm()
        {
            Theme.SetMode(Theme.ResolveDark(_settings.Appearance));
            Text = Lang.T("磁碟清理") + (_isAdmin ? Lang.T("（系統管理員）") : string.Empty);
            Font = UiFont;
            BackColor = Theme.Background;
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(760, 480);

            BuildUi();
            BuildTray();
            LoadItems();
            UpdateHeader();

            Load += delegate
            {
                Theme.Apply(this);
                SpaceLog.Record();
                Backup.Purge(_settings.BackupDays);
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray) HideToTray();
            };
            FormClosing += (s, e) =>
            {
                if (_settings.CloseToTray && !_exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
                if (_cts != null) _cts.Cancel();
                SaveChecks();
                _settings.Save();
            };
            FormClosed += delegate
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            };
        }

        /// <summary>Windows 切換淺色／深色時，外觀設成「跟隨系統」的就跟著換。</summary>
        private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (_settings.Appearance != "system" || IsDisposed) return;
            BeginInvoke((Action)delegate { ApplyAppearance(); });
        }

        private void ApplyAppearance()
        {
            Theme.SetMode(Theme.ResolveDark(_settings.Appearance));
            _tree.StateImageList = BuildStateImages();
            _tree.Invalidate();
            _list.Invalidate();
        }

        // ───────────────────────────── 系統匣 ─────────────────────────────

        private void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new ModernRenderer() };
            menu.Items.Add(Lang.T("開啟磁碟清理"), null, delegate { RestoreFromTray(); });
            menu.Items.Add(Lang.T("快速清理"), null, delegate { QuickClean(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("結束"), null, delegate { _exiting = true; Close(); });

            _tray = new NotifyIcon { Icon = AppIcon.Get(), Text = Lang.T("磁碟清理"), ContextMenuStrip = menu };
            _tray.DoubleClick += delegate { RestoreFromTray(); };
            _tray.BalloonTipClicked += delegate { RestoreFromTray(); };
            UpdateTrayVisibility();
        }

        private void UpdateTrayVisibility()
        {
            _tray.Visible = _settings.MinimizeToTray || _settings.CloseToTray;
        }

        private void HideToTray()
        {
            _tray.Visible = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _tray.ShowBalloonTip(4000, Lang.T("磁碟清理"), Lang.T("程式在系統匣裡繼續執行；右鍵圖示可以快速清理或結束。"), ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            UpdateTrayVisibility();
        }

        /// <summary>系統匣的「快速清理」：用勾選的檔案類項目在背景清理，完成後跳通知（範圍同每週自動清理）。</summary>
        private async void QuickClean()
        {
            if (_phase == Phase.Scanning || _phase == Phase.Cleaning) return;
            SaveChecks();
            _tray.ShowBalloonTip(3000, Lang.T("磁碟清理"), Lang.T("快速清理中…"), ToolTipIcon.Info);
            _status.Text = Lang.T("快速清理中…");

            CleanResult result;
            try
            {
                result = await Task.Run(() => Background.SafeClean(_settings, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(5000, Lang.T("磁碟清理"), Lang.T("快速清理失敗：") + ex.Message, ToolTipIcon.Warning);
                return;
            }

            string message = string.Format(Lang.T("快速清理完成：刪除 {0:N0} 個檔案，釋放 {1}"), result.Deleted, Format.Size(result.Bytes));
            _tray.ShowBalloonTip(5000, Lang.T("磁碟清理"), message, ToolTipIcon.Info);
            _status.Text = message;
            _lastFreed = result.Bytes;
            _lastResult = message;
            UpdateHeader();
            CheckLowSpace();
        }

        // ───────────────────────────── 介面 ─────────────────────────────

        private void BuildUi()
        {
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem(Lang.T("檔案"));
            file.DropDownItems.Add(Lang.T("匯出掃描結果（CSV）…"), null, delegate { ExportCsv(); });
            file.DropDownItems.Add(Lang.T("以系統管理員身分重新啟動"), null, delegate { RestartAsAdmin(); }).Enabled = !_isAdmin;
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Lang.T("結束"), null, delegate { Close(); });

            var edit = new ToolStripMenuItem(Lang.T("編輯"));
            edit.DropDownItems.Add(Lang.T("全部選取"), null, delegate { SetAll(SelectAllRule); });
            edit.DropDownItems.Add(Lang.T("全不選取"), null, delegate { SetAll(i => false); });
            edit.DropDownItems.Add(Lang.T("預設選取"), null, delegate { SetAll(i => i.DefaultChecked); });

            var view = new ToolStripMenuItem(Lang.T("檢視"));
            view.DropDownItems.Add(Lang.T("全部展開"), null, delegate { _tree.ExpandAll(); });
            view.DropDownItems.Add(Lang.T("全部收合"), null, delegate { _tree.CollapseAll(); });
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(Lang.T("檢視歷史記錄"), null, delegate { ShowHistory(); });

            var tools = new ToolStripMenuItem(Lang.T("工具"));
            var disk = new ToolStripMenuItem(Lang.T("磁碟分析"));
            disk.DropDownItems.Add(Lang.T("大檔案分析…"), null, delegate { ShowAnalyzer(0); });
            disk.DropDownItems.Add(Lang.T("重複檔案…"), null, delegate { ShowAnalyzer(1); });
            disk.DropDownItems.Add(Lang.T("空資料夾…"), null, delegate { ShowAnalyzer(2); });
            disk.DropDownItems.Add(Lang.T("安裝殘留…"), null, delegate { new LeftoverForm().Show(this); });
            disk.DropDownItems.Add(Lang.T("磁碟空間趨勢…"), null, delegate { new TrendForm(_settings.History).Show(this); });
            disk.DropDownItems.Add(Lang.T("壓縮資料夾…"), null, delegate { new CompressForm().Show(this); });
            var system = new ToolStripMenuItem(Lang.T("系統"));
            system.DropDownItems.Add(Lang.T("已安裝的程式…"), null, delegate { new InstalledAppsForm().Show(this); });
            system.DropDownItems.Add(Lang.T("開機啟動項目…"), null, delegate { new StartupForm().Show(this); });
            system.DropDownItems.Add(Lang.T("背景服務…"), null, delegate { new ServicesForm().Show(this); });
            system.DropDownItems.Add(Lang.T("資源佔用…"), null, delegate { new ProcessForm().Show(this); });
            system.DropDownItems.Add(Lang.T("系統還原點…"), null, delegate { new RestorePointsForm().Show(this); });
            var security = new ToolStripMenuItem(Lang.T("安全"));
            security.DropDownItems.Add(Lang.T("檔案粉碎機…"), null, delegate { new ShredderForm().Show(this); });
            tools.DropDownItems.Add(disk);
            tools.DropDownItems.Add(system);
            tools.DropDownItems.Add(security);
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(Lang.T("清理備份…"), null, delegate { new BackupForm().Show(this); });

            var options = new ToolStripMenuItem(Lang.T("設定"));
            options.DropDownItems.Add(Lang.T("選項…"), null, delegate { ShowOptions(); });
            options.DropDownItems.Add(Lang.T("忽略清單…"), null, delegate { ShowIgnoreList(); });
            options.DropDownItems.Add(Lang.T("自訂義檔案和資料夾…"), null, delegate { ShowCustomFolders(); });

            var help = new ToolStripMenuItem(Lang.T("說明"));
            help.DropDownItems.Add(Lang.T("關於"), null, delegate
            {
                Msg.Show(this,
                    Lang.T("磁碟清理\r\n\r\n掃描並刪除系統暫存、快取、錯誤報告等可安全移除的檔案。\r\n")
                    + Lang.T("瀏覽器預設只清快取；歷史紀錄與 Cookie 在「隱私」分類，要自己勾選。\r\n\r\n")
                    + Lang.T("設定與記錄：%LOCALAPPDATA%\\DiskCleaner"),
                    Lang.T("關於"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.AddRange(new ToolStripItem[] { file, edit, view, tools, options, help });

            menu.Renderer = new ModernRenderer();
            menu.BackColor = Theme.Surface;
            menu.Padding = new Padding(6, 3, 0, 3);

            _header = new HeaderPanel { Dock = DockStyle.Top };
            _header.Action.Click += delegate { OnAction(); };
            _header.HistoryClicked += delegate { ShowHistory(); };

            // ── 左：工作清單 ──
            var states = BuildStateImages();

            var icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            foreach (int siid in new[]
            {
                StockIcons.Folder, StockIcons.RecyclerFull, StockIcons.Link, StockIcons.World, StockIcons.Shield,
                StockIcons.Warning, StockIcons.ImageFiles, StockIcons.DriveFixed, StockIcons.Application,
                StockIcons.Settings, StockIcons.DocNoAssoc, StockIcons.Key, StockIcons.Lock
            })
            {
                icons.Images.Add(StockIcons.Get(siid, 16));
            }

            // 勾選框自己畫（StateImageList），才有父節點的「部分勾選」狀態；文字也自己畫，容量放成右邊的標籤。
            _tree = new NoHScrollTreeView
            {
                Dock = DockStyle.Fill,
                StateImageList = states,
                ImageList = icons,
                ShowNodeToolTips = true,
                HideSelection = false,
                ShowLines = false,      // 不開 FullRowSelect：選取底色由 DrawNode 畫，開了系統會把勾選框那格也塗成藍色
                ItemHeight = 26,
                Indent = 18,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                DrawMode = TreeViewDrawMode.OwnerDrawText
            };
            _tree.DrawNode += Tree_DrawNode;
            _tree.NodeMouseClick += Tree_NodeMouseClick;
            _tree.KeyDown += Tree_KeyDown;
            _tree.AfterSelect += delegate { ShowFiles(); _tree.Invalidate(); };

            var leftTitle = new Label
            {
                Dock = DockStyle.Top, Height = 40, Text = Lang.T("工作清單"), Padding = new Padding(12, 11, 0, 0),
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Surface
            };
            var treeHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 4, 6), BackColor = Theme.Surface };
            treeHolder.Controls.Add(_tree);

            var leftCard = new CardPanel { Dock = DockStyle.Fill };
            leftCard.Controls.Add(treeHolder);
            leftCard.Controls.Add(leftTitle);

            var leftLinks = LinkRow(DockStyle.Bottom,
                Link(Lang.T("全部選取"), delegate { SetAll(SelectAllRule); }),
                Link(Lang.T("全不選取"), delegate { SetAll(i => false); }),
                Link(Lang.T("預設選取"), delegate { SetAll(i => i.DefaultChecked); }));

            // ── 右：總覽＋搜尋＋檔案清單（虛擬模式，幾萬筆也不卡；自己畫，大小欄有比例長條） ──
            _overview = new OverviewPanel { Dock = DockStyle.Fill };
            _overview.ItemClicked += tag => SelectByTag(tag);
            _overviewCard = new CardPanel { Dock = DockStyle.Top, Height = 172, Padding = new Padding(2), Visible = false };
            _overviewCard.Controls.Add(_overview);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                VirtualMode = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                OwnerDraw = true,
                BackColor = Theme.Surface
            };
            _list.Columns.Add(Lang.T("路徑"), 520);
            _list.Columns.Add(Lang.T("大小"), 150, HorizontalAlignment.Right);
            _list.Columns.Add(Lang.T("建立時間"), 140);
            _list.RetrieveVirtualItem += List_RetrieveVirtualItem;
            _list.DrawColumnHeader += List_DrawColumnHeader;
            _list.DrawItem += (s, e) => { };   // 細節檢視只畫子項目
            _list.DrawSubItem += List_DrawSubItem;
            // 路徑欄吃掉剩下的寬度，大小與建立時間固定。
            _list.Resize += delegate { FitColumns(); };
            _list.DoubleClick += delegate { OpenFolder(); };
            _list.ColumnClick += (s, e) => SortBy(e.Column);

            var listMenu = new ContextMenuStrip { Renderer = new ModernRenderer() };
            listMenu.Items.Add(Lang.T("開啟資料夾"), null, delegate { OpenFolder(); });
            listMenu.Items.Add(Lang.T("屬性"), null, delegate { ShowProperties(); });
            listMenu.Items.Add(Lang.T("複製路徑"), null, delegate { CopyPaths(); });
            listMenu.Items.Add(new ToolStripSeparator());
            listMenu.Items.Add(Lang.T("排除此檔案（加入忽略清單）"), null, delegate { ExcludeSelected(); });
            _list.ContextMenuStrip = listMenu;

            var listCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1, 1, 1, 6) };
            listCard.Controls.Add(_list);

            _search = new SearchBox(Lang.T("搜尋路徑…")) { Dock = DockStyle.Fill };
            _search.TextChanged += delegate { ApplyView(); };
            var searchBar = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 8, 0, 6), BackColor = Theme.Background };
            searchBar.Controls.Add(_search);

            var rightLinks = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = Theme.Background };
            var rightFlow = LinkRow(DockStyle.Left,
                Link(Lang.T("自訂義檔案和資料夾"), delegate { ShowCustomFolders(); }),
                Link(Lang.T("開啟資料夾"), delegate { OpenFolder(); }),
                Link(Lang.T("屬性"), delegate { ShowProperties(); }));
            rightFlow.Width = 420;
            var exclude = Link(Lang.T("排除此檔案"), delegate { ExcludeSelected(); });
            exclude.Dock = DockStyle.Right;
            exclude.Padding = new Padding(0, 8, 4, 0);
            rightLinks.Controls.Add(rightFlow);
            rightLinks.Controls.Add(exclude);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 12, BackColor = Theme.Background };
            // 分隔線位置與最小寬度要等視窗量好大小才設得進去，太早設會丟例外。
            Load += delegate
            {
                split.Panel1MinSize = 240;
                split.SplitterDistance = 330;
                FitColumns();
            };
            split.Panel1.Controls.Add(leftCard);
            split.Panel1.Controls.Add(leftLinks);
            split.Panel2.Controls.Add(listCard);
            split.Panel2.Controls.Add(searchBar);
            split.Panel2.Controls.Add(_overviewCard);
            split.Panel2.Controls.Add(rightLinks);

            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 0), BackColor = Theme.Background };
            content.Controls.Add(split);

            // ── 底列：忽略清單 ──
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Theme.Background };
            var ignore = new RoundButton { Text = Lang.T("忽略清單"), Size = new Size(130, 40), Style = RoundButton.Kind.Light, Font = Theme.Ui };
            ignore.Click += delegate { ShowIgnoreList(); };
            bottom.Controls.Add(ignore);
            bottom.Resize += delegate { ignore.Location = new Point(bottom.Width - ignore.Width - 12, 8); };

            var status = new StatusStrip { Renderer = new ModernRenderer(), BackColor = Theme.Surface, SizingGrip = false };
            _status = new ToolStripStatusLabel(Lang.T("就緒")) { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, ForeColor = Theme.TextMuted };
            _statusRight = new ToolStripStatusLabel(_isAdmin
                ? Lang.T("● 系統管理員模式")
                : Lang.T("一般權限：標盾牌的系統項目只掃得到可讀的部分（檔案 → 以系統管理員身分重新啟動）")) { ForeColor = _isAdmin ? Theme.AccentDark : Theme.TextMuted };
            status.Items.Add(_status);
            status.Items.Add(_statusRight);

            _lowSpace = new Banner { Dock = DockStyle.Top, Padding = new Padding(14, 10, 14, 0) };
            CheckLowSpace();

            _live.Tick += delegate { UpdateHeader(); };

            Controls.Add(content);
            Controls.Add(bottom);
            Controls.Add(status);
            Controls.Add(_lowSpace);

            // 所有磁碟的空間卡片，緊貼在橫幅下方；點卡片用「大檔案分析」分析那個磁碟。
            _drives = new DrivesPanel { Dock = DockStyle.Top };
            _drives.DriveClicked += root => ShowAnalyzer(0, root);
            Controls.Add(_drives);
            Controls.Add(_header);
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        /// <summary>路徑欄吃掉剩下的寬度，大小與建立時間固定，不出現水平捲軸。</summary>
        private void FitColumns()
        {
            int rest = _list.ClientSize.Width - _list.Columns[1].Width - _list.Columns[2].Width - SystemInformation.VerticalScrollBarWidth;
            if (rest > 120) _list.Columns[0].Width = rest;
        }

        private static ImageList BuildStateImages()
        {
            var states = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
            states.Images.Add(CheckImage(Unchecked));
            states.Images.Add(CheckImage(Checked));
            states.Images.Add(CheckImage(Mixed));
            return states;
        }

        /// <summary>圓角勾選框：勾＝主色底白勾，部分＝主色底白橫線，沒勾＝白底灰框。</summary>
        private static Bitmap CheckImage(int state)
        {
            var bmp = new Bitmap(18, 18);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new RectangleF(1.5f, 1.5f, 14, 14);
                if (state == Unchecked)
                {
                    // 不填底色，淺色與深色模式都看得清楚。
                    Theme.DrawRound(g, Color.FromArgb(148, 163, 184), r, 4, 1.4f);
                }
                else
                {
                    Theme.FillRound(g, Theme.Accent, r, 4);
                    using (var pen = new Pen(Color.White, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    {
                        if (state == Checked) g.DrawLines(pen, new[] { new PointF(5, 9), new PointF(7.6f, 11.6f), new PointF(12.5f, 6) });
                        else g.DrawLine(pen, 5, 8.5f, 12, 8.5f);
                    }
                }
            }
            return bmp;
        }

        /// <summary>樹節點：選取列淡綠底、分類粗體帶色點、右邊是容量標籤。</summary>
        private void Tree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var row = new Rectangle(e.Bounds.Left, e.Bounds.Top, _tree.ClientSize.Width - e.Bounds.Left, e.Bounds.Height);
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            using (var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, row);
            if (selected) Theme.FillRound(g, _tree.Focused ? Theme.AccentLight : Theme.AccentSoft, new RectangleF(row.Left, row.Top + 1, row.Width - 4, row.Height - 2), 6);

            bool isCategory = e.Node.Parent == null;
            long bytes = isCategory
                ? e.Node.Nodes.Cast<TreeNode>().Sum(n => ((CleanItem)n.Tag).FoundBytes)
                : ((CleanItem)e.Node.Tag).FoundBytes;
            bool hasResult = isCategory
                ? e.Node.Nodes.Cast<TreeNode>().Any(n => ((CleanItem)n.Tag).Found.Count > 0)
                : ((CleanItem)e.Node.Tag).Found.Count > 0;

            // 容量標籤
            int right = row.Right - 8;
            if (hasResult)
            {
                string text = Format.Size(bytes);
                var size = TextRenderer.MeasureText(text, Theme.SmallBold);
                var pill = new RectangleF(right - size.Width - 10, row.Top + 4, size.Width + 10, row.Height - 8);
                Color pillColor = isCategory ? Theme.CategoryColor((string)e.Node.Tag) : Theme.Accent;
                Theme.FillRound(g, Theme.Tint(pillColor), pill, pill.Height / 2);
                TextRenderer.DrawText(g, text, Theme.SmallBold, Rectangle.Round(pill), Theme.TintText(pillColor),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                right = (int)pill.Left - 6;
            }

            // 名稱
            int x = e.Bounds.Left + 2;
            if (isCategory)
            {
                using (var dot = new SolidBrush(Theme.CategoryColor((string)e.Node.Tag))) g.FillEllipse(dot, x, row.Top + row.Height / 2 - 4, 8, 8);
                x += 13;
            }
            var item = e.Node.Tag as CleanItem;
            Color fore = item != null && item.Privacy ? (Theme.Dark ? Color.FromArgb(251, 113, 133) : Color.FromArgb(190, 18, 60)) : Theme.Text;
            TextRenderer.DrawText(g, e.Node.Text, isCategory ? Theme.UiBold : Theme.Ui,
                new Rectangle(x, row.Top, Math.Max(10, right - x), row.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private void List_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var back = new SolidBrush(Theme.HeaderRow)) e.Graphics.FillRectangle(back, e.Bounds);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            string text = e.Header.Text;
            if (e.ColumnIndex == _sortColumn) text += _sortDesc ? "  ▼" : "  ▲";
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                      | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, text, Theme.SmallBold, Rectangle.Inflate(e.Bounds, -8, 0), Theme.TextMuted, flags);
        }

        /// <summary>間隔列淡底、選取列淡綠；大小欄畫一條跟最大檔案比例的長條。</summary>
        private void List_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            bool selected = _list.SelectedIndices.Contains(e.ItemIndex);
            Color back = selected ? Theme.AccentLight : e.ItemIndex % 2 == 1 ? Theme.SurfaceAlt : Theme.Surface;
            using (var brush = new SolidBrush(back)) g.FillRectangle(brush, e.Bounds);

            var entry = e.ItemIndex < _shown.Count ? _shown[e.ItemIndex] : null;
            var r = Rectangle.Inflate(e.Bounds, -8, 0);

            if (e.ColumnIndex == 1 && entry != null && entry.Size > 0)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float w = Math.Max(3, (float)(r.Width * 0.55 * entry.Size / Math.Max(1, _maxShown)));
                var bar = new RectangleF(r.Left, r.Top + r.Height / 2f - 3, w, 6);
                Theme.FillRound(g, Theme.Blend(Theme.Accent, Color.White, selected ? 0.2f : 0.45f), bar, 3);
            }

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                      | (e.ColumnIndex == 0 ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis)
                      | (e.ColumnIndex == 1 ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(g, e.SubItem.Text, e.ColumnIndex == 1 ? Theme.SmallBold : Theme.Ui, r,
                e.ColumnIndex == 0 ? Theme.Text : Theme.TextMuted, flags);
        }

        /// <summary>總覽點了分類或項目：在左邊樹裡選到它。</summary>
        private void SelectByTag(object tag)
        {
            foreach (TreeNode parent in _tree.Nodes)
            {
                if (Equals(parent.Tag, tag)) { _tree.SelectedNode = parent; return; }
                foreach (TreeNode node in parent.Nodes)
                {
                    if (node.Tag == tag) { parent.Expand(); _tree.SelectedNode = node; node.EnsureVisible(); return; }
                }
            }
        }

        private static LinkLabel Link(string text, EventHandler click)
        {
            var link = new LinkLabel
            {
                Text = text, AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline,
                LinkColor = Theme.AccentDark, ActiveLinkColor = Theme.Accent, Margin = new Padding(0, 8, 14, 0)
            };
            link.LinkClicked += delegate { click(link, EventArgs.Empty); };
            return link;
        }

        private static FlowLayoutPanel LinkRow(DockStyle dock, params Control[] links)
        {
            var row = new FlowLayoutPanel { Dock = dock, Height = 32, WrapContents = false, Padding = new Padding(4, 0, 0, 0), BackColor = Theme.Background };
            row.Controls.AddRange(links);
            return row;
        }

        // ───────────────────────────── 工作清單 ─────────────────────────────

        private void LoadItems()
        {
            SaveChecks();
            _items = Cleaner.BuiltIn(_settings.DevRoots);
            if (_settings.CustomFolders.Count > 0) _items.Add(Cleaner.Custom(_settings.CustomFolders));

            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            foreach (var group in _items.GroupBy(i => i.Category))
            {
                int icon = group.Key == Cleaner.CatWindows ? Cleaner.IconDrive
                         : group.Key == Cleaner.CatBrowser ? Cleaner.IconWorld
                         : group.Key == Cleaner.CatApps ? Cleaner.IconApp
                         : group.Key == Cleaner.CatDev ? Cleaner.IconDoc
                         : group.Key == Cleaner.CatPrivacy ? Cleaner.IconLock
                         : Cleaner.IconFolder;
                var parent = new TreeNode(Lang.T(group.Key)) { Tag = group.Key, ImageIndex = icon, SelectedImageIndex = icon };
                parent.NodeFont = new Font(UiFont, FontStyle.Bold);
                int onCount = 0;

                foreach (var item in group)
                {
                    bool on;
                    if (!_settings.Checked.TryGetValue(item.Id, out on)) on = item.DefaultChecked;
                    if (on) onCount++;

                    var node = new TreeNode(item.Name)
                    {
                        Tag = item,
                        ImageIndex = item.Icon,
                        SelectedImageIndex = item.Icon,
                        StateImageIndex = on ? Checked : Unchecked,
                        ToolTipText = item.Description
                                    + (item.NeedsAdmin && !_isAdmin ? Lang.T("\r\n（需要系統管理員權限才掃得完整）") : string.Empty)
                    };
                    if (item.NeedsAdmin && !_isAdmin)
                    {
                        node.ImageIndex = node.SelectedImageIndex = Cleaner.IconShield;
                    }
                    parent.Nodes.Add(node);
                }

                _tree.Nodes.Add(parent);

                // 子節點掛進 TreeView 之前讀不到正確的 StateImageIndex，父節點狀態直接用剛才數的算。
                parent.StateImageIndex = onCount == 0 ? Unchecked : onCount == parent.Nodes.Count ? Checked : Mixed;
            }
            _tree.ExpandAll();
            _tree.EndUpdate();
            if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];
        }

        private IEnumerable<TreeNode> Leaves()
        {
            return _tree.Nodes.Cast<TreeNode>().SelectMany(p => p.Nodes.Cast<TreeNode>());
        }

        private List<CleanItem> CheckedItems()
        {
            return Leaves().Where(n => n.StateImageIndex == Checked).Select(n => (CleanItem)n.Tag).ToList();
        }

        private void SaveChecks()
        {
            if (_tree == null) return;
            foreach (var node in Leaves()) _settings.Checked[((CleanItem)node.Tag).Id] = node.StateImageIndex == Checked;
        }

        private void Tree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var hit = _tree.HitTest(e.Location);
            if (hit.Location == TreeViewHitTestLocations.StateImage) Toggle(e.Node);
        }

        private void Tree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space || _tree.SelectedNode == null) return;
            Toggle(_tree.SelectedNode);
            e.Handled = true;
        }

        /// <summary>點父節點＝整組勾或整組取消；點子節點後重算父節點是全勾、全不勾還是部分。</summary>
        private void Toggle(TreeNode node)
        {
            if (_phase == Phase.Scanning || _phase == Phase.Cleaning) return;

            int next = node.StateImageIndex == Checked ? Unchecked : Checked;
            if (node.Parent == null)
            {
                // 整組勾選時，有警告的項目要另外確認；不同意就只勾其他的。
                var risky = node.Nodes.Cast<TreeNode>().Where(n => ((CleanItem)n.Tag).Warning != null && n.StateImageIndex != Checked).ToList();
                bool allowRisky = next == Unchecked || risky.Count == 0 || ConfirmRisky(risky.Select(n => (CleanItem)n.Tag).ToList());
                foreach (TreeNode child in node.Nodes)
                {
                    if (next == Checked && !allowRisky && risky.Contains(child)) continue;
                    child.StateImageIndex = next;
                }
                UpdateParent(node);
            }
            else
            {
                var item = (CleanItem)node.Tag;
                if (next == Checked && item.Warning != null && !ConfirmRisky(new List<CleanItem> { item })) return;
                node.StateImageIndex = next;
                UpdateParent(node.Parent);
            }
            UpdateHeader();
        }

        private bool ConfirmRisky(List<CleanItem> items)
        {
            string text = items.Count == 1
                ? items[0].Warning
                : Lang.T("以下項目有副作用：\r\n\r\n") + string.Join("\r\n\r\n", items.Select(i => "■ " + i.Name + "\r\n" + i.Warning));
            return Msg.Show(this, text, Lang.T("確認勾選"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                   == DialogResult.Yes;
        }

        /// <summary>「全部選取」不包含有副作用的項目（隱私、Windows.old 之類），那些要自己逐項勾。</summary>
        private static bool SelectAllRule(CleanItem item)
        {
            return item.Warning == null && !item.Privacy;
        }

        private static void UpdateParent(TreeNode parent)
        {
            int on = parent.Nodes.Cast<TreeNode>().Count(n => n.StateImageIndex == Checked);
            parent.StateImageIndex = on == 0 ? Unchecked : on == parent.Nodes.Count ? Checked : Mixed;
        }

        private void SetAll(Func<CleanItem, bool> rule)
        {
            if (_phase == Phase.Scanning || _phase == Phase.Cleaning) return;
            foreach (var node in Leaves()) node.StateImageIndex = rule((CleanItem)node.Tag) ? Checked : Unchecked;
            foreach (TreeNode parent in _tree.Nodes) UpdateParent(parent);
            UpdateHeader();
        }

        /// <summary>掃描後重畫樹（容量標籤）並更新右上方的總覽。</summary>
        private void RefreshNodeTexts()
        {
            _tree.Invalidate();

            var slices = _items.GroupBy(i => i.Category).Select(gr => new OverviewPanel.Slice
            {
                Label = Lang.T(gr.Key), Bytes = gr.Sum(i => i.FoundBytes), Color = Theme.CategoryColor(gr.Key), Tag = gr.Key
            }).ToList();
            var top = _items.Select(i => new OverviewPanel.Slice
            {
                Label = i.Name, Bytes = i.FoundBytes, Color = Theme.CategoryColor(i.Category), Tag = i
            }).ToList();

            bool any = slices.Any(sl => sl.Bytes > 0);
            if (any) _overview.SetData(slices, top);
            else _overview.Clear();
            _overviewCard.Visible = any;
        }

        // ───────────────────────────── 檔案清單 ─────────────────────────────

        private void ShowFiles()
        {
            var node = _tree.SelectedNode;
            if (node == null) _source = new List<FileEntry>();
            else if (node.Tag is CleanItem) _source = ((CleanItem)node.Tag).Found;
            else _source = node.Nodes.Cast<TreeNode>().SelectMany(n => ((CleanItem)n.Tag).Found).ToList();
            ApplyView();
        }

        /// <summary>依搜尋字串過濾、依點選的欄位排序後顯示。</summary>
        private void ApplyView()
        {
            IEnumerable<FileEntry> view = _source;
            string q = _search == null ? string.Empty : _search.Text.Trim();
            if (q.Length > 0) view = view.Where(e => e.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            if (_sortColumn == 0) view = _sortDesc ? view.OrderByDescending(e => e.Path, StringComparer.OrdinalIgnoreCase) : view.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
            else if (_sortColumn == 1) view = _sortDesc ? view.OrderByDescending(e => e.Size) : view.OrderBy(e => e.Size);
            else if (_sortColumn == 2) view = _sortDesc ? view.OrderByDescending(e => e.Created) : view.OrderBy(e => e.Created);

            _shown = view.ToList();
            _maxShown = _shown.Count > 0 ? Math.Max(1, _shown.Max(e => e.Size)) : 1;
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _shown.Count;
            _list.Invalidate();
        }

        /// <summary>點欄位標題排序，再點一次反過來；大小與時間第一次點是大的在前。</summary>
        private void SortBy(int column)
        {
            if (_sortColumn == column) _sortDesc = !_sortDesc;
            else
            {
                _sortColumn = column;
                _sortDesc = column != 0;
            }
            ApplyView();
        }

        private void CopyPaths()
        {
            var paths = _list.SelectedIndices.Cast<int>().Where(i => i < _shown.Count).Select(i => _shown[i].Path).ToList();
            if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }

        private void List_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var entry = e.ItemIndex < _shown.Count ? _shown[e.ItemIndex] : null;
            var item = new ListViewItem(entry != null ? entry.Path : string.Empty);
            item.SubItems.Add(entry != null ? Format.Size(entry.Size) : string.Empty);
            item.SubItems.Add(entry != null && entry.Created > DateTime.MinValue ? entry.Created.ToString("yyyy/MM/dd HH:mm") : string.Empty);
            e.Item = item;
        }

        private FileEntry SelectedEntry()
        {
            if (_list.SelectedIndices.Count == 0) return null;
            int index = _list.SelectedIndices[0];
            return index < _shown.Count ? _shown[index] : null;
        }

        private static bool IsRealFile(FileEntry entry)
        {
            return entry != null && System.IO.File.Exists(entry.Path);
        }

        private void OpenFolder()
        {
            var entry = SelectedEntry();
            if (IsRealFile(entry)) Process.Start("explorer.exe", "/select,\"" + entry.Path + "\"");
        }

        private void ShowProperties()
        {
            var entry = SelectedEntry();
            if (IsRealFile(entry)) ShellProperties.Show(Handle, entry.Path);
        }

        /// <summary>選中的檔案加進忽略清單，並從這次的結果拿掉。</summary>
        private void ExcludeSelected()
        {
            if (_phase != Phase.Scanned || _list.SelectedIndices.Count == 0) return;

            var picked = _list.SelectedIndices.Cast<int>().Where(i => i < _shown.Count).Select(i => _shown[i])
                                              .Where(IsRealFile).ToList();
            if (picked.Count == 0) return;

            foreach (var entry in picked)
            {
                if (!_settings.Ignore.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)) _settings.Ignore.Add(entry.Path);
            }
            var set = new HashSet<FileEntry>(picked);
            foreach (var item in _items)
            {
                if (item.Found.RemoveAll(set.Contains) > 0) item.FoundBytes = item.Found.Sum(f => f.Size);
            }

            RefreshNodeTexts();
            ShowFiles();
            UpdateHeader();
            _settings.Save();
            _status.Text = string.Format(Lang.T("已將 {0} 個檔案加入忽略清單"), picked.Count);
        }

        // ───────────────────────────── 掃描與清理 ─────────────────────────────

        private void OnAction()
        {
            switch (_phase)
            {
                case Phase.Idle: Scan(); break;
                case Phase.Scanned: Clean(); break;
                default: if (_cts != null) _cts.Cancel(); break;   // 掃描或清理中按下＝停止
            }
        }

        private void SetPhase(Phase phase)
        {
            _phase = phase;
            _header.Action.Text = phase == Phase.Idle ? Lang.T("掃瞄")
                                : phase == Phase.Scanned ? Lang.T("清理")
                                : Lang.T("停止");
            _header.Action.Accent = phase == Phase.Idle ? Color.FromArgb(34, 197, 94)
                                  : phase == Phase.Scanned ? Theme.Orange
                                  : Theme.Red;
            _header.Busy = phase == Phase.Scanning || phase == Phase.Cleaning;
            if (phase == Phase.Scanning) _live.Start(); else _live.Stop();
            UpdateHeader();
        }

        /// <summary>背景執行緒回報進度；每 100 毫秒最多更新一次，不然 UI 執行緒會被訊息淹沒。</summary>
        private Action<string> Throttled(string prefix)
        {
            var progress = new Progress<string>(text => _status.Text = prefix + text);
            int last = 0;
            return text =>
            {
                int now = Environment.TickCount;
                if (now - last < 100) return;
                last = now;
                ((IProgress<string>)progress).Report(text);
            };
        }

        private async void Scan()
        {
            var items = CheckedItems();
            if (items.Count == 0)
            {
                Msg.Show(this, Lang.T("請先在左邊勾選要掃描的項目。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            foreach (var item in _items) item.ResetResult();
            RefreshNodeTexts();
            ShowFiles();
            _lastResult = null;
            _lastFreed = 0;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var options = new ScanOptions { MinAgeHours = _settings.MinAgeHours, Ignore = _settings.Ignore.ToList() };
            var report = Throttled(Lang.T("掃描中："));
            SetPhase(Phase.Scanning);

            var watch = Stopwatch.StartNew();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var item in items)
                    {
                        if (token.IsCancellationRequested) break;
                        Cleaner.Scan(item, options, token, report);
                    }
                });
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("掃描時發生錯誤：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            bool cancelled = token.IsCancellationRequested;
            RefreshNodeTexts();
            ShowFiles();

            int denied = items.Sum(i => i.Denied);
            int count = items.Sum(i => i.Found.Count);
            _status.Text = string.Format(Lang.T("{0}，{1:N0} 個項目，用時 {2:0.0} 秒{3}"),
                cancelled ? Lang.T("已停止掃描") : Lang.T("掃描完成"), count, watch.Elapsed.TotalSeconds,
                denied > 0 ? string.Format(Lang.T("；{0:N0} 個資料夾沒有權限讀取"), denied) : string.Empty);

            SetPhase(count > 0 ? Phase.Scanned : Phase.Idle);
            if (count == 0) _lastResult = cancelled ? Lang.T("已停止掃描") : Lang.T("沒有找到可清理的檔案");
            UpdateHeader();
        }

        private async void Clean()
        {
            var items = CheckedItems().Where(i => i.Found.Count > 0).ToList();

            // DISM、Windows.old 沒有系統管理員權限一定失敗，先拿掉並說明。
            var needAdmin = items.Where(i => !_isAdmin && i.NeedsAdmin && (i.Kind == ItemKind.Command || i.Kind == ItemKind.FolderTotal)).ToList();
            items = items.Except(needAdmin).ToList();

            long count = items.Sum(i => i.Count);
            long bytes = items.Sum(i => i.FoundBytes);
            if (count == 0)
            {
                if (needAdmin.Count > 0) ShowNeedAdmin(needAdmin);
                return;
            }

            bool recycle = _settings.UseRecycleBin;
            string how = recycle
                ? Lang.T("檔案會送進資源回收筒，可以還原；空間要等清空資源回收筒才會釋放。")
                : Lang.T("刪除後無法復原（不會進資源回收筒）。");
            if (items.Any(i => i.Kind != ItemKind.Files && i.Kind != ItemKind.BrokenShortcuts))
            {
                how += Lang.T("\r\n資源回收筒、Windows.old、元件存放區、執行紀錄一律直接清除。");
            }
            string skipped = needAdmin.Count > 0
                ? Lang.T("\r\n\r\n以下項目需要系統管理員權限，這次略過：") + string.Join(Lang.T("、"), needAdmin.Select(i => i.Name))
                : string.Empty;

            var answer = Msg.Show(this,
                string.Format(Lang.T("將清理 {0:N0} 個項目，共 {1}。\r\n\r\n{2}{3}\r\n\r\n確定要清理？"), count, Format.Size(bytes), how, skipped),
                Lang.T("確認清理"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var report = Throttled(Lang.T("刪除中："));
            SetPhase(Phase.Cleaning);

            bool backup = _settings.BackupBeforeClean && !recycle;
            BackupResult saved = null;
            CleanResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    if (backup) saved = Backup.Create(items, token, report);
                    if (token.IsCancellationRequested) return new CleanResult();
                    return Cleaner.Clean(items, recycle, token, report);
                });
            }
            catch (Exception ex)
            {
                result = new CleanResult();
                Msg.Show(this, Lang.T("清理時發生錯誤：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (result.Deleted > 0)
            {
                _settings.History.Add(new HistoryEntry { Time = DateTime.Now, Files = result.Deleted, Bytes = result.Bytes, Failed = result.Failed });
                _settings.Save();
            }

            _lastFreed = recycle ? 0 : result.Bytes;
            _lastResult = string.Format(Lang.T("已{0} {1:N0} 個項目，{2} {3}{4}"), recycle ? Lang.T("送進資源回收筒") : Lang.T("清理"), result.Deleted,
                recycle ? Lang.T("共") : Lang.T("釋放"), Format.Size(result.Bytes),
                result.Failed > 0 ? string.Format(Lang.T("；{0:N0} 個使用中或沒有權限，已略過"), result.Failed) : string.Empty);
            if (saved != null && saved.Files > 0)
            {
                _lastResult += string.Format(Lang.T("；已備份 {0:N0} 個檔案（{1}）{2}"), saved.Files, Format.Size(saved.Bytes),
                    saved.Skipped > 0 ? string.Format(Lang.T("，{0:N0} 個太大或讀不到沒備份"), saved.Skipped) : string.Empty);
            }
            _status.Text = token.IsCancellationRequested ? Lang.T("已停止清理。") + _lastResult : _lastResult;

            foreach (var item in _items) item.ResetResult();
            RefreshNodeTexts();
            ShowFiles();
            SetPhase(Phase.Idle);
            CheckLowSpace();
        }

        private void ShowNeedAdmin(List<CleanItem> items)
        {
            Msg.Show(this, Lang.T("以下項目需要系統管理員權限才能清理：\r\n") + string.Join(Lang.T("、"), items.Select(i => i.Name))
                + Lang.T("\r\n\r\n請用「檔案 → 以系統管理員身分重新啟動」。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CheckLowSpace()
        {
            if (_drives != null) _drives.RefreshDrives();   // 清理後空間變了，卡片也跟著更新
            string note = Background.LowSpaceNote().Trim();
            _lowSpace.Text = note.Length > 0 ? Lang.T("系統磁碟空間不足：") + note + Lang.T("，建議掃瞄清理，或用「工具 → 大檔案分析」找出佔空間的檔案") : string.Empty;
        }

        private void UpdateHeader()
        {
            if (_header == null) return;

            _header.SetStats(_settings.TodayBytes, _settings.TotalBytes);
            switch (_phase)
            {
                case Phase.Scanning:
                    // 背景執行緒正在累加，這裡每 120 毫秒讀一次總數，大字跟著往上跳。
                    _header.SetMain(_items.Sum(i => i.FoundBytes), null, Lang.T("正在掃描勾選的項目…　按「停止」可以中斷"));
                    break;
                case Phase.Cleaning:
                    _header.SetMain(null, Lang.T("清理中…"), Lang.T("正在刪除檔案，按「停止」可以中斷"));
                    break;
                case Phase.Scanned:
                    var picked = CheckedItems();
                    _header.SetMain(picked.Sum(i => i.FoundBytes), null,
                        string.Format(Lang.T("找到 {0:N0} 個可清理的項目，點擊「清理」{1}（取消勾選的項目不會清）"),
                            picked.Sum(i => i.Count), _settings.UseRecycleBin ? Lang.T("送進資源回收筒") : Lang.T("刪除")));
                    break;
                default:
                    _header.SetMain(null, _lastFreed > 0 ? Lang.T("已釋放 ") + Format.Size(_lastFreed) : "0 MB",
                        _lastResult ?? Lang.T("點擊「掃瞄」掃瞄您的系統垃圾檔案"));
                    break;
            }
        }

        // ───────────────────────────── 對話框 ─────────────────────────────

        private bool Busy()
        {
            if (_phase != Phase.Scanning && _phase != Phase.Cleaning) return false;
            Msg.Show(this, Lang.T("請等掃描或清理結束（或按「停止」）再修改設定。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        private void ShowHistory()
        {
            using (var dlg = new HistoryDialog(_settings.History))
            {
                dlg.ShowDialog(this);
                if (dlg.Cleared) _settings.Save();
            }
            UpdateHeader();
        }

        private void ShowIgnoreList()
        {
            if (Busy()) return;
            using (var dlg = new IgnoreListDialog(_settings.Ignore))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _settings.Ignore = dlg.Result;
                _settings.Save();
            }
            _status.Text = Lang.T("忽略清單已更新，下次掃描生效");
        }

        private void ShowCustomFolders()
        {
            if (Busy()) return;
            using (var dlg = new CustomFoldersDialog(_settings.CustomFolders))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _settings.CustomFolders = dlg.Result;
                _settings.Save();
            }

            // 自訂項目是整組重建的，舊的掃描結果作廢。
            LoadItems();
            if (_phase == Phase.Scanned) SetPhase(Phase.Idle);
            _status.Text = Lang.T("自訂資料夾已更新");
        }

        private void ShowOptions()
        {
            if (Busy()) return;
            using (var dlg = new OptionsDialog(_settings))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                bool rootsChanged = !dlg.DevRoots.SequenceEqual(_settings.DevRoots, StringComparer.OrdinalIgnoreCase);
                bool autoChanged = dlg.AutoClean != _settings.AutoClean || dlg.AutoCleanDay != _settings.AutoCleanDay
                                || dlg.AutoCleanTime != _settings.AutoCleanTime;
                bool lowChanged = dlg.LowSpaceCheck != _settings.LowSpaceCheck;

                _settings.MinAgeHours = dlg.MinAgeHours;
                _settings.UseRecycleBin = dlg.UseRecycleBin;
                _settings.DevRoots = dlg.DevRoots;
                _settings.AutoClean = dlg.AutoClean;
                _settings.AutoCleanDay = dlg.AutoCleanDay;
                _settings.AutoCleanTime = dlg.AutoCleanTime;
                _settings.LowSpaceCheck = dlg.LowSpaceCheck;
                bool appearanceChanged = dlg.Appearance != _settings.Appearance;
                bool languageChanged = dlg.Language != _settings.Language;
                _settings.Appearance = dlg.Appearance;
                _settings.Language = dlg.Language;
                _settings.MinimizeToTray = dlg.MinimizeToTray;
                _settings.CloseToTray = dlg.CloseToTray;
                _settings.BackupBeforeClean = dlg.BackupBeforeClean;
                _settings.BackupDays = dlg.BackupDays;
                UpdateTrayVisibility();
                if (appearanceChanged) ApplyAppearance();

                var errors = new List<string>();
                string error;
                if (autoChanged && (error = Scheduler.SetAutoClean(dlg.AutoClean, dlg.AutoCleanDay, dlg.AutoCleanTime)) != null)
                {
                    errors.Add(Lang.T("自動清理：") + error);
                    _settings.AutoClean = false;
                }
                if (lowChanged && (error = Scheduler.SetLowSpaceCheck(dlg.LowSpaceCheck)) != null)
                {
                    errors.Add(Lang.T("空間檢查：") + error);
                    _settings.LowSpaceCheck = false;
                }
                _settings.Save();

                if (errors.Count > 0)
                {
                    Msg.Show(this, Lang.T("設定排程失敗：\r\n") + string.Join("\r\n", errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // 開發專案資料夾改了，__pycache__ 那兩項的搜尋範圍跟著變，舊結果作廢。
                if (rootsChanged)
                {
                    LoadItems();
                    if (_phase == Phase.Scanned) SetPhase(Phase.Idle);
                }
                UpdateHeader();
                _status.Text = Lang.T("選項已儲存");

                if (languageChanged && Msg.Show(this, Lang.T("語言設定重新啟動後生效。要現在重新啟動嗎？"), Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    SaveChecks();
                    _settings.Save();
                    _exiting = true;
                    Process.Start(Application.ExecutablePath);
                    Close();
                }
            }
        }

        private void ShowAnalyzer(int tab, string folder = null)
        {
            var form = new AnalyzerForm(tab);
            form.Show(this);
            if (folder != null) form.AnalyzeFolder(folder);
        }

        private void ExportCsv()
        {
            var rows = _items.SelectMany(i => i.Found.Select(f => new { Item = i, File = f })).ToList();
            if (rows.Count == 0)
            {
                Msg.Show(this, Lang.T("還沒有掃描結果可以匯出。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog { Filter = Lang.T("CSV 檔 (*.csv)|*.csv"), FileName = Lang.T("掃描結果_") + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                Func<string, string> q = v => "\"" + (v ?? string.Empty).Replace("\"", "\"\"") + "\"";
                var lines = new List<string> { Lang.T("分類,項目,路徑,大小(位元組),建立時間") };
                lines.AddRange(rows.Select(r => string.Join(",", q(r.Item.Category), q(r.Item.Name), q(r.File.Path),
                    r.File.Size.ToString(), q(r.File.Created > DateTime.MinValue ? r.File.Created.ToString("yyyy/MM/dd HH:mm:ss") : string.Empty))));

                try
                {
                    // 帶 BOM 的 UTF-8，Excel 直接開中文才不會亂碼。
                    System.IO.File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(true));
                    _status.Text = string.Format(Lang.T("已匯出 {0:N0} 筆：{1}"), rows.Count, dlg.FileName);
                }
                catch (Exception ex)
                {
                    Msg.Show(this, Lang.T("匯出失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void RestartAsAdmin()
        {
            try
            {
                SaveChecks();
                _settings.Save();
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                Close();
            }
            catch (Win32Exception)
            {
                // 使用者在 UAC 視窗按了「否」。
            }
        }
    }
}
