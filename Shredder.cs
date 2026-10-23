using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 安全刪除：先用亂數把檔案內容覆寫 N 次、改成亂碼檔名，再刪除，一般救援軟體就救不回來。
    /// SSD 有損耗平均與 TRIM，覆寫不一定寫到原本那塊快閃記憶體，所以 SSD 上不能保證；畫面上有寫明。
    /// </summary>
    public static class Shredder
    {
        public static void ShredFile(string path, int passes, CancellationToken token)
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes = FileAttributes.Normal;
            long length = info.Length;

            using (var rng = RandomNumberGenerator.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                var buffer = new byte[1 << 16];
                for (int pass = 0; pass < passes && !token.IsCancellationRequested; pass++)
                {
                    stream.Position = 0;
                    long left = length;
                    while (left > 0 && !token.IsCancellationRequested)
                    {
                        rng.GetBytes(buffer);
                        int n = (int)Math.Min(buffer.Length, left);
                        stream.Write(buffer, 0, n);
                        left -= n;
                    }
                    stream.Flush(true);
                }
                stream.SetLength(0);
            }

            // 檔名也是線索，改成亂碼再刪。
            string renamed = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N").Substring(0, 12));
            File.Move(path, renamed);
            File.Delete(renamed);
        }

        /// <summary>資料夾：底下的檔案一個個粉碎，再由下往上刪掉空資料夾。不跟進 junction。</summary>
        public static IEnumerable<string> FilesUnder(string dir)
        {
            var pending = new Stack<string>();
            pending.Push(dir);
            while (pending.Count > 0)
            {
                string d = pending.Pop();
                string[] files = new string[0], subs = new string[0];
                try { files = Directory.GetFiles(d); subs = Directory.GetDirectories(d); } catch (Exception) { }
                foreach (string f in files) yield return f;
                foreach (string s in subs)
                {
                    if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) pending.Push(s);
                }
            }
        }
    }

    public class ShredderForm : ToolWindow
    {
        private readonly ThemedListView _list;
        private readonly ComboBox _passes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly RoundButton _shred;
        private CancellationTokenSource _cts;

        public ShredderForm() : base(Lang.T("檔案粉碎機"), Lang.T("覆寫後刪除，一般救援軟體救不回來（SSD 因為硬體特性無法保證）"), "", new Size(860, 520))
        {
            _list = NewList(Lang.T("要粉碎的檔案或資料夾:620"), Lang.T("大小:110:r"));
            _passes.Items.AddRange(new object[] { Lang.T("覆寫 1 次（快）"), Lang.T("覆寫 3 次（較安全）"), Lang.T("覆寫 7 次（最慢）") });
            _passes.SelectedIndex = 0;
            var addFiles = Light(Lang.T("加入檔案…"), 110);
            var addFolder = Light(Lang.T("加入資料夾…"), 120);
            var remove = Light(Lang.T("移出清單"), 100);
            _shred = new RoundButton { Text = Lang.T("粉碎"), Size = new Size(110, 38), Accent = Theme.Red };

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(addFiles, addFolder, remove, _passes, _shred));

            AllowDrop = true;
            _list.AllowDrop = true;
            DragEventHandler enter = (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragEventHandler drop = (s, e) => { foreach (string p in (string[])e.Data.GetData(DataFormats.FileDrop)) Add(p); };
            DragEnter += enter; DragDrop += drop;
            _list.DragEnter += enter; _list.DragDrop += drop;

            addFiles.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Multiselect = true, Title = Lang.T("選擇要粉碎的檔案") })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) foreach (string f in dlg.FileNames) Add(f);
                }
            };
            addFolder.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = Lang.T("選擇要粉碎的資料夾（整個資料夾都會刪掉）") })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) Add(dlg.SelectedPath);
                }
            };
            remove.Click += delegate { foreach (ListViewItem i in _list.SelectedItems.Cast<ListViewItem>().ToList()) _list.Items.Remove(i); };
            _shred.Click += delegate { Shred(); };
            FormClosing += delegate { if (_cts != null) _cts.Cancel(); };
            Status.Text = Lang.T("把檔案或資料夾拖進來，或用上面的按鈕加入。");
        }

        private void Add(string path)
        {
            if (_list.Items.Cast<ListViewItem>().Any(i => string.Equals((string)i.Tag, path, StringComparison.OrdinalIgnoreCase))) return;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (Cleaner.IsProtected(path) || path.StartsWith(win, StringComparison.OrdinalIgnoreCase))
            {
                Msg.Show(this, Lang.T("系統資料夾或使用者資料夾本身不能粉碎：\r\n") + path, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            long size = File.Exists(path) ? new FileInfo(path).Length : Shredder.FilesUnder(path).Sum(f => { try { return new FileInfo(f).Length; } catch (Exception) { return 0L; } });
            var item = new ListViewItem(path) { Tag = path };
            item.SubItems.Add(Format.Size(size));
            _list.Items.Add(item);
        }

        private async void Shred()
        {
            if (_cts != null) { _cts.Cancel(); return; }
            var targets = _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
            if (targets.Count == 0) return;
            if (Msg.Show(this, string.Format(Lang.T("永久粉碎這 {0} 個項目？\r\n\r\n粉碎後不會進資源回收筒，任何方式都救不回來。"), targets.Count), Lang.T("確認粉碎"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            int passes = new[] { 1, 3, 7 }[_passes.SelectedIndex];
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _shred.Text = Lang.T("停止");
            var progress = new Progress<string>(t => Status.Text = t);
            int done = 0, failed = 0;

            await Task.Run(() =>
            {
                foreach (string target in targets)
                {
                    bool isDir = Directory.Exists(target);
                    var files = isDir ? Shredder.FilesUnder(target).ToList() : new List<string> { target };
                    foreach (string f in files)
                    {
                        if (token.IsCancellationRequested) return;
                        ((IProgress<string>)progress).Report(Lang.T("粉碎 ") + f);
                        try { Shredder.ShredFile(f, passes, token); done++; }
                        catch (Exception) { failed++; }
                    }
                    if (isDir && !token.IsCancellationRequested)
                    {
                        try { Directory.Delete(target, true); } catch (Exception) { }
                    }
                }
            });

            foreach (ListViewItem item in _list.Items.Cast<ListViewItem>().ToList())
            {
                string p = (string)item.Tag;
                if (!File.Exists(p) && !Directory.Exists(p)) _list.Items.Remove(item);
            }
            Status.Text = string.Format(Lang.T("{0}：粉碎 {1:N0} 個檔案{2}"), token.IsCancellationRequested ? Lang.T("已停止") : Lang.T("完成"), done,
                failed > 0 ? string.Format(Lang.T("，{0:N0} 個失敗（使用中或沒有權限）"), failed) : string.Empty);
            _shred.Text = Lang.T("粉碎");
            _cts = null;
        }
    }
}
