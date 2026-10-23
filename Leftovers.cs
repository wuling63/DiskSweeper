using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    public class LeftoverFolder
    {
        public string Path;
        public string Where;
        public long Size;
        public int Files;
        public DateTime LastWrite;
    }

    /// <summary>
    /// 安裝殘留：Program Files、ProgramData、AppData 第一層裡，找不到任何「還在用」線索的資料夾。
    /// 線索：已安裝程式的名稱／發行者／安裝路徑、服務與啟動項目的執行檔、執行中的程式。
    /// 這是推測，所以只列出、預設不勾，刪除一律送資源回收筒。
    /// </summary>
    public static class Leftovers
    {
        /// <summary>系統或共用的資料夾，不管有沒有線索都不列。</summary>
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Common Files", "Internet Explorer", "WindowsApps", "ModifiableWindowsApps",
            "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player",
            "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar",
            "WindowsPowerShell", "Uninstall Information", "Reference Assemblies", "MSBuild", "dotnet", "Microsoft.NET",
            "Package Cache", "Packages", "Packages Cache", "Temp", "Programs", "Publishers", "Comms", "ConnectedDevicesPlatform",
            "D3DSCache", "CrashDumps", "PeerDistRepub", "VirtualStore", "History", "Application Data", "Temporary Internet Files",
            "Microsoft Help", "Microsoft SDKs", "Windows Kits", "IsolatedStorage", "SoftwareDistribution", "regid.1991-06.com.microsoft",
            "ssh", "USOPrivate", "USOShared", "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "Intel", "NVIDIA",
            "NVIDIA Corporation", "AMD", "Realtek", "Adobe", "Mozilla", "Google", "Apple", "Oracle", "Java", "Sun",
            "pip", "npm", "npm-cache", "NuGet", "Yarn", "conda", ".conda", "Code", "JetBrains", "Docker", "Python",
            "fontconfig", "Downloaded Installations", "Downloaded Program Files", "Installer", "Setup", "Logs", "cache", "Cache"
        };

        /// <summary>這麼多天沒有任何檔案變動才算殘留：工具的快取、常用程式的資料多半近期內會被寫到。</summary>
        public const int IdleDays = 180;

        /// <summary>小於這個大小的不列，刪了也省不了什麼，只會讓清單變長。</summary>
        public const long MinSize = 1024 * 1024;

        /// <summary>名稱看起來就是快取、建置輸出或更新程式的資料夾，多半是工具自己的資料，不當成殘留。</summary>
        private static readonly string[] ToolHints = { "cache", "build", "updater", "update", "temp", "tmp", "log", "crash", "telemetry" };

        public static List<LeftoverFolder> Find(CancellationToken token, Action<string> progress)
        {
            progress(Lang.T("收集已安裝程式、服務、啟動項目與執行中的程式…"));
            var apps = InstalledApps.Load();
            var paths = new List<string>();
            var names = new List<string>();
            var firstWords = new HashSet<string>(StringComparer.Ordinal);

            foreach (var a in apps)
            {
                if (!string.IsNullOrEmpty(a.InstallLocation)) paths.Add(a.InstallLocation.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(a.Name)) { names.Add(Norm(a.Name)); firstWords.Add(FirstWord(a.Name)); }
                if (!string.IsNullOrEmpty(a.Publisher)) names.Add(Norm(a.Publisher));
                var exe = InstalledApps.SplitCommand(a.UninstallString).Item1;
                if (exe != null && Path.IsPathRooted(exe)) paths.Add(SafeDir(exe));
            }
            foreach (var s in StartupManager.Load()) if (s.ExePath != null && Path.IsPathRooted(s.ExePath)) paths.Add(SafeDir(s.ExePath));
            foreach (var exe in ServiceExes()) paths.Add(SafeDir(exe));
            foreach (var exe in ProcessExes()) paths.Add(SafeDir(exe));
            // 開始選單的捷徑：名稱與目標資料夾都算線索（免安裝、沒登記在「已安裝」的程式常常只有捷徑）。
            foreach (var lnk in StartMenuShortcuts())
            {
                names.Add(Norm(Path.GetFileNameWithoutExtension(lnk)));
                firstWords.Add(FirstWord(Path.GetFileNameWithoutExtension(lnk)));
                string target = ShellLink.Target(lnk);
                if (!string.IsNullOrEmpty(target) && Path.IsPathRooted(target)) paths.Add(SafeDir(target));
            }
            paths = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            names = names.Where(n => n.Length >= 3).Distinct().ToList();

            var roots = new[]
            {
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppData\\Local")
            };

            var result = new List<LeftoverFolder>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root.Item1) || !seenRoots.Add(root.Item1) || !Directory.Exists(root.Item1)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root.Item1); } catch (Exception) { continue; }

                foreach (string dir in dirs)
                {
                    if (token.IsCancellationRequested) return result;
                    string name = Path.GetFileName(dir);
                    if (Known.Contains(name) || name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith("{", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try { if ((File.GetAttributes(dir) & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue; }
                    catch (Exception) { continue; }

                    if (InUse(dir, name, paths, names)) continue;
                    // 名稱很短的程式（例如 Qt、R）上面比不到，改比第一個字：資料夾 QtProject 的第一個字 qt＝程式 Qt。
                    string first = FirstWord(name);
                    if (first.Length >= 2 && firstWords.Contains(first)) continue;
                    string lower = name.ToLowerInvariant();
                    if (ToolHints.Any(h => lower.Contains(h))) continue;

                    progress(Lang.T("檢查 ") + dir);
                    var info = Measure(dir, token);
                    if (info == null || info.Size < MinSize) continue;
                    if (info.LastWrite > DateTime.Now.AddDays(-IdleDays)) continue;   // 最近還有在寫，多半還在用
                    info.Where = root.Item2;
                    result.Add(info);
                }
            }
            return result.OrderByDescending(r => r.Size).ToList();
        }

        /// <summary>有任何線索指向這個資料夾就算「還在用」。</summary>
        private static bool InUse(string dir, string name, List<string> paths, List<string> names)
        {
            string d = dir.TrimEnd('\\');
            foreach (string p in paths)
            {
                if (p.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
                if (p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) return true;   // 程式裝在這底下
                if (d.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) return true;   // 這是某個程式資料夾的子目錄
            }

            string n = Norm(name);
            if (n.Length < 3) return true;   // 名字太短（例如 "x"）比不出來，保守當成在用
            foreach (string app in names)
            {
                if (app.Contains(n) || n.Contains(app)) return true;
                // 資料夾名稱是程式名稱的第一個字（例如資料夾 "Zoom"、程式 "Zoom Workplace"）
                int cut = Math.Min(app.Length, Math.Max(4, n.Length));
                if (app.Length >= 4 && n.StartsWith(app.Substring(0, cut), StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>第一個「字」：遇到空白、符號、數字或小寫轉大寫（CamelCase）就斷開，轉小寫。「QtProject」→「qt」、「Wing 101」→「wing」。</summary>
        private static string FirstWord(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? string.Empty)
            {
                if (!char.IsLetter(c)) { if (sb.Length > 0) break; continue; }
                if (sb.Length > 0 && char.IsUpper(c) && char.IsLower(sb[sb.Length - 1])) break;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>只留英數字並轉小寫：「Notepad++ (64-bit)」→「notepad64bit」。</summary>
        private static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? string.Empty) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string SafeDir(string file)
        {
            try { return Path.GetDirectoryName(file); } catch (Exception) { return null; }
        }

        private static IEnumerable<string> StartMenuShortcuts()
        {
            var list = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            {
                string root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try { list.AddRange(Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories)); }
                catch (Exception) { }
            }
            return list;
        }

        private static IEnumerable<string> ServiceExes()
        {
            var list = new List<string>();
            try
            {
                using (var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    foreach (string name in services.GetSubKeyNames())
                    {
                        using (var key = services.OpenSubKey(name))
                        {
                            string image = key == null ? null : key.GetValue("ImagePath") as string;
                            string exe = image == null ? null : InstalledApps.SplitCommand(image).Item1;
                            if (exe != null && Path.IsPathRooted(exe)) list.Add(exe);
                        }
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        private static IEnumerable<string> ProcessExes()
        {
            var list = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { list.Add(p.MainModule.FileName); } catch (Exception) { }
                }
            }
            return list;
        }

        private static LeftoverFolder Measure(string dir, CancellationToken token)
        {
            var info = new LeftoverFolder { Path = dir, LastWrite = Directory.GetLastWriteTime(dir) };
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(dir));
            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                var d = pending.Pop();
                try
                {
                    foreach (var f in d.EnumerateFiles())
                    {
                        info.Size += f.Length;
                        info.Files++;
                        if (f.LastWriteTime > info.LastWrite) info.LastWrite = f.LastWriteTime;
                    }
                    foreach (var sub in d.EnumerateDirectories())
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                    }
                }
                catch (Exception) { }
            }
            return info;
        }
    }

    public class LeftoverForm : ToolWindow
    {
        private readonly ThemedListView _list;
        private readonly RoundButton _scan, _delete;
        private CancellationTokenSource _cts;

        public LeftoverForm() : base(Lang.T("安裝殘留"), Lang.T("程式移除後留下、已經沒有任何程式在用的資料夾（推測，請確認後再刪）"), "", new Size(1000, 600))
        {
            _list = NewList(Lang.T("資料夾（勾選＝要刪除）:470"), Lang.T("位置:150"), Lang.T("大小:100:r"), Lang.T("檔案數:80:r"), Lang.T("最後修改:120"));
            _list.CheckBoxes = true;
            _scan = Primary(Lang.T("開始掃描"), 120);
            _delete = Light(Lang.T("勾選的送進資源回收筒"), 190);
            var open = Light(Lang.T("開啟資料夾"), 110);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(_scan, _delete, open));

            _list.DoubleClick += delegate { OpenSelected(); };
            open.Click += delegate { OpenSelected(); };
            _scan.Click += delegate { Scan(); };
            _delete.Click += delegate { Delete(); };
            FormClosing += delegate { if (_cts != null) _cts.Cancel(); };
            Status.Text = Lang.T("按「開始掃描」。判斷依據：已安裝程式、服務、啟動項目、執行中的程式都對不上，而且 180 天內沒有變動、大於 1 MB。");
        }

        private void OpenSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            string path = ((LeftoverFolder)_list.SelectedItems[0].Tag).Path;
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
        }

        private async void Scan()
        {
            if (_cts != null) { _cts.Cancel(); return; }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _scan.Text = Lang.T("停止");
            var progress = new Progress<string>(t => Status.Text = t);
            List<LeftoverFolder> found = null;
            try
            {
                found = await Task.Run(() => Leftovers.Find(token, t => ((IProgress<string>)progress).Report(t)));
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("掃描失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _scan.Text = Lang.T("開始掃描");
            _cts = null;
            if (found == null) return;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var f in found)
            {
                var item = new ListViewItem(f.Path) { Tag = f };
                item.SubItems.Add(f.Where);
                item.SubItems.Add(Format.Size(f.Size));
                item.SubItems.Add(f.Files.ToString("N0"));
                item.SubItems.Add(f.LastWrite.ToString("yyyy/MM/dd"));
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            Status.Text = string.Format(Lang.T("找到 {0} 個可能的殘留資料夾，共 {1}。預設不勾，請確認是自己認得、已經移除的程式再勾。"),
                found.Count, Format.Size(found.Sum(f => f.Size)));
        }

        private void Delete()
        {
            var picked = _list.CheckedItems.Cast<ListViewItem>().Select(i => (LeftoverFolder)i.Tag)
                              .Where(f => !Cleaner.IsProtected(f.Path)).ToList();
            if (picked.Count == 0) return;
            if (Msg.Show(this, string.Format(Lang.T("把 {0} 個資料夾（共 {1}）送進資源回收筒？\r\n如果之後有程式出問題，可以從資源回收筒還原。"),
                    picked.Count, Format.Size(picked.Sum(f => f.Size))), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            RecycleBin.Send(picked.Select(f => f.Path));
            int gone = 0;
            foreach (var item in _list.CheckedItems.Cast<ListViewItem>().ToList())
            {
                if (!Directory.Exists(((LeftoverFolder)item.Tag).Path)) { _list.Items.Remove(item); gone++; }
            }
            Status.Text = string.Format(Lang.T("已將 {0} 個資料夾送進資源回收筒{1}"), gone, gone < picked.Count ? Lang.T("（有些沒有權限或使用中，沒有移動）") : string.Empty);
        }
    }
}
