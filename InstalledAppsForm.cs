using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 已安裝的程式：名稱、發行者、版本、安裝日期、安裝路徑、大小，可以解除安裝、修改／修復、開資料夾。
    /// 資料夾大小在背景一個一個算，算好就填進表格。
    /// </summary>
    public class InstalledAppsForm : Form
    {
        private const int ColName = 0, ColPublisher = 1, ColVersion = 2, ColDate = 3, ColSize = 4, ColFolder = 5, ColPath = 6;

        private List<InstalledApp> _all = new List<InstalledApp>();
        private List<InstalledApp> _shown = new List<InstalledApp>();
        private CancellationTokenSource _sizing;
        private int _sortColumn = ColName;
        private bool _sortDesc;

        private readonly ListView _list;
        private readonly TextBox _search;
        private readonly CheckBox _showSystem;
        private readonly Button _uninstall, _modify, _folder, _web, _remove;
        private readonly ToolStripStatusLabel _status;

        public InstalledAppsForm()
        {
            Text = Lang.T("已安裝的程式");
            Font = new Font("Microsoft JhengHei UI", 9.5f);
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1180, 700);
            MinimumSize = new Size(820, 460);

            // ── 上方：搜尋與篩選 ──
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 7, 6, 0), WrapContents = false };
            _search = new TextBox { Width = 320 };
            _showSystem = new CheckBox { Text = Lang.T("顯示系統元件與更新"), AutoSize = true, Margin = new Padding(16, 4, 0, 0) };
            var refresh = new Button { Text = Lang.T("重新整理"), Width = 90, Height = 27, Margin = new Padding(16, 0, 0, 0) };
            var export = new Button { Text = Lang.T("匯出 CSV…"), Width = 100, Height = 27 };
            top.Controls.Add(new Label { Text = Lang.T("搜尋"), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            top.Controls.Add(_search);
            top.Controls.Add(_showSystem);
            top.Controls.Add(refresh);
            top.Controls.Add(export);

            // ── 中間：清單（虛擬模式） ──
            _list = new ThemedListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
                VirtualMode = true, MultiSelect = false
            };
            _list.Columns.Add(Lang.T("名稱"), 300);
            _list.Columns.Add(Lang.T("發行者"), 170);
            _list.Columns.Add(Lang.T("版本"), 110);
            _list.Columns.Add(Lang.T("安裝日期"), 90);
            _list.Columns.Add(Lang.T("大小（登錄）"), 90, HorizontalAlignment.Right);
            _list.Columns.Add(Lang.T("資料夾大小"), 90, HorizontalAlignment.Right);
            _list.Columns.Add(Lang.T("安裝路徑"), 320);
            _list.RetrieveVirtualItem += List_RetrieveVirtualItem;
            _list.ColumnClick += (s, e) => SortBy(e.Column);
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _list.DoubleClick += delegate { OpenFolder(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("解除安裝…"), null, delegate { Uninstall(); });
            menu.Items.Add(Lang.T("修改／修復…"), null, delegate { Modify(); });
            menu.Items.Add(Lang.T("開啟安裝資料夾"), null, delegate { OpenFolder(); });
            menu.Items.Add(Lang.T("開啟發行者網站"), null, delegate { OpenWeb(); });
            menu.Items.Add(Lang.T("複製資訊"), null, delegate { CopyInfo(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("從清單移除殘留項目…"), null, delegate { RemoveEntry(); });
            _list.ContextMenuStrip = menu;

            // ── 下方：操作按鈕 ──
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(6, 7, 6, 0) };
            _uninstall = new Button { Text = Lang.T("解除安裝…"), Width = 110, Height = 30 };
            _modify = new Button { Text = Lang.T("修改／修復…"), Width = 110, Height = 30 };
            _folder = new Button { Text = Lang.T("開啟安裝資料夾"), Width = 130, Height = 30 };
            _web = new Button { Text = Lang.T("發行者網站"), Width = 100, Height = 30 };
            _remove = new Button { Text = Lang.T("移除殘留項目…"), Width = 130, Height = 30 };
            bottom.Controls.AddRange(new Control[] { _uninstall, _modify, _folder, _web, _remove });

            var status = new StatusStrip();
            _status = new ToolStripStatusLabel(Lang.T("讀取中…")) { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            status.Items.Add(_status);

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(status);

            _search.TextChanged += delegate { ApplyView(); };
            _showSystem.CheckedChanged += delegate { ApplyView(); };
            refresh.Click += delegate { Reload(); };
            export.Click += delegate { ExportCsv(); };
            _uninstall.Click += delegate { Uninstall(); };
            _modify.Click += delegate { Modify(); };
            _folder.Click += delegate { OpenFolder(); };
            _web.Click += delegate { OpenWeb(); };
            _remove.Click += delegate { RemoveEntry(); };

            Load += delegate { Theme.Apply(this); Reload(); };
            FormClosing += delegate { if (_sizing != null) _sizing.Cancel(); };
        }

        // ───────────────────────────── 資料 ─────────────────────────────

        private void Reload()
        {
            if (_sizing != null) _sizing.Cancel();

            var selected = Selected();
            _all = InstalledApps.Load();
            ApplyView();
            if (selected != null) Select(_shown.FindIndex(a => a.Name == selected.Name && a.Version == selected.Version));
            ComputeSizes();
        }

        /// <summary>背景依序算每個程式的資料夾大小；同一個資料夾只算一次。</summary>
        private async void ComputeSizes()
        {
            var cts = new CancellationTokenSource();
            _sizing = cts;
            var token = cts.Token;
            var cache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var todo = _all.ToList();
            int done = 0;

            foreach (var app in todo)
            {
                if (token.IsCancellationRequested) return;
                done++;

                if (string.IsNullOrEmpty(app.InstallLocation) || !Directory.Exists(app.InstallLocation))
                {
                    app.FolderSize = -2;
                    continue;
                }

                long size;
                if (!cache.TryGetValue(app.InstallLocation, out size))
                {
                    string dir = app.InstallLocation;
                    size = await Task.Run(() => InstalledApps.FolderSize(dir, token));
                    if (token.IsCancellationRequested) return;
                    cache[dir] = size;
                }
                app.FolderSize = size;

                _status.Text = string.Format(Lang.T("{0}　│　計算資料夾大小 {1}/{2}"), Summary(), done, todo.Count);
                int index = _shown.IndexOf(app);
                if (index >= 0) _list.RedrawItems(index, index, true);
            }

            if (_sortColumn == ColFolder) ApplyView();
            _status.Text = Summary() + Lang.T("　│　資料夾大小計算完成");
        }

        private string Summary()
        {
            long folders = _shown.Where(a => a.FolderSize > 0).Sum(a => a.FolderSize);
            return string.Format(Lang.T("共 {0:N0} 個程式{1}，資料夾合計 {2}"), _shown.Count,
                _showSystem.Checked ? Lang.T("（含系統元件）") : string.Empty, Format.Size(folders));
        }

        private void ApplyView()
        {
            var selected = Selected();
            string q = _search.Text.Trim();

            IEnumerable<InstalledApp> view = _all.Where(a => _showSystem.Checked || !a.SystemComponent);
            if (q.Length > 0)
            {
                view = view.Where(a => Contains(a.Name, q) || Contains(a.Publisher, q) || Contains(a.InstallLocation, q));
            }

            Func<InstalledApp, object> key;
            switch (_sortColumn)
            {
                case ColPublisher: key = a => a.Publisher ?? string.Empty; break;
                case ColVersion: key = a => a.Version ?? string.Empty; break;
                case ColDate: key = a => a.InstallDate ?? DateTime.MinValue; break;
                case ColSize: key = a => a.RegistrySize; break;
                case ColFolder: key = a => a.FolderSize; break;
                case ColPath: key = a => a.InstallLocation ?? string.Empty; break;
                default: key = a => a.Name; break;
            }
            var comparer = Comparer<object>.Create((x, y) =>
                x is string && y is string ? StringComparer.CurrentCultureIgnoreCase.Compare((string)x, (string)y) : Comparer<object>.Default.Compare(x, y));
            view = _sortDesc ? view.OrderByDescending(key, comparer) : view.OrderBy(key, comparer);

            _shown = view.ToList();
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _shown.Count;
            _list.Invalidate();
            if (selected != null) Select(_shown.IndexOf(selected));
            UpdateButtons();
            _status.Text = Summary();
        }

        private static bool Contains(string text, string q)
        {
            return text != null && text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>點欄位標題排序；大小與日期第一次點是大的（新的）在前。</summary>
        private void SortBy(int column)
        {
            if (_sortColumn == column) _sortDesc = !_sortDesc;
            else
            {
                _sortColumn = column;
                _sortDesc = column == ColSize || column == ColFolder || column == ColDate;
            }
            ApplyView();
        }

        private void List_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var app = e.ItemIndex < _shown.Count ? _shown[e.ItemIndex] : null;
            var item = new ListViewItem(app == null ? string.Empty : app.Name);
            if (app != null)
            {
                item.SubItems.Add(app.Publisher ?? string.Empty);
                item.SubItems.Add(app.Version ?? string.Empty);
                item.SubItems.Add(app.InstallDate.HasValue ? app.InstallDate.Value.ToString("yyyy/MM/dd") : string.Empty);
                item.SubItems.Add(app.RegistrySize > 0 ? Format.Size(app.RegistrySize) : string.Empty);
                item.SubItems.Add(app.FolderSize >= 0 ? Format.Size(app.FolderSize) : app.FolderSize == -1 ? Lang.T("計算中…") : string.Empty);
                item.SubItems.Add(app.InstallLocation == null ? string.Empty : app.InstallLocation + (app.LocationGuessed ? Lang.T("（推測）") : string.Empty));

                // 解除安裝程式已經不在：多半是沒移除乾淨的殘留項目，標灰。
                if (!app.UninstallerExists) item.ForeColor = Color.Gray;
                else if (app.SystemComponent) item.ForeColor = Color.FromArgb(90, 90, 140);
            }
            e.Item = item;
        }

        private InstalledApp Selected()
        {
            if (_list.SelectedIndices.Count == 0) return null;
            int i = _list.SelectedIndices[0];
            return i < _shown.Count ? _shown[i] : null;
        }

        private void Select(int index)
        {
            if (index < 0 || index >= _shown.Count) return;
            _list.SelectedIndices.Add(index);
            _list.EnsureVisible(index);
        }

        private void UpdateButtons()
        {
            var app = Selected();
            _uninstall.Enabled = app != null && app.UninstallerExists && (app.UninstallString != null || app.ProductCode != null);
            _modify.Enabled = app != null && app.CanModify;
            _folder.Enabled = app != null && app.InstallLocation != null && Directory.Exists(app.InstallLocation);
            _web.Enabled = app != null && IsWebUrl(app.Url);
            _remove.Enabled = app != null && !app.UninstallerExists;
        }

        private static bool IsWebUrl(string url)
        {
            return url != null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        // ───────────────────────────── 操作 ─────────────────────────────

        private async void Uninstall()
        {
            var app = Selected();
            if (app == null || !_uninstall.Enabled) return;

            var answer = Msg.Show(this,
                string.Format(Lang.T("要解除安裝「{0}」嗎？\r\n\r\n會啟動它自己的解除安裝程式（可能會要求系統管理員權限），\r\n請依畫面指示完成。"), app.Name),
                Lang.T("解除安裝"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            await RunAndRefresh(() => InstalledApps.StartUninstall(app), Lang.T("解除安裝 ") + app.Name);
        }

        private async void Modify()
        {
            var app = Selected();
            if (app == null || !app.CanModify) return;
            await RunAndRefresh(() => InstalledApps.StartModify(app), Lang.T("修改／修復 ") + app.Name);
        }

        /// <summary>啟動安裝程式，等它結束後重新讀清單（看看是不是真的移除了）。</summary>
        private async Task RunAndRefresh(Func<Process> start, string what)
        {
            Process p;
            try
            {
                p = start();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _status.Text = Lang.T("已取消（UAC 選了「否」）");
                return;
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("無法啟動：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _status.Text = what + Lang.T("：等待安裝程式結束…");
            if (p != null)
            {
                // 有些解除安裝程式會再開一個子程序後自己先結束，所以結束後多等一下再重新整理。
                await Task.Run(() => { using (p) p.WaitForExit(); });
                await Task.Delay(1500);
            }
            Reload();
            _status.Text = what + Lang.T("：安裝程式已結束，清單已重新整理");
        }

        private void OpenFolder()
        {
            var app = Selected();
            if (app != null && app.InstallLocation != null && Directory.Exists(app.InstallLocation))
            {
                Process.Start("explorer.exe", "\"" + app.InstallLocation + "\"");
            }
        }

        private void OpenWeb()
        {
            var app = Selected();
            if (app != null && IsWebUrl(app.Url)) Process.Start(new ProcessStartInfo(app.Url) { UseShellExecute = true });
        }

        private void CopyInfo()
        {
            var app = Selected();
            if (app == null) return;
            Clipboard.SetText(string.Join(Environment.NewLine, new[]
            {
                Lang.T("名稱：") + app.Name,
                Lang.T("發行者：") + app.Publisher,
                Lang.T("版本：") + app.Version,
                Lang.T("安裝日期：") + (app.InstallDate.HasValue ? app.InstallDate.Value.ToString("yyyy/MM/dd") : string.Empty),
                Lang.T("安裝路徑：") + app.InstallLocation,
                Lang.T("大小：") + (app.FolderSize >= 0 ? Format.Size(app.FolderSize) : app.RegistrySize > 0 ? Format.Size(app.RegistrySize) : string.Empty),
                Lang.T("解除安裝：") + app.UninstallString,
                Lang.T("登錄位置：") + app.RegPath
            }));
            _status.Text = Lang.T("已複製「") + app.Name + Lang.T("」的資訊");
        }

        /// <summary>
        /// 解除安裝程式已經不在的殘留項目：刪掉登錄裡那一筆，讓它從「應用程式」清單消失。
        /// 刪之前先備份成 .reg；所有使用者的項目要系統管理員權限。
        /// </summary>
        private void RemoveEntry()
        {
            var app = Selected();
            if (app == null) return;
            if (app.UninstallerExists)
            {
                Msg.Show(this, Lang.T("這個程式的解除安裝程式還在，請用「解除安裝」正常移除。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = Msg.Show(this,
                string.Format(Lang.T("「{0}」的解除安裝程式已經不存在：\r\n{1}\r\n\r\n要把它從已安裝清單中移除嗎？\r\n") +
                              Lang.T("只會刪除登錄裡的這一筆（不會刪任何檔案），刪除前會先備份成 .reg 檔，雙擊就能還原。"),
                              app.Name, app.UninstallString),
                Lang.T("移除殘留項目"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            try
            {
                string backup = InstalledApps.RemoveEntry(app);
                Reload();
                _status.Text = Lang.T("已移除「") + app.Name + Lang.T("」，備份：") + backup;
            }
            catch (UnauthorizedAccessException)
            {
                Msg.Show(this, Lang.T("「所有使用者」的項目需要系統管理員權限才能移除。\r\n請在主視窗用「檔案 → 以系統管理員身分重新啟動」。"),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("移除失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExportCsv()
        {
            using (var dlg = new SaveFileDialog { Filter = Lang.T("CSV 檔 (*.csv)|*.csv"), FileName = Lang.T("已安裝的程式_") + DateTime.Now.ToString("yyyyMMdd") + ".csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                Func<string, string> q = v => "\"" + (v ?? string.Empty).Replace("\"", "\"\"") + "\"";
                var lines = new List<string> { Lang.T("名稱,發行者,版本,安裝日期,大小(登錄,位元組),資料夾大小(位元組),安裝路徑,範圍,解除安裝命令") };
                lines.AddRange(_shown.Select(a => string.Join(",",
                    q(a.Name), q(a.Publisher), q(a.Version),
                    a.InstallDate.HasValue ? a.InstallDate.Value.ToString("yyyy/MM/dd") : string.Empty,
                    a.RegistrySize > 0 ? a.RegistrySize.ToString() : string.Empty,
                    a.FolderSize >= 0 ? a.FolderSize.ToString() : string.Empty,
                    q(a.InstallLocation), q(a.Scope), q(a.UninstallString))));
                try
                {
                    File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(true));
                    _status.Text = string.Format(Lang.T("已匯出 {0:N0} 筆：{1}"), _shown.Count, dlg.FileName);
                }
                catch (Exception ex)
                {
                    Msg.Show(this, Lang.T("匯出失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }
}
