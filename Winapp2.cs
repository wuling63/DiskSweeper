using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskCleaner
{
    /// <summary>
    /// 社群清理規則 winapp2.ini（CCleaner／BleachBit 共用的格式，https://github.com/MoscaDotTo/Winapp2，CC-BY-SA 4.0）。
    /// 為了安全只取一部分：
    ///   • 只收名稱看得出是快取、日誌、暫存、當機傾印、著色器的規則；歷史、Cookie、自動填入、密碼、設定類一律不收
    ///   • 只收偵測得到（Detect／DetectFile 成立）的程式
    ///   • 只做檔案清理，RegKey（刪登錄）不執行；展開後落在受保護位置的路徑跳過
    /// 收進來的項目放在「社群規則」分類，預設不勾，排程與快速清理不碰。
    /// </summary>
    public static class Winapp2
    {
        public const string DownloadUrl = "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";

        public static string FilePath
        {
            get { return Path.Combine(Settings.Folder, "winapp2.ini"); }
        }

        private static readonly string[] SafeWords = { "cache", "temp", "log", "crash", "dump", "shader", "thumbnail", "update", "installer", "download" };
        private static readonly string[] RiskyWords =
        {
            "history", "cookie", "autofill", "password", "login", "session", "setting", "preference", "bookmark",
            "recent", "mru", "save", "favorite", "backup", "profile", "account", "database", "cloud", "sync", "note",
            "template", "wallet", "license", "mail", "message", "chat"
        };

        public class ParseResult
        {
            public List<CleanItem> Items = new List<CleanItem>();
            public int Total;           // 檔案裡全部的規則數
            public int Unsafe;          // 名稱不像快取類而不收的
            public int NotInstalled;    // 偵測不到程式的
        }

        /// <summary>讀已匯入的 winapp2.ini（沒有就回空的）。</summary>
        public static ParseResult LoadInstalled()
        {
            try
            {
                if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception) { }
            return new ParseResult();
        }

        public static ParseResult Parse(string text)
        {
            var result = new ParseResult();
            string name = null;
            var keys = new List<KeyValuePair<string, string>>();

            Action flush = () =>
            {
                if (name == null) return;
                result.Total++;
                var item = Build(name, keys, result);
                if (item != null) result.Items.Add(item);
            };

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    flush();
                    name = line.Substring(1, line.Length - 2).Trim();
                    if (name.EndsWith(" *", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2).Trim();
                    keys = new List<KeyValuePair<string, string>>();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && name != null) keys.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
            }
            flush();

            result.Items = result.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private static CleanItem Build(string name, List<KeyValuePair<string, string>> keys, ParseResult result)
        {
            string lower = name.ToLowerInvariant();
            if (!SafeWords.Any(w => lower.Contains(w)) || RiskyWords.Any(w => lower.Contains(w)))
            {
                result.Unsafe++;
                return null;
            }

            var detects = keys.Where(k => Starts(k.Key, "Detect") && !Starts(k.Key, "DetectFile") && !Starts(k.Key, "DetectOS")).Select(k => k.Value).ToList();
            var detectFiles = keys.Where(k => Starts(k.Key, "DetectFile")).Select(k => k.Value).ToList();
            var special = keys.Where(k => Starts(k.Key, "SpecialDetect")).Select(k => k.Value).ToList();

            bool hasDetect = detects.Count > 0 || detectFiles.Count > 0 || special.Count > 0;
            bool detected = !hasDetect
                            || detects.Any(RegistryExists)
                            || detectFiles.Any(PathExists)
                            || special.Any(SpecialExists);
            if (!detected)
            {
                result.NotInstalled++;
                return null;
            }

            var excludes = keys.Where(k => Starts(k.Key, "ExcludeKey")).Select(k => k.Value).ToList();
            Func<string, bool> skip = BuildExclude(excludes);

            var item = new CleanItem
            {
                Id = "w2." + Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-'),
                Category = Cleaner.CatCommunity,
                Name = name,
                Icon = Cleaner.IconWorld,
                Community = true,
                DefaultChecked = false,
                RespectAge = true,          // 跟內建暫存項目一樣，只清超過 N 小時沒動的檔案
                RemoveEmptyDirs = false,
                Description = Lang.T("社群規則（winapp2.ini）：") + name
            };

            foreach (var key in keys.Where(k => Starts(k.Key, "FileKey")))
            {
                var parts = key.Value.Split('|');
                if (parts.Length == 0 || parts[0].Length == 0) continue;
                string patterns = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "*";
                string flags = parts.Length > 2 ? parts[2].ToUpperInvariant() : string.Empty;
                bool recursive = flags.Contains("RECURSE") || flags.Contains("REMOVESELF");

                foreach (string path in ExpandVariables(parts[0]))
                {
                    // 展開後落在受保護位置（磁碟根目錄、Windows、Program Files、使用者資料夾本身…）的不收。
                    if (Cleaner.IsProtected(path.Replace("*", string.Empty).TrimEnd('\\'))) continue;
                    foreach (string pattern in patterns.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
                    {
                        if (IsBlocked(path, pattern)) continue;
                        item.Rules.Add(new ScanRule(path, pattern, recursive) { Skip = skip });
                    }
                }
            }

            if (item.Rules.Count == 0) return null;   // 只有 RegKey 的規則不收
            return item;
        }

        /// <summary>
        /// 社群規則也不准碰的地方：事件記錄（winevt）、System32／SysWOW64 根目錄；
        /// Windows\Installer 只准刪 .tmp（其他是安裝快取，刪了程式就不能解除安裝或修復）。
        /// </summary>
        private static bool IsBlocked(string path, string pattern)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            string p = path.TrimEnd('\\');
            Func<string, bool> under = d => p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);

            if (under(win + @"\System32\winevt")) return true;
            if (p.Equals(win + @"\System32", StringComparison.OrdinalIgnoreCase) || p.Equals(win + @"\SysWOW64", StringComparison.OrdinalIgnoreCase)) return true;
            if (under(win + @"\Installer") && !pattern.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Starts(string key, string prefix)
        {
            return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>winapp2 的變數換成實際路徑；%ProgramFiles% 同時代表 64 與 32 位元兩個資料夾。</summary>
        public static IEnumerable<string> ExpandVariables(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "%LocalAppData%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) },
                { "%AppData%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) },
                { "%LocalLowAppData%", Path.Combine(home, @"AppData\LocalLow") },
                { "%ProgramData%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) },
                { "%CommonAppData%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) },
                { "%UserProfile%", home },
                { "%Documents%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                { "%WinDir%", Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
                { "%SystemDrive%", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\') },
                { "%CommonProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles) },
                { "%Public%", Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty },
                { "%Temp%", Path.GetTempPath().TrimEnd('\\') },
                { "%UserName%", Environment.UserName }
            };

            var bases = new List<string> { path };
            if (path.IndexOf("%ProgramFiles%", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                bases = new List<string> { Replace(path, "%ProgramFiles%", pf) };
                if (!string.IsNullOrEmpty(pf86) && pf86 != pf) bases.Add(Replace(path, "%ProgramFiles%", pf86));
            }

            foreach (string b in bases)
            {
                string p = b;
                foreach (var kv in map) p = Replace(p, kv.Key, kv.Value);
                if (p.IndexOf('%') >= 0) continue;   // 還有不認識的變數，不猜
                yield return p;
            }
        }

        private static string Replace(string s, string from, string to)
        {
            return Regex.Replace(s, Regex.Escape(from), (to ?? string.Empty).Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        private static bool RegistryExists(string path)
        {
            try
            {
                int slash = path.IndexOf('\\');
                if (slash < 0) return false;
                string hive = path.Substring(0, slash).ToUpperInvariant(), sub = path.Substring(slash + 1);
                RegistryHive h;
                if (hive == "HKCU" || hive == "HKEY_CURRENT_USER") h = RegistryHive.CurrentUser;
                else if (hive == "HKLM" || hive == "HKEY_LOCAL_MACHINE") h = RegistryHive.LocalMachine;
                else if (hive == "HKCR" || hive == "HKEY_CLASSES_ROOT") h = RegistryHive.ClassesRoot;
                else return false;

                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var root = RegistryKey.OpenBaseKey(h, view))
                    using (var key = root.OpenSubKey(sub))
                    {
                        if (key != null) return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>DetectFile 可能是資料夾也可能是檔案，路徑裡也可能有萬用字元。</summary>
        private static bool PathExists(string raw)
        {
            foreach (string path in ExpandVariables(raw))
            {
                if (Cleaner.ExpandRoot(path).Any()) return true;
                try
                {
                    string dir = Path.GetDirectoryName(path), file = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) continue;
                    foreach (string d in Cleaner.ExpandRoot(dir))
                    {
                        if (Directory.GetFiles(d, file).Length > 0) return true;
                    }
                }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>SpecialDetect 是 CCleaner 內建的瀏覽器偵測代號，這裡只認得幾個常見的。</summary>
        private static bool SpecialExists(string code)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            switch ((code ?? string.Empty).ToUpperInvariant())
            {
                case "DET_CHROME": return Directory.Exists(Path.Combine(local, @"Google\Chrome"));
                case "DET_MOZILLA": return Directory.Exists(Path.Combine(roaming, @"Mozilla\Firefox"));
                case "DET_THUNDERBIRD": return Directory.Exists(Path.Combine(roaming, "Thunderbird"));
                case "DET_OPERA": return Directory.Exists(Path.Combine(roaming, "Opera Software"));
                default: return false;
            }
        }

        /// <summary>ExcludeKey：FILE|資料夾|樣式（只比那一層）、PATH|資料夾|樣式（含子資料夾）；REG 不處理。</summary>
        private static Func<string, bool> BuildExclude(List<string> excludes)
        {
            var rules = new List<Tuple<string, Regex, bool>>();
            foreach (string e in excludes)
            {
                var parts = e.Split('|');
                if (parts.Length < 2) continue;
                string kind = parts[0].Trim().ToUpperInvariant();
                if (kind != "FILE" && kind != "PATH") continue;
                string pattern = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "*";
                var regex = WildcardRegex(pattern);
                foreach (string dir in ExpandVariables(parts[1])) rules.Add(Tuple.Create(dir.TrimEnd('\\'), regex, kind == "PATH"));
            }
            if (rules.Count == 0) return null;

            return file =>
            {
                string dir = Path.GetDirectoryName(file) ?? string.Empty;
                string name = Path.GetFileName(file);
                foreach (var r in rules)
                {
                    bool under = r.Item3 ? (dir.Equals(r.Item1, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(r.Item1 + "\\", StringComparison.OrdinalIgnoreCase))
                                         : dir.Equals(r.Item1, StringComparison.OrdinalIgnoreCase);
                    if (under && r.Item2.IsMatch(name)) return true;
                }
                return false;
            };
        }

        public static Regex WildcardRegex(string pattern)
        {
            string body = string.Join("|", pattern.Split(';').Select(p => "^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"));
            return new Regex(body, RegexOptions.IgnoreCase);
        }

        /// <summary>從 GitHub 下載最新版存到設定資料夾；回傳錯誤訊息，成功回 null。</summary>
        public static async Task<string> DownloadAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("DiskSweeper");
                    string text = await http.GetStringAsync(DownloadUrl).ConfigureAwait(false);
                    if (!text.Contains("FileKey1=")) return Lang.T("下載的內容不是 winapp2.ini");
                    Directory.CreateDirectory(Settings.Folder);
                    File.WriteAllText(FilePath, text, Encoding.UTF8);
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>社群清理規則的管理對話框：從檔案匯入、從 GitHub 下載、移除。</summary>
    public class Winapp2Dialog : DialogBase
    {
        private readonly Label _info = new Label { Dock = DockStyle.Top, Height = 110, Font = Theme.Ui };
        public bool Changed { get; private set; }

        public Winapp2Dialog() : base(Lang.T("社群清理規則"), new Size(600, 360), false)
        {
            Glyph = "";
            Subtitle = Lang.T("winapp2.ini：CCleaner／BleachBit 共用的社群規則（CC-BY-SA 4.0）");

            var download = new RoundButton { Text = Lang.T("從 GitHub 下載最新版"), Size = new Size(190, 38) };
            var import = new RoundButton { Text = Lang.T("從檔案匯入…"), Size = new Size(140, 38), Style = RoundButton.Kind.Light, Font = Theme.Ui };
            var remove = new RoundButton { Text = Lang.T("移除"), Size = new Size(90, 38), Style = RoundButton.Kind.Light, Font = Theme.Ui };
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50 };
            row.Controls.AddRange(new Control[] { download, import, remove });

            var note = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted,
                Text = Lang.T("為了安全只收快取、日誌、暫存、當機傾印、著色器類的規則，而且只收偵測得到的程式；歷史紀錄、Cookie、自動填入、密碼、設定類一律不收，也不執行刪除登錄（RegKey）。收進來的項目在「社群規則」分類，預設不勾，排程與快速清理不碰。")
            };

            Body.Controls.Add(note);
            Body.Controls.Add(row);
            Body.Controls.Add(_info);
            Refresh_();

            download.Click += async delegate
            {
                download.Enabled = false;
                _info.Text = Lang.T("下載中…");
                string error = await Winapp2.DownloadAsync();
                download.Enabled = true;
                if (error != null) Msg.Show(this, Lang.T("下載失敗：") + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else Changed = true;
                Refresh_();
            };
            import.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Filter = "winapp2.ini|*.ini", Title = Lang.T("選擇 winapp2.ini") })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        Directory.CreateDirectory(Settings.Folder);
                        File.Copy(dlg.FileName, Winapp2.FilePath, true);
                        Changed = true;
                    }
                    catch (Exception ex)
                    {
                        Msg.Show(this, Lang.T("匯入失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    Refresh_();
                }
            };
            remove.Click += delegate
            {
                if (!File.Exists(Winapp2.FilePath)) return;
                try { File.Delete(Winapp2.FilePath); Changed = true; } catch (Exception) { }
                Refresh_();
            };
        }

        private void Refresh_()
        {
            if (!File.Exists(Winapp2.FilePath))
            {
                _info.Text = Lang.T("目前沒有匯入社群規則。");
                return;
            }
            var r = Winapp2.LoadInstalled();
            _info.Text = string.Format(Lang.T("已匯入：{0}（{1:yyyy/MM/dd}）\r\n檔案裡共 {2:N0} 條規則\r\n收進清單 {3:N0} 條（偵測到已安裝的快取類程式）\r\n不像快取類而不收 {4:N0} 條、沒安裝 {5:N0} 條"),
                Winapp2.FilePath, File.GetLastWriteTime(Winapp2.FilePath), r.Total, r.Items.Count, r.Unsafe, r.NotInstalled);
        }
    }
}
