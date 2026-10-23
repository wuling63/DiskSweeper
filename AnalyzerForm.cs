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
    /// 磁碟分析工具：大檔案分析（資料夾大小樹＋樹狀圖＋最大檔案）、重複檔案、空資料夾。
    /// 這裡刪除一律送進資源回收筒。
    /// </summary>
    public class AnalyzerForm : Form
    {
        private CancellationTokenSource _cts;
        private ToolStripStatusLabel _status;

        // 大檔案分析
        private TextBox _sizePath;
        private Button _sizeGo;
        private TreeView _sizeTree;
        private TreemapPanel _treemap;
        private ListView _bigList;
        private readonly List<BigFile> _bigFiles = new List<BigFile>();
        private List<BigFile> _bigShown = new List<BigFile>();

        // 重複檔案
        private TextBox _dupPath;
        private NumericUpDown _dupMin;
        private Button _dupGo;
        private ListView _dupList;

        // 空資料夾
        private TextBox _emptyPath;
        private Button _emptyGo;
        private ListView _emptyList;

        public AnalyzerForm(int tab)
        {
            Text = Lang.T("磁碟分析工具");
            Font = new Font("Microsoft JhengHei UI", 9.5f);
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(800, 500);

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            tabs.TabPages.Add(BuildSizePage());
            tabs.TabPages.Add(BuildDuplicatePage());
            tabs.TabPages.Add(BuildEmptyPage());
            tabs.SelectedIndex = Math.Max(0, Math.Min(2, tab));

            var status = new StatusStrip();
            _status = new ToolStripStatusLabel(Lang.T("就緒")) { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            status.Items.Add(_status);

            Controls.Add(tabs);
            Controls.Add(status);

            FormClosing += delegate { if (_cts != null) _cts.Cancel(); };
            Load += delegate { Theme.Apply(this); };
        }

        // ───────────────────────────── 共用 ─────────────────────────────

        /// <summary>上方的「資料夾 [____] [瀏覽] [開始]」列。</summary>
        private Panel PathBar(string defaultPath, string action, out TextBox path, out Button go, params Control[] extra)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 6, 4, 0), WrapContents = false };
            var box = new TextBox { Text = defaultPath, Width = 420 };
            var browse = new Button { Text = Lang.T("瀏覽…"), Width = 70, Height = 27 };
            var run = new Button { Text = action, Width = 90, Height = 27 };
            browse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = box.Text })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            bar.Controls.Add(new Label { Text = Lang.T("資料夾"), AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            bar.Controls.Add(box);
            bar.Controls.Add(browse);
            foreach (var c in extra) bar.Controls.Add(c);
            bar.Controls.Add(run);
            path = box;
            go = run;
            return bar;
        }

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

        /// <summary>開始一個背景工作；正在跑的時候按同一顆鈕＝停止。</summary>
        private bool BeginWork(Button button, string idleText)
        {
            if (_cts != null)
            {
                if (button.Text == Lang.T("停止")) _cts.Cancel();
                else Msg.Show(this, Lang.T("另一個分頁的工作還在執行，請等它結束或先按「停止」。"), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            _cts = new CancellationTokenSource();
            button.Text = Lang.T("停止");
            button.Tag = idleText;
            return true;
        }

        private void EndWork(Button button)
        {
            button.Text = (string)button.Tag;
            _cts = null;
        }

        private bool CheckFolder(TextBox box)
        {
            if (Directory.Exists(box.Text)) return true;
            Msg.Show(this, Lang.T("找不到這個資料夾：") + box.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private static void OpenFolder(string path)
        {
            if (File.Exists(path) || Directory.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        /// <summary>送進資源回收筒，回傳真的不見了的那些。</summary>
        private List<string> Recycle(List<string> paths, string what)
        {
            if (paths.Count == 0) return new List<string>();
            var answer = Msg.Show(this, string.Format(Lang.T("將 {0:N0} 個{1}送進資源回收筒？\r\n（之後可以從資源回收筒還原）"), paths.Count, what),
                Lang.T("確認"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return new List<string>();

            foreach (var batch in paths.Select((p, i) => new { p, i }).GroupBy(x => x.i / 200, x => x.p))
            {
                RecycleBin.Send(batch);
            }
            var gone = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            _status.Text = string.Format(Lang.T("已將 {0:N0} 個{1}送進資源回收筒{2}"), gone.Count, what,
                gone.Count < paths.Count ? string.Format(Lang.T("，{0:N0} 個沒有移動（使用中或沒有權限）"), paths.Count - gone.Count) : string.Empty);
            return gone;
        }

        // ───────────────────────────── 大檔案分析 ─────────────────────────────

        private TabPage BuildSizePage()
        {
            var page = new TabPage(Lang.T("大檔案分析"));
            string drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var bar = PathBar(drive, Lang.T("分析"), out _sizePath, out _sizeGo);

            _sizeTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true };
            _sizeTree.BeforeExpand += (s, e) => FillChildren(e.Node);
            _sizeTree.AfterSelect += delegate { ShowSizeNode(); };

            _treemap = new TreemapPanel { Dock = DockStyle.Fill, Font = Font };
            _treemap.ItemClicked += tag => SelectDir(tag as DirNode);

            _bigList = new ThemedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            _bigList.Columns.Add(Lang.T("最大的檔案（目前選的資料夾底下）"), 560);
            _bigList.Columns.Add(Lang.T("大小"), 90, HorizontalAlignment.Right);
            _bigList.Columns.Add(Lang.T("修改時間"), 130);
            _bigList.DoubleClick += delegate { if (_bigList.SelectedItems.Count > 0) OpenFolder(_bigList.SelectedItems[0].Text); };
            var menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("開啟資料夾"), null, delegate { if (_bigList.SelectedItems.Count > 0) OpenFolder(_bigList.SelectedItems[0].Text); });
            menu.Items.Add(Lang.T("送進資源回收筒…"), null, delegate { RecycleBigFiles(); });
            _bigList.ContextMenuStrip = menu;

            var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            right.Panel1.Controls.Add(_treemap);
            right.Panel2.Controls.Add(_bigList);

            var split = new SplitContainer { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(_sizeTree);
            split.Panel2.Controls.Add(right);

            page.Controls.Add(split);
            page.Controls.Add(bar);

            bool placed = false;
            page.Layout += delegate
            {
                if (placed || split.Width < 600 || right.Height < 300) return;
                placed = true;
                split.SplitterDistance = 340;
                right.SplitterDistance = right.Height * 55 / 100;
            };

            _sizeGo.Click += delegate { AnalyzeSizes(); };
            return page;
        }

        /// <summary>從外面指定資料夾（例如主頁的磁碟卡片）並立刻開始分析。</summary>
        public void AnalyzeFolder(string folder)
        {
            _sizePath.Text = folder;
            AnalyzeSizes();
        }

        private async void AnalyzeSizes()
        {
            if (!BeginWork(_sizeGo, Lang.T("分析"))) return;
            if (!CheckFolder(_sizePath)) { EndWork(_sizeGo); return; }

            string root = _sizePath.Text;
            var token = _cts.Token;
            var report = Throttled(Lang.T("分析中："));
            var watch = Stopwatch.StartNew();

            DirNode tree = null;
            try
            {
                tree = await Task.Run(() => Analysis.BuildTree(root, _bigFiles, token, report));
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("分析時發生錯誤：") + ex.Message, Text);
            }

            bool cancelled = token.IsCancellationRequested;
            EndWork(_sizeGo);
            if (tree == null) return;

            _sizeTree.BeginUpdate();
            _sizeTree.Nodes.Clear();
            var rootNode = MakeNode(tree, tree.Size);
            _sizeTree.Nodes.Add(rootNode);
            rootNode.Expand();
            _sizeTree.EndUpdate();
            _sizeTree.SelectedNode = rootNode;

            _status.Text = string.Format(Lang.T("{0}：{1}，{2:N0} 個檔案，用時 {3:0.0} 秒（沒權限讀的資料夾不計）"),
                cancelled ? Lang.T("已停止，以下是部分結果") : Lang.T("分析完成"), Format.Size(tree.Size), tree.FileCount, watch.Elapsed.TotalSeconds);
        }

        /// <summary>節點文字：名稱、大小、佔上一層的比例。子節點等展開時才建（整顆 C 槽一次建會很慢）。</summary>
        private static TreeNode MakeNode(DirNode dir, long parentSize)
        {
            double pct = parentSize > 0 ? dir.Size * 100.0 / parentSize : 100;
            var node = new TreeNode(string.Format(Lang.T("{0}　{1}　({2:0.#}%)"), dir.Name, Format.Size(dir.Size), pct)) { Tag = dir };
            if (dir.Children.Count > 0) node.Nodes.Add(new TreeNode("…"));
            return node;
        }

        private static void FillChildren(TreeNode node)
        {
            var dir = node.Tag as DirNode;
            if (dir == null || node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;
            node.Nodes.Clear();
            foreach (var child in dir.Children) node.Nodes.Add(MakeNode(child, dir.Size));
        }

        private void ShowSizeNode()
        {
            var dir = _sizeTree.SelectedNode == null ? null : _sizeTree.SelectedNode.Tag as DirNode;
            if (dir == null) return;

            var items = dir.Children.Select(c => new TreemapPanel.Item { Name = c.Name, Size = c.Size, Tag = c }).ToList();
            if (dir.OwnFiles > 0) items.Add(new TreemapPanel.Item { Name = Lang.T("（這一層的檔案）"), Size = dir.OwnFiles });
            _treemap.SetItems(items);

            string prefix = dir.Path.TrimEnd('\\') + "\\";
            _bigShown = _bigFiles.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(200).ToList();
            _bigList.BeginUpdate();
            _bigList.Items.Clear();
            foreach (var f in _bigShown)
            {
                var item = new ListViewItem(f.Path) { Tag = f };
                item.SubItems.Add(Format.Size(f.Size));
                item.SubItems.Add(f.Modified.ToString("yyyy/MM/dd HH:mm"));
                _bigList.Items.Add(item);
            }
            _bigList.EndUpdate();
        }

        /// <summary>樹狀圖點了某個資料夾：在左邊樹裡展開到那裡並選取。</summary>
        private void SelectDir(DirNode target)
        {
            if (target == null || _sizeTree.SelectedNode == null) return;
            var node = _sizeTree.SelectedNode;
            FillChildren(node);
            node.Expand();
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag == target) { _sizeTree.SelectedNode = child; return; }
            }
        }

        private void RecycleBigFiles()
        {
            var picked = _bigList.SelectedItems.Cast<ListViewItem>().Select(i => (BigFile)i.Tag).ToList();
            var gone = new HashSet<string>(Recycle(picked.Select(f => f.Path).ToList(), Lang.T("檔案")), StringComparer.OrdinalIgnoreCase);
            if (gone.Count == 0) return;

            _bigFiles.RemoveAll(f => gone.Contains(f.Path));
            foreach (var item in _bigList.Items.Cast<ListViewItem>().Where(i => gone.Contains(((BigFile)i.Tag).Path)).ToList())
            {
                _bigList.Items.Remove(item);
            }
            _status.Text += Lang.T("（資料夾大小要重新分析才會更新）");
        }

        // ───────────────────────────── 重複檔案 ─────────────────────────────

        private TabPage BuildDuplicatePage()
        {
            var page = new TabPage(Lang.T("重複檔案"));
            _dupMin = new NumericUpDown { Minimum = 1, Maximum = 10000000, Value = 1024, Width = 90, ThousandsSeparator = true };
            var minLabel = new Label { Text = Lang.T("KB 以上"), AutoSize = true, Margin = new Padding(3, 6, 10, 0) };
            var minPrefix = new Label { Text = Lang.T("只找"), AutoSize = true, Margin = new Padding(10, 6, 3, 0) };
            var bar = PathBar(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Lang.T("搜尋"), out _dupPath, out _dupGo,
                minPrefix, _dupMin, minLabel);

            _dupList = new ThemedListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false,
                ShowGroups = true
            };
            _dupList.Columns.Add(Lang.T("檔案（勾選＝要刪除）"), 620);
            _dupList.Columns.Add(Lang.T("大小"), 90, HorizontalAlignment.Right);
            _dupList.Columns.Add(Lang.T("修改時間"), 130);
            _dupList.DoubleClick += delegate { if (_dupList.SelectedItems.Count > 0) OpenFolder(_dupList.SelectedItems[0].Text); };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4, 4, 4, 0) };
            var keepOld = new Button { Text = Lang.T("每組只留最舊的"), Width = 130, Height = 28 };
            var keepNew = new Button { Text = Lang.T("每組只留最新的"), Width = 130, Height = 28 };
            var none = new Button { Text = Lang.T("全部取消勾選"), Width = 110, Height = 28 };
            var delete = new Button { Text = Lang.T("勾選的送進資源回收筒"), Width = 170, Height = 28 };
            buttons.Controls.AddRange(new Control[] { keepOld, keepNew, none, delete });

            var hint = new Label
            {
                Dock = DockStyle.Top, Height = 24, Padding = new Padding(6, 4, 0, 0), ForeColor = Color.FromArgb(90, 90, 100),
                Text = Lang.T("內容完全相同（比對雜湊值）的檔案分成一組；每組至少要留一份沒勾。雙擊開啟所在資料夾。")
            };

            page.Controls.Add(_dupList);
            page.Controls.Add(buttons);
            page.Controls.Add(hint);
            page.Controls.Add(bar);

            _dupGo.Click += delegate { FindDuplicates(); };
            keepOld.Click += delegate { MarkDuplicates(true); };
            keepNew.Click += delegate { MarkDuplicates(false); };
            none.Click += delegate { foreach (ListViewItem i in _dupList.Items) i.Checked = false; };
            delete.Click += delegate { RecycleDuplicates(); };
            return page;
        }

        private async void FindDuplicates()
        {
            if (!BeginWork(_dupGo, Lang.T("搜尋"))) return;
            if (!CheckFolder(_dupPath)) { EndWork(_dupGo); return; }

            string root = _dupPath.Text;
            long min = (long)_dupMin.Value * 1024;
            var token = _cts.Token;
            var report = Throttled(Lang.T("搜尋中："));

            List<List<BigFile>> groups = null;
            try
            {
                groups = await Task.Run(() => Analysis.FindDuplicates(root, min, token, report));
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("搜尋時發生錯誤：") + ex.Message, Text);
            }
            bool cancelled = token.IsCancellationRequested;
            EndWork(_dupGo);
            if (groups == null) return;

            const int maxGroups = 1000;
            _dupList.BeginUpdate();
            _dupList.Items.Clear();
            _dupList.Groups.Clear();
            foreach (var g in groups.Take(maxGroups))
            {
                var group = new ListViewGroup(string.Format(Lang.T("{0} 個相同檔案，各 {1}，可省 {2}"),
                    g.Count, Format.Size(g[0].Size), Format.Size(g[0].Size * (g.Count - 1))));
                _dupList.Groups.Add(group);
                foreach (var f in g.OrderBy(f => f.Modified))
                {
                    var item = new ListViewItem(f.Path, group) { Tag = f };
                    item.SubItems.Add(Format.Size(f.Size));
                    item.SubItems.Add(f.Modified.ToString("yyyy/MM/dd HH:mm"));
                    _dupList.Items.Add(item);
                }
            }
            _dupList.EndUpdate();

            long saving = groups.Sum(g => g[0].Size * (g.Count - 1));
            _status.Text = string.Format(Lang.T("{0}：{1:N0} 組重複，最多可省 {2}{3}"), cancelled ? Lang.T("已停止，以下是部分結果") : Lang.T("搜尋完成"),
                groups.Count, Format.Size(saving), groups.Count > maxGroups ? string.Format(Lang.T("（只列出最大的 {0} 組）"), maxGroups) : string.Empty);
        }

        /// <summary>每組留一份（最舊或最新），其餘勾起來。</summary>
        private void MarkDuplicates(bool keepOldest)
        {
            foreach (ListViewGroup group in _dupList.Groups)
            {
                var items = group.Items.Cast<ListViewItem>().OrderBy(i => ((BigFile)i.Tag).Modified).ToList();
                if (items.Count == 0) continue;
                var keep = keepOldest ? items.First() : items.Last();
                foreach (var i in items) i.Checked = i != keep;
            }
        }

        private void RecycleDuplicates()
        {
            // 防呆：任何一組全部被勾，就整組都不見了，不讓它執行。
            foreach (ListViewGroup group in _dupList.Groups)
            {
                var items = group.Items.Cast<ListViewItem>().ToList();
                if (items.Count > 0 && items.All(i => i.Checked))
                {
                    Msg.Show(this, Lang.T("有一組的檔案全部被勾選了，每組至少要留一份：\r\n\r\n") + items[0].Text,
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var picked = _dupList.CheckedItems.Cast<ListViewItem>().ToList();
            var gone = new HashSet<string>(Recycle(picked.Select(i => i.Text).ToList(), Lang.T("重複檔案")), StringComparer.OrdinalIgnoreCase);
            foreach (var item in picked.Where(i => gone.Contains(i.Text))) _dupList.Items.Remove(item);
        }

        // ───────────────────────────── 空資料夾 ─────────────────────────────

        private TabPage BuildEmptyPage()
        {
            var page = new TabPage(Lang.T("空資料夾"));
            var bar = PathBar(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Lang.T("搜尋"), out _emptyPath, out _emptyGo);

            _emptyList = new ThemedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false };
            _emptyList.Columns.Add(Lang.T("空資料夾（勾選＝要刪除）"), 700);
            _emptyList.Columns.Add(Lang.T("建立時間"), 140);
            _emptyList.DoubleClick += delegate { if (_emptyList.SelectedItems.Count > 0) OpenFolder(_emptyList.SelectedItems[0].Text); };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4, 4, 4, 0) };
            var all = new Button { Text = Lang.T("全部勾選"), Width = 100, Height = 28 };
            var none = new Button { Text = Lang.T("全部取消勾選"), Width = 110, Height = 28 };
            var delete = new Button { Text = Lang.T("勾選的送進資源回收筒"), Width = 170, Height = 28 };
            buttons.Controls.AddRange(new Control[] { all, none, delete });

            var hint = new Label
            {
                Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 4, 0, 0), ForeColor = Color.FromArgb(90, 90, 100),
                Text = Lang.T("底下完全沒有檔案的資料夾（整棵都空的只列最上層）。有些程式會用到空資料夾（例如設定、外掛資料夾），")
                     + Environment.NewLine + Lang.T("所以預設都不勾，請只刪自己認得的。")
            };

            page.Controls.Add(_emptyList);
            page.Controls.Add(buttons);
            page.Controls.Add(hint);
            page.Controls.Add(bar);

            _emptyGo.Click += delegate { FindEmpty(); };
            all.Click += delegate { foreach (ListViewItem i in _emptyList.Items) i.Checked = true; };
            none.Click += delegate { foreach (ListViewItem i in _emptyList.Items) i.Checked = false; };
            delete.Click += delegate
            {
                var picked = _emptyList.CheckedItems.Cast<ListViewItem>().ToList();
                var gone = new HashSet<string>(Recycle(picked.Select(i => i.Text).ToList(), Lang.T("空資料夾")), StringComparer.OrdinalIgnoreCase);
                foreach (var item in picked.Where(i => gone.Contains(i.Text))) _emptyList.Items.Remove(item);
            };
            return page;
        }

        private async void FindEmpty()
        {
            if (!BeginWork(_emptyGo, Lang.T("搜尋"))) return;
            if (!CheckFolder(_emptyPath)) { EndWork(_emptyGo); return; }

            string root = _emptyPath.Text;
            var token = _cts.Token;
            var report = Throttled(Lang.T("搜尋中："));

            List<string> dirs = null;
            try
            {
                dirs = await Task.Run(() => Analysis.FindEmptyDirs(root, token, report));
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("搜尋時發生錯誤：") + ex.Message, Text);
            }
            bool cancelled = token.IsCancellationRequested;
            EndWork(_emptyGo);
            if (dirs == null) return;

            _emptyList.BeginUpdate();
            _emptyList.Items.Clear();
            foreach (string d in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem(d);
                DateTime created;
                try { created = Directory.GetCreationTime(d); } catch (Exception) { created = DateTime.MinValue; }
                item.SubItems.Add(created > DateTime.MinValue ? created.ToString("yyyy/MM/dd HH:mm") : string.Empty);
                _emptyList.Items.Add(item);
            }
            _emptyList.EndUpdate();
            _status.Text = string.Format(Lang.T("{0}：{1:N0} 個空資料夾"), cancelled ? Lang.T("已停止，以下是部分結果") : Lang.T("搜尋完成"), dirs.Count);
        }
    }
}
