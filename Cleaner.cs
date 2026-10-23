using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace DiskCleaner
{
    /// <summary>掃到的一個檔案（資源回收筒、DISM 這類則是一筆總計）。</summary>
    public class FileEntry
    {
        public string Path;
        public long Size;
        public DateTime Created;

        /// <summary>刪完往上清空資料夾，最多清到這一層（含）；用在 __pycache__ 這種散在專案裡的快取資料夾。</summary>
        public string PruneUpTo;
    }

    public enum ItemKind
    {
        Files,            // 依規則列出資料夾裡的檔案
        RecycleBin,       // 資源回收筒，用 Shell API 查與清
        BrokenShortcuts,  // 目標已經不存在的 .lnk
        FolderTotal,      // 整個資料夾當成一筆（Windows.old），清理時先取得擁有權再刪
        Command,          // 交給系統工具清（DISM），大小執行完才知道
        RegistryValues,   // 登錄機碼裡的值（執行紀錄、搜尋紀錄、Office 最近清單）；Target 可用分號列多個機碼
        Clipboard         // 剪貼簿目前的內容
    }

    /// <summary>
    /// 一條掃描規則。Root 可以在中間放「*」代表任意一層子資料夾，
    /// 例如 Edge 的 User Data\*\Cache\Cache_Data（每個設定檔各一份）。
    /// </summary>
    public class ScanRule
    {
        public string Root;
        public string Pattern = "*";
        public bool Recursive = true;
        public int MinAgeDays;                 // 只算超過幾天沒動的（例如舊的預讀取資料）
        public Func<string, bool> Skip;        // 回傳 true 的檔案不列入

        /// <summary>
        /// 有設時只收「位在這個名稱的資料夾裡」的檔案，例如 __pycache__。
        /// 會走遍 Root 底下找這種資料夾，但不進 .git、node_modules 這些又大又不相關的地方。
        /// </summary>
        public string DirName;

        public ScanRule(string root, string pattern = "*", bool recursive = true)
        {
            Root = Environment.ExpandEnvironmentVariables(root);
            Pattern = pattern;
            Recursive = recursive;
        }
    }

    /// <summary>左邊樹狀清單的一個工作項目，掃描結果也掛在這裡。</summary>
    public class CleanItem
    {
        public string Id;
        public string Category;
        public string Name;
        public string Description;
        public bool DefaultChecked = true;
        public bool NeedsAdmin;
        public bool RespectAge;                // 套用「只清超過 N 小時」的設定（暫存類）
        public bool RemoveEmptyDirs;           // 清完把空的子資料夾也刪掉（不刪根）
        public bool Privacy;                   // 隱私類：勾選時要警告，自動清理不碰
        public string Warning;                 // 勾選時跳出的提醒
        public ItemKind Kind = ItemKind.Files;
        public int Icon;
        public List<ScanRule> Rules = new List<ScanRule>();

        // FolderTotal／Command／RegistryValues 用
        public string Target;                  // 資料夾、命令列或登錄機碼
        public string Arguments;

        // ── 掃描結果 ──
        public List<FileEntry> Found = new List<FileEntry>();
        public long FoundBytes;
        public int Denied;                     // 沒權限讀的資料夾數
        public long RecycleItems;              // 資源回收筒的項目數

        /// <summary>自動（排程）清理會跑的項目：只清檔案類，不清空回收筒、不碰隱私、不跑系統工具。</summary>
        public bool SafeForAuto
        {
            get { return !Privacy && (Kind == ItemKind.Files || Kind == ItemKind.BrokenShortcuts); }
        }

        /// <summary>畫面上的「項目數」：回收筒算裡面的項目，其他算列出的筆數。</summary>
        public long Count
        {
            get { return Kind == ItemKind.RecycleBin ? RecycleItems : Found.Count; }
        }

        public void ResetResult()
        {
            Found = new List<FileEntry>();
            FoundBytes = 0;
            Denied = 0;
            RecycleItems = 0;
        }
    }

    public class ScanOptions
    {
        public int MinAgeHours;
        public List<string> Ignore = new List<string>();
    }

    public class CleanResult
    {
        public int Deleted;
        public long Bytes;
        public int Failed;
    }

    /// <summary>掃描與清理的引擎；項目清單在 CleanItems.cs。</summary>
    public static partial class Cleaner
    {
        // 走目錄找 DirName 時不進去的資料夾：又大又不會有我們要的快取。
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", ".hg", ".svn"
        };

        /// <summary>這些位置整個清掉會出事，不讓使用者加成自訂資料夾。</summary>
        public static bool IsProtected(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;

            string full;
            try { full = System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
            catch (Exception) { return true; }

            if (full.Length <= 3) return true;   // 磁碟根目錄

            var blocked = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };
            return blocked.Any(b => !string.IsNullOrEmpty(b) && string.Equals(b.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
        }

        // ───────────────────────────── 掃描 ─────────────────────────────

        public static void Scan(CleanItem item, ScanOptions options, CancellationToken token, Action<string> progress)
        {
            item.ResetResult();

            switch (item.Kind)
            {
                case ItemKind.RecycleBin: ScanRecycleBin(item); return;
                case ItemKind.FolderTotal: ScanFolderTotal(item, token, progress); return;
                case ItemKind.Command: ScanCommand(item); return;
                case ItemKind.RegistryValues: ScanRegistry(item); return;
                case ItemKind.Clipboard:
                    item.Found.Add(new FileEntry { Path = Lang.T("剪貼簿目前的內容（文字、圖片、複製的檔案）"), Size = 0 });
                    return;
            }

            DateTime ageLimit = item.RespectAge && options.MinAgeHours > 0
                ? DateTime.Now.AddHours(-options.MinAgeHours)
                : DateTime.MaxValue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in item.Rules)
            {
                foreach (string root in ExpandRoot(rule.Root))
                {
                    if (token.IsCancellationRequested) return;
                    progress(root);

                    DateTime limit = ageLimit;
                    if (rule.MinAgeDays > 0) limit = Min(limit, DateTime.Now.AddDays(-rule.MinAgeDays));

                    foreach (var hit in EnumerateFiles(root, rule, item, token))
                    {
                        var file = hit.Item1;
                        if (!seen.Add(file.FullName)) continue;
                        if (!MatchesExtension(file.Name, rule.Pattern)) continue;
                        if (IsIgnored(file.FullName, options.Ignore)) continue;
                        if (rule.Skip != null && rule.Skip(file.FullName)) continue;
                        if (file.LastWriteTime > limit) continue;

                        if (item.Kind == ItemKind.BrokenShortcuts && !ShellLink.IsBroken(file.FullName)) continue;

                        long size;
                        DateTime created;
                        try
                        {
                            size = file.Length;
                            created = file.CreationTime;
                        }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }

                        item.Found.Add(new FileEntry { Path = file.FullName, Size = size, Created = created, PruneUpTo = hit.Item2 });
                        item.FoundBytes += size;
                    }
                }
            }
        }

        private static void ScanRecycleBin(CleanItem item)
        {
            long size, count;
            if (!RecycleBin.Query(out size, out count) || count == 0) return;

            item.RecycleItems = count;
            item.FoundBytes = size;
            item.Found.Add(new FileEntry { Path = string.Format(Lang.T("資源回收筒（{0:N0} 個項目）"), count), Size = size });
        }

        /// <summary>整個資料夾算一筆，只加總大小（Windows.old 動輒幾十萬個檔案，一個個列出來沒意義）。</summary>
        private static void ScanFolderTotal(CleanItem item, CancellationToken token, Action<string> progress)
        {
            if (!Directory.Exists(item.Target)) return;
            progress(item.Target);

            long size = 0, files = 0;
            var rule = new ScanRule(item.Target);
            foreach (var hit in EnumerateFiles(item.Target, rule, item, token))
            {
                try { size += hit.Item1.Length; files++; }
                catch (Exception) { }
            }

            item.FoundBytes = size;
            item.Found.Add(new FileEntry
            {
                Path = string.Format(Lang.T("{0}（{1:N0} 個檔案{2}）"), item.Target, files,
                    item.Denied > 0 ? Lang.T("，部分資料夾沒權限讀取，實際更大") : string.Empty),
                Size = size,
                Created = Directory.GetCreationTime(item.Target)
            });
        }

        private static void ScanCommand(CleanItem item)
        {
            item.Found.Add(new FileEntry { Path = item.Description, Size = 0 });
        }

        private static void ScanRegistry(CleanItem item)
        {
            foreach (string path in RegistryKeys(item.Target))
            {
                using (var key = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (key == null) continue;
                    foreach (string name in key.GetValueNames())
                    {
                        if (IsMruIndex(name)) continue;   // 排序用的索引，跟著一起刪但不列出來
                        object value = key.GetValue(name);
                        // 搜尋紀錄存成 UTF-16 的位元組，轉回文字比較看得懂。
                        string text = value is byte[] ? System.Text.Encoding.Unicode.GetString((byte[])value).TrimEnd('\0') : Convert.ToString(value);
                        item.Found.Add(new FileEntry { Path = string.Format(@"HKCU\{0}\{1} = {2}", path, name, text), Size = 0 });
                    }
                }
            }
        }

        private static bool IsMruIndex(string name)
        {
            return name == "MRUList" || name == "MRUListEx";
        }

        /// <summary>
        /// Target 用分號分隔多個機碼；「office:」開頭的展開成每個 Office 版本、每個程式的 File MRU／Place MRU
        /// （Software\Microsoft\Office\16.0\Word\User MRU\&lt;帳號&gt;\File MRU 之類）。
        /// </summary>
        private static IEnumerable<string> RegistryKeys(string target)
        {
            foreach (string part in (target ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.StartsWith("office:", StringComparison.Ordinal)) { yield return part; continue; }

                const string office = @"Software\Microsoft\Office";
                using (var root = Registry.CurrentUser.OpenSubKey(office))
                {
                    if (root == null) continue;
                    foreach (string version in root.GetSubKeyNames().Where(v => v.Contains(".")))
                    {
                        using (var ver = root.OpenSubKey(version))
                        {
                            if (ver == null) continue;
                            foreach (string app in ver.GetSubKeyNames())
                            {
                                string appPath = office + "\\" + version + "\\" + app;
                                foreach (string mru in new[] { "File MRU", "Place MRU" })
                                {
                                    yield return appPath + "\\" + mru;   // 舊版直接在 app 底下
                                    using (var users = Registry.CurrentUser.OpenSubKey(appPath + "\\User MRU"))
                                    {
                                        if (users == null) continue;
                                        foreach (string user in users.GetSubKeyNames()) yield return appPath + "\\User MRU\\" + user + "\\" + mru;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static DateTime Min(DateTime a, DateTime b)
        {
            return a < b ? a : b;
        }

        /// <summary>
        /// Windows 的「*.log」會連「x.log1」「x.log_old」都比對到（8.3 短檔名的老規則），
        /// 規則是「*.副檔名」時再嚴格比一次。
        /// </summary>
        private static bool MatchesExtension(string name, string pattern)
        {
            if (pattern == null || !pattern.StartsWith("*.", StringComparison.Ordinal) || pattern.IndexOfAny(new[] { '*', '?' }, 2) >= 0)
            {
                return true;
            }
            return name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsIgnored(string path, List<string> ignore)
        {
            foreach (string entry in ignore)
            {
                string e = entry.TrimEnd('\\');
                if (path.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
                if (path.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把 Root 中的「*」展開成實際存在的資料夾。</summary>
        public static IEnumerable<string> ExpandRoot(string root)
        {
            // 結尾的「\*」也是「每個子資料夾」，補一個斜線讓下面同一套處理。
            if (root.EndsWith("\\*", StringComparison.Ordinal)) root += "\\";

            int star = root.IndexOf("\\*\\", StringComparison.Ordinal);
            if (star < 0)
            {
                root = root.TrimEnd('\\');
                if (Directory.Exists(root)) yield return root;
                yield break;
            }

            string parent = root.Substring(0, star);
            string rest = root.Substring(star + 3);
            if (!Directory.Exists(parent)) yield break;

            string[] subs;
            try { subs = Directory.GetDirectories(parent); }
            catch (Exception) { yield break; }

            foreach (string sub in subs)
            {
                foreach (string expanded in ExpandRoot(sub + "\\" + rest)) yield return expanded;
            }
        }

        /// <summary>
        /// 自己走目錄樹而不是用 SearchOption.AllDirectories：一個子資料夾沒權限時那個就跳過，
        /// 不會整棵樹丟例外；也不跟進 junction／符號連結，免得繞到別的地方去刪。
        /// 回傳 (檔案, 命中的 DirName 資料夾；沒設 DirName 時為 null)。
        /// </summary>
        private static IEnumerable<Tuple<FileInfo, string>> EnumerateFiles(string root, ScanRule rule, CleanItem item, CancellationToken token)
        {
            bool wantDir = !string.IsNullOrEmpty(rule.DirName);
            var pending = new Stack<Tuple<DirectoryInfo, string>>();
            pending.Push(Tuple.Create(new DirectoryInfo(root), (string)null));

            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) yield break;
                var current = pending.Pop();
                var dir = current.Item1;
                string matched = current.Item2;

                FileInfo[] files = null;
                DirectoryInfo[] subs = null;
                try
                {
                    // 找 DirName 時，還沒進到命中的資料夾前不用列檔案。
                    if (!wantDir || matched != null) files = dir.GetFiles(rule.Pattern);
                    if (rule.Recursive) subs = dir.GetDirectories();
                }
                catch (UnauthorizedAccessException) { item.Denied++; }
                catch (IOException) { }

                if (files != null)
                {
                    foreach (var f in files) yield return Tuple.Create(f, matched);
                }

                if (subs == null) continue;
                foreach (var sub in subs)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    string next = matched;
                    if (wantDir && matched == null)
                    {
                        if (SkipDirs.Contains(sub.Name)) continue;
                        if (sub.Name.Equals(rule.DirName, StringComparison.OrdinalIgnoreCase)) next = sub.FullName;
                    }
                    pending.Push(Tuple.Create(sub, next));
                }
            }
        }

        // ───────────────────────────── 清理 ─────────────────────────────

        /// <param name="recycle">true 時檔案送進資源回收筒（可救回，但空間要等清空回收筒才釋放）。</param>
        public static CleanResult Clean(IList<CleanItem> items, bool recycle, CancellationToken token, Action<string> progress)
        {
            var result = new CleanResult();

            foreach (var item in items)
            {
                if (token.IsCancellationRequested) break;

                switch (item.Kind)
                {
                    case ItemKind.RecycleBin: CleanRecycleBin(item, result, progress); continue;
                    case ItemKind.FolderTotal: CleanFolderTotal(item, result, progress); continue;
                    case ItemKind.Command: CleanCommand(item, result, progress); continue;
                    case ItemKind.RegistryValues: CleanRegistry(item, result); continue;
                    case ItemKind.Clipboard: ClearClipboard(result); continue;
                }

                if (recycle) RecycleFiles(item, result, token, progress);
                else DeleteFiles(item, result, token, progress);

                // 散在專案裡的快取資料夾（__pycache__）：刪空了就往上收，收到命中的那層為止。
                foreach (var entry in item.Found.Where(e => e.PruneUpTo != null))
                {
                    PruneEmptyParents(System.IO.Path.GetDirectoryName(entry.Path), entry.PruneUpTo);
                }

                if (item.RemoveEmptyDirs)
                {
                    foreach (var rule in item.Rules.Where(r => string.IsNullOrEmpty(r.DirName)))
                    {
                        foreach (string root in ExpandRoot(rule.Root)) RemoveEmptyDirs(root, true);
                    }
                }
            }

            return result;
        }

        private static void DeleteFiles(CleanItem item, CleanResult result, CancellationToken token, Action<string> progress)
        {
            foreach (var entry in item.Found)
            {
                if (token.IsCancellationRequested) break;
                progress(entry.Path);

                if (DeleteFile(entry.Path) || DeleteFile(@"\\?\" + entry.Path))
                {
                    result.Deleted++;
                    result.Bytes += entry.Size;
                }
                else if (File.Exists(entry.Path))
                {
                    // 使用中、沒權限：略過，算進失敗數。
                    result.Failed++;
                }
            }
        }

        /// <summary>一次送 200 個給 Shell，比一個一個送快很多；送完還在原地的算失敗。</summary>
        private static void RecycleFiles(CleanItem item, CleanResult result, CancellationToken token, Action<string> progress)
        {
            foreach (var batch in item.Found.Select((e, i) => new { e, i }).GroupBy(x => x.i / 200, x => x.e))
            {
                if (token.IsCancellationRequested) break;
                var entries = batch.Where(e => File.Exists(e.Path)).ToList();
                if (entries.Count == 0) continue;

                progress(entries[0].Path);
                RecycleBin.Send(entries.Select(e => e.Path));

                foreach (var entry in entries)
                {
                    if (File.Exists(entry.Path)) result.Failed++;
                    else
                    {
                        result.Deleted++;
                        result.Bytes += entry.Size;
                    }
                }
            }
        }

        private static void CleanRecycleBin(CleanItem item, CleanResult result, Action<string> progress)
        {
            if (item.RecycleItems == 0) return;
            progress(Lang.T("清空資源回收筒"));
            if (RecycleBin.Empty())
            {
                result.Deleted += (int)Math.Min(int.MaxValue, item.RecycleItems);
                result.Bytes += item.FoundBytes;
            }
            else result.Failed++;
        }

        /// <summary>
        /// Windows.old 的檔案擁有者是 TrustedInstaller，直接刪會被拒絕：
        /// 先 takeown 取得擁有權、icacls 給 Administrators 完整權限，再整個刪掉。需要系統管理員。
        /// </summary>
        private static void CleanFolderTotal(CleanItem item, CleanResult result, Action<string> progress)
        {
            if (item.Found.Count == 0 || !Directory.Exists(item.Target)) return;

            progress(Lang.T("取得 ") + item.Target + Lang.T(" 的擁有權…"));
            Run("takeown.exe", string.Format("/F \"{0}\" /R /A /D Y", item.Target));
            progress(Lang.T("設定 ") + item.Target + Lang.T(" 的權限…"));
            Run("icacls.exe", string.Format("\"{0}\" /grant *S-1-5-32-544:F /T /C /Q", item.Target));
            progress(Lang.T("刪除 ") + item.Target + "…");
            Run("cmd.exe", string.Format("/c rd /s /q \"{0}\"", item.Target));

            if (Directory.Exists(item.Target)) result.Failed++;
            else
            {
                result.Deleted++;
                result.Bytes += item.FoundBytes;
            }
        }

        private static void CleanCommand(CleanItem item, CleanResult result, Action<string> progress)
        {
            progress(Lang.T("執行 ") + System.IO.Path.GetFileName(item.Target) + " " + item.Arguments + Lang.T("（可能要好幾分鐘）"));
            if (Run(item.Target, item.Arguments) == 0) result.Deleted++;
            else result.Failed++;
        }

        private static void CleanRegistry(CleanItem item, CleanResult result)
        {
            foreach (string path in RegistryKeys(item.Target))
            {
                using (var key = Registry.CurrentUser.OpenSubKey(path, true))
                {
                    if (key == null) continue;
                    foreach (string name in key.GetValueNames())
                    {
                        try
                        {
                            key.DeleteValue(name);
                            if (!IsMruIndex(name)) result.Deleted++;
                        }
                        catch (Exception) { result.Failed++; }
                    }
                }
            }
        }

        /// <summary>剪貼簿只能在 STA 執行緒操作，清理是在背景執行緒跑的，所以另開一個 STA 執行緒來清。</summary>
        private static void ClearClipboard(CleanResult result)
        {
            bool ok = false;
            var thread = new Thread(() =>
            {
                try { System.Windows.Forms.Clipboard.Clear(); ok = true; }
                catch (Exception) { }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(5000);
            if (ok) result.Deleted++;
            else result.Failed++;
        }

        /// <summary>跑一個系統工具，不開視窗，等它結束；回傳結束代碼（啟動失敗回 -1）。</summary>
        private static int Run(string file, string arguments)
        {
            try
            {
                var info = new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(info))
                {
                    // 輸出要讀掉，不然緩衝區滿了程式會卡住。
                    p.OutputDataReceived += delegate { };
                    p.ErrorDataReceived += delegate { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// 刪一個檔案；唯讀的先拿掉唯讀。檔名是 nul、con 這類保留字時一般路徑刪不掉，
        /// 呼叫端會再用 \\?\ 前綴的路徑試一次。
        /// </summary>
        private static bool DeleteFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes = FileAttributes.Normal;
                info.Delete();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>從 dir 往上刪空資料夾，最多刪到 stopAt（含）。</summary>
        private static void PruneEmptyParents(string dir, string stopAt)
        {
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
                    Directory.Delete(dir);
                }
                catch (Exception) { return; }

                if (dir.Equals(stopAt, StringComparison.OrdinalIgnoreCase)) return;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
        }

        /// <summary>由下往上刪空資料夾，根目錄本身留著。</summary>
        private static bool RemoveEmptyDirs(string dir, bool isRoot)
        {
            bool empty = true;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) { empty = false; continue; }
                    if (!RemoveEmptyDirs(sub, false)) empty = false;
                }
                if (Directory.EnumerateFiles(dir).Any()) empty = false;
                if (empty && !isRoot) Directory.Delete(dir);
                return empty;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
