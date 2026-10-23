using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DiskCleaner
{
    public class BackupInfo
    {
        public string Path;
        public DateTime Time;
        public long Size;       // 壓縮檔大小
        public int Files;
    }

    public class BackupResult
    {
        public string Archive;
        public int Files;
        public long Bytes;      // 原始大小
        public int Skipped;     // 太大或讀不到而沒備份的
    }

    /// <summary>
    /// 清理前備份：要刪的檔案先壓進一個 zip，保留 N 天後自動刪掉，需要時可以還原回原位。
    /// zip 裡的路徑是「磁碟代號/原路徑」，例如 C/Users/me/AppData/Local/Temp/x.tmp。
    /// </summary>
    public static class Backup
    {
        /// <summary>單一檔案超過這個大小就不備份（例如好幾 GB 的 pip 快取包），不然 zip 會大到失去意義。</summary>
        public const long MaxFileSize = 200L * 1024 * 1024;

        /// <summary>一次備份最多這麼多原始資料，超過的部分不備份。</summary>
        public const long MaxTotal = 2L * 1024 * 1024 * 1024;

        public static string Folder
        {
            get { return System.IO.Path.Combine(Settings.Folder, "Backup"); }
        }

        /// <summary>把要刪的檔案壓進新的 zip。只處理一般檔案類項目（回收筒、登錄、系統工具不適用）。</summary>
        public static BackupResult Create(IEnumerable<CleanItem> items, CancellationToken token, Action<string> progress)
        {
            var result = new BackupResult();
            var files = items.Where(i => i.Kind == ItemKind.Files || i.Kind == ItemKind.BrokenShortcuts)
                             .SelectMany(i => i.Found).ToList();
            if (files.Count == 0) return result;

            Directory.CreateDirectory(Folder);
            result.Archive = System.IO.Path.Combine(Folder, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");

            using (var zip = ZipFile.Open(result.Archive, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    if (token.IsCancellationRequested) break;
                    if (f.Size > MaxFileSize || result.Bytes + f.Size > MaxTotal) { result.Skipped++; continue; }

                    progress(Lang.T("備份 ") + f.Path);
                    try
                    {
                        // 用共用讀取開，使用中但允許讀的檔案也備得到。
                        using (var source = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var entry = zip.CreateEntry(EntryName(f.Path), CompressionLevel.Fastest);
                            entry.LastWriteTime = File.GetLastWriteTime(f.Path);
                            using (var target = entry.Open()) source.CopyTo(target);
                        }
                        result.Files++;
                        result.Bytes += f.Size;
                    }
                    catch (Exception)
                    {
                        result.Skipped++;
                    }
                }
            }

            if (result.Files == 0)
            {
                try { File.Delete(result.Archive); } catch (Exception) { }
                result.Archive = null;
            }
            return result;
        }

        /// <summary>C:\Users\x → C/Users/x</summary>
        private static string EntryName(string path)
        {
            return path.Replace(":", string.Empty).Replace('\\', '/');
        }

        /// <summary>C/Users/x → C:\Users\x</summary>
        private static string OriginalPath(string entryName)
        {
            if (entryName.Length < 2 || entryName[1] != '/') return null;
            return entryName.Substring(0, 1) + ":\\" + entryName.Substring(2).Replace('/', '\\');
        }

        public static List<BackupInfo> List()
        {
            var list = new List<BackupInfo>();
            if (!Directory.Exists(Folder)) return list;

            foreach (var file in new DirectoryInfo(Folder).GetFiles("*.zip"))
            {
                var info = new BackupInfo { Path = file.FullName, Time = file.CreationTime, Size = file.Length };
                try
                {
                    using (var zip = ZipFile.OpenRead(file.FullName)) info.Files = zip.Entries.Count;
                }
                catch (Exception) { info.Files = -1; }
                list.Add(info);
            }
            return list.OrderByDescending(b => b.Time).ToList();
        }

        /// <summary>還原到原本的位置；原位已經有同名檔案的不覆蓋。回傳 (還原數, 略過數)。</summary>
        public static Tuple<int, int> Restore(string archive)
        {
            int restored = 0, skipped = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    string target = OriginalPath(entry.FullName);
                    if (target == null || File.Exists(target)) { skipped++; continue; }
                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        entry.ExtractToFile(target);
                        restored++;
                    }
                    catch (Exception) { skipped++; }
                }
            }
            return Tuple.Create(restored, skipped);
        }

        /// <summary>刪掉超過 days 天的備份。</summary>
        public static void Purge(int days)
        {
            if (!Directory.Exists(Folder) || days <= 0) return;
            foreach (var file in new DirectoryInfo(Folder).GetFiles("*.zip"))
            {
                if (file.CreationTime < DateTime.Now.AddDays(-days))
                {
                    try { file.Delete(); } catch (Exception) { }
                }
            }
        }
    }

    /// <summary>備份管理：列出備份、還原、刪除。</summary>
    public class BackupForm : ToolWindow
    {
        private readonly ThemedListView _list;

        public BackupForm() : base(Lang.T("清理備份"), Lang.T("清理前自動備份的檔案；可以還原到原來的位置"), "\uE777", new Size(760, 480))
        {
            _list = NewList(Lang.T("備份時間:180"), Lang.T("檔案數:100:r"), Lang.T("壓縮後大小:120:r"), Lang.T("位置:320"));
            var restore = Primary(Lang.T("還原選取的備份"), 150);
            var open = Light(Lang.T("開啟備份資料夾"), 140);
            var delete = Light(Lang.T("刪除選取"), 110);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(restore, open, delete));

            restore.Click += delegate { RestoreSelected(); };
            open.Click += delegate
            {
                Directory.CreateDirectory(Backup.Folder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Backup.Folder + "\"");
            };
            delete.Click += delegate { DeleteSelected(); };
            Load += delegate { Reload(); };
        }

        private void Reload()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var b in Backup.List())
            {
                var item = new ListViewItem(b.Time.ToString("yyyy/MM/dd HH:mm:ss")) { Tag = b };
                item.SubItems.Add(b.Files >= 0 ? b.Files.ToString("N0") : Lang.T("（損毀）"));
                item.SubItems.Add(Format.Size(b.Size));
                item.SubItems.Add(b.Path);
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            Status.Text = string.Format(Lang.T("共 {0} 份備份，合計 {1}（設定 → 選項 可以調整保留天數）"),
                _list.Items.Count, Format.Size(_list.Items.Cast<ListViewItem>().Sum(i => ((BackupInfo)i.Tag).Size)));
        }

        private void RestoreSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var b = (BackupInfo)_list.SelectedItems[0].Tag;
            if (Msg.Show(this, string.Format(Lang.T("把 {0} 的備份還原到原來的位置？\r\n原位已經有同名檔案的不會覆蓋。"), b.Time.ToString("yyyy/MM/dd HH:mm")),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            try
            {
                var r = Backup.Restore(b.Path);
                Status.Text = string.Format(Lang.T("已還原 {0:N0} 個檔案，略過 {1:N0} 個（原位已有檔案或無法寫入）"), r.Item1, r.Item2);
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("還原失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DeleteSelected()
        {
            var picked = _list.SelectedItems.Cast<ListViewItem>().Select(i => (BackupInfo)i.Tag).ToList();
            if (picked.Count == 0) return;
            if (Msg.Show(this, string.Format(Lang.T("刪除選取的 {0} 份備份？刪了就不能還原那些檔案。"), picked.Count), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (var b in picked)
            {
                try { File.Delete(b.Path); } catch (Exception) { }
            }
            Reload();
        }
    }
}
