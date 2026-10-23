using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace DiskCleaner
{
    /// <summary>登錄裡「解除安裝」清單的一筆。</summary>
    public class InstalledApp
    {
        public string Name;
        public string Publisher;
        public string Version;
        public DateTime? InstallDate;
        public string InstallLocation;        // 登錄有寫的，或從圖示／解除安裝程式路徑推出來的
        public bool LocationGuessed;
        public long RegistrySize;             // EstimatedSize（位元組；0＝沒寫）
        public long FolderSize = -1;          // 實際資料夾大小；-1＝還沒算，-2＝沒有資料夾可算
        public string UninstallString;
        public string QuietUninstallString;
        public string ModifyPath;
        public bool NoModify, NoRepair;
        public bool IsMsi;
        public string ProductCode;            // MSI 的 {GUID}
        public string Url;
        public bool SystemComponent;          // SystemComponent=1、更新、修補程式
        public string Scope;                  // 所有使用者（64 位元）／所有使用者（32 位元）／目前使用者

        // 登錄位置：移除殘留項目、備份用
        public RegistryHive Hive;
        public RegistryView View;
        public string KeyPath;                // 不含 hive，例如 SOFTWARE\WOW6432Node\...\Uninstall\{GUID}

        /// <summary>reg.exe 用的完整路徑（32 位元的項目已經是 WOW6432Node 的實際位置）。</summary>
        public string RegPath
        {
            get { return (Hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\") + KeyPath; }
        }

        /// <summary>解除安裝程式的執行檔還在不在；MSI 一律交給 msiexec，當成在。</summary>
        /// <summary>
        /// 只有「確定不在」才回 false。RunDll32 這種系統程式本身一定在，要看它載入的 DLL；
        /// 判斷不出來（格式怪、路徑有非法字元）一律當成在，寧可漏掉也不把正常程式標成殘留。
        /// </summary>
        public bool UninstallerExists
        {
            get
            {
                if (IsMsi || string.IsNullOrWhiteSpace(UninstallString)) return true;
                try
                {
                    var cmd = InstalledApps.SplitCommand(UninstallString);
                    string exe = cmd.Item1;
                    if (string.IsNullOrEmpty(exe)) return true;

                    string name = Path.GetFileNameWithoutExtension(exe);
                    if (name.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
                    {
                        string dll = cmd.Item2.Split(',')[0].Trim().Trim('"');
                        return !Path.IsPathRooted(dll) || File.Exists(dll);
                    }
                    return !Path.IsPathRooted(exe) || File.Exists(exe);
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        public bool CanModify
        {
            get { return !NoModify && (IsMsi || !string.IsNullOrEmpty(ModifyPath)); }
        }
    }

    public static class InstalledApps
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>讀三個位置：HKLM 64 位元、HKLM 32 位元（WOW6432Node）、HKCU。同名同版本的只留一筆。</summary>
        public static List<InstalledApp> Load()
        {
            var list = new List<InstalledApp>();
            Read(list, RegistryHive.LocalMachine, RegistryView.Registry64, Lang.T("所有使用者（64 位元）"));
            if (Environment.Is64BitOperatingSystem)
            {
                Read(list, RegistryHive.LocalMachine, RegistryView.Registry32, Lang.T("所有使用者（32 位元）"));
            }
            Read(list, RegistryHive.CurrentUser, RegistryView.Registry64, Lang.T("目前使用者"));

            return list.GroupBy(a => (a.Name + "|" + a.Version).ToLowerInvariant())
                       .Select(g => g.First())
                       .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }

        private static void Read(List<InstalledApp> list, RegistryHive hive, RegistryView view, string scope)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var uninstall = root.OpenSubKey(UninstallKey))
                {
                    if (uninstall == null) return;

                    // 32 位元檢視下開的 SOFTWARE\... 實際位置在 WOW6432Node，reg.exe 備份要用實際路徑。
                    string realBase = hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem
                        ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                        : UninstallKey;

                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using (var key = uninstall.OpenSubKey(name))
                        {
                            var app = key == null ? null : FromKey(key, name);
                            if (app == null) continue;
                            app.Scope = scope;
                            app.Hive = hive;
                            app.View = view;
                            app.KeyPath = realBase + "\\" + name;
                            list.Add(app);
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        private static InstalledApp FromKey(RegistryKey key, string keyName)
        {
            string name = Str(key, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var app = new InstalledApp
            {
                Name = name.Trim(),
                Publisher = Str(key, "Publisher"),
                Version = Str(key, "DisplayVersion"),
                InstallLocation = Clean(Str(key, "InstallLocation")),
                UninstallString = Str(key, "UninstallString"),
                QuietUninstallString = Str(key, "QuietUninstallString"),
                ModifyPath = Str(key, "ModifyPath"),
                NoModify = Int(key, "NoModify") == 1,
                NoRepair = Int(key, "NoRepair") == 1,
                IsMsi = Int(key, "WindowsInstaller") == 1,
                Url = Str(key, "URLInfoAbout") ?? Str(key, "HelpLink"),
                RegistrySize = Math.Max(0, Int(key, "EstimatedSize")) * 1024L,
                SystemComponent = Int(key, "SystemComponent") == 1
                                  || !string.IsNullOrEmpty(Str(key, "ParentKeyName"))
                                  || IsUpdate(Str(key, "ReleaseType"))
            };

            if (app.IsMsi && keyName.StartsWith("{", StringComparison.Ordinal)) app.ProductCode = keyName;
            if (string.IsNullOrEmpty(app.UninstallString) && app.ProductCode == null) app.SystemComponent = true;

            DateTime date;
            string raw = Str(key, "InstallDate");
            if (raw != null && DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                app.InstallDate = date;
            }

            if (string.IsNullOrEmpty(app.InstallLocation) || !Directory.Exists(app.InstallLocation))
            {
                string guess = Guess(Str(key, "DisplayIcon")) ?? Guess(SplitCommand(app.UninstallString).Item1);
                if (guess != null)
                {
                    app.InstallLocation = guess;
                    app.LocationGuessed = true;
                }
            }
            return app;
        }

        private static bool IsUpdate(string releaseType)
        {
            return releaseType != null && (releaseType.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0
                                           || releaseType.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 登錄沒寫安裝路徑時，從圖示或解除安裝程式的位置推。
        /// 放在 Windows、Installer 快取、Package Cache 這些共用位置的不算（那不是程式本身的資料夾）。
        /// </summary>
        private static string Guess(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            file = Clean(file);
            int comma = file.LastIndexOf(',');
            if (comma > 2 && !File.Exists(file)) file = file.Substring(0, comma);   // "xxx.exe,0"

            string dir;
            try { dir = Path.GetDirectoryName(file); }
            catch (Exception) { return null; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || dir.Length <= 3) return null;

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string[] shared =
            {
                win, Path.Combine(common, "Package Cache"), Path.Combine(common, "Microsoft"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string s in shared)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (dir.Equals(s, StringComparison.OrdinalIgnoreCase)) return null;
                if (s != shared[3] && s != shared[4] && dir.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            }
            return dir;
        }

        private static string Clean(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd('\\');
        }

        private static string Str(RegistryKey key, string name)
        {
            var v = key.GetValue(name);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static int Int(RegistryKey key, string name)
        {
            var v = key.GetValue(name);
            if (v is int) return (int)v;
            int n;
            return v != null && int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? n : 0;
        }

        // ───────────────────────────── 命令列 ─────────────────────────────

        /// <summary>
        /// 把 UninstallString 拆成 (執行檔, 參數)。有引號照引號；沒引號就切在 .exe 之後（路徑常有空白又沒加引號），
        /// 但只找第一個引號之前的 .exe——「RunDll32 x.dll,LaunchSetup "...\setup.exe"」的執行檔是 RunDll32，不是後面那個。
        /// </summary>
        public static Tuple<string, string> SplitCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return Tuple.Create((string)null, string.Empty);
            command = Environment.ExpandEnvironmentVariables(command.Trim());

            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = command.IndexOf('"', 1);
                if (end > 0) return Tuple.Create(command.Substring(1, end - 1), command.Substring(end + 1).Trim());
            }

            int quote = command.IndexOf('"');
            string head = quote > 0 ? command.Substring(0, quote) : command;
            int exe = head.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) return Tuple.Create(command.Substring(0, exe + 4), command.Substring(exe + 4).Trim());

            int space = command.IndexOf(' ');
            return space > 0 ? Tuple.Create(command.Substring(0, space), command.Substring(space + 1)) : Tuple.Create(command, string.Empty);
        }

        /// <summary>
        /// 啟動解除安裝程式。用 ShellExecute 讓需要系統管理員的安裝程式自己跳 UAC。
        /// MSI 一律用 msiexec /X{GUID}（不少 MSI 的 UninstallString 寫的是 /I，那是「修改」）。
        /// </summary>
        public static Process StartUninstall(InstalledApp app)
        {
            if (app.IsMsi && app.ProductCode != null) return Start("msiexec.exe", "/X" + app.ProductCode);

            var cmd = SplitCommand(app.UninstallString);
            if (cmd.Item1 != null && cmd.Item1.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Start(cmd.Item1, System.Text.RegularExpressions.Regex.Replace(cmd.Item2, @"(?i)/I\s*\{", "/X{"));
            }
            return Start(cmd.Item1, cmd.Item2);
        }

        /// <summary>修改／修復：有 ModifyPath 用它，MSI 則開 msiexec /I（維護模式，可選修復或變更）。</summary>
        public static Process StartModify(InstalledApp app)
        {
            if (!string.IsNullOrEmpty(app.ModifyPath))
            {
                var cmd = SplitCommand(app.ModifyPath);
                return Start(cmd.Item1, cmd.Item2);
            }
            if (app.IsMsi && app.ProductCode != null) return Start("msiexec.exe", "/I" + app.ProductCode);
            return null;
        }

        private static Process Start(string file, string arguments)
        {
            if (string.IsNullOrEmpty(file)) throw new InvalidOperationException(Lang.T("登錄裡沒有寫解除安裝程式。"));
            return Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
        }

        // ───────────────────────────── 殘留項目 ─────────────────────────────

        /// <summary>先用 reg export 備份成 .reg（雙擊就能還原），再刪掉登錄項目。回傳備份檔路徑；失敗丟例外。</summary>
        public static string RemoveEntry(InstalledApp app)
        {
            string folder = Path.Combine(Settings.Folder, "RegistryBackup");
            Directory.CreateDirectory(folder);
            string safe = new string(app.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            string file = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safe + ".reg");

            var info = new ProcessStartInfo("reg.exe", string.Format("export \"{0}\" \"{1}\" /y", app.RegPath, file))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(info))
            {
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(file)) throw new InvalidOperationException(Lang.T("備份登錄項目失敗，沒有刪除。"));
            }

            using (var root = RegistryKey.OpenBaseKey(app.Hive, app.View))
            {
                // OpenBaseKey 搭配 View 會自己轉到 WOW6432Node，所以這裡用一般路徑。
                string relative = app.KeyPath.Replace(@"SOFTWARE\WOW6432Node\", @"SOFTWARE\");
                root.DeleteSubKeyTree(relative, false);
            }
            return file;
        }

        // ───────────────────────────── 資料夾大小 ─────────────────────────────

        public static long FolderSize(string dir, CancellationToken token)
        {
            long total = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(dir));
            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                var d = pending.Pop();
                try
                {
                    foreach (var f in d.EnumerateFiles())
                    {
                        try { total += f.Length; } catch (Exception) { }
                    }
                    foreach (var sub in d.EnumerateDirectories())
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                    }
                }
                catch (Exception) { }
            }
            return total;
        }
    }
}
