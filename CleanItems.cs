using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiskCleaner
{
    /// <summary>內建的清理項目。順序就是畫面上的順序。</summary>
    public static partial class Cleaner
    {
        // 圖示索引，對應 MainForm 建的 ImageList。
        public const int IconFolder = 0, IconRecycle = 1, IconLink = 2, IconWorld = 3, IconShield = 4,
                         IconWarning = 5, IconImage = 6, IconDrive = 7, IconApp = 8, IconSettings = 9, IconDoc = 10,
                         IconKey = 11, IconLock = 12;

        public const string CatWindows = "Windows";
        public const string CatBrowser = "瀏覽器";
        public const string CatApps = "應用程式";
        public const string CatDev = "開發者";
        public const string CatPrivacy = "隱私";
        public const string CatCustom = "自訂";

        private static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        private static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        private static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        private static string Common { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        private static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        private static string LocalLow { get { return Path.Combine(Home, @"AppData\LocalLow"); } }

        /// <param name="devRoots">開發專案資料夾：__pycache__、.mypy_cache 這類只在這些地方找。</param>
        public static List<CleanItem> BuiltIn(IList<string> devRoots)
        {
            var items = new List<CleanItem>();
            items.AddRange(WindowsItems());
            items.AddRange(BrowserItems());
            items.AddRange(AppItems());
            items.AddRange(DevItems(devRoots ?? new List<string>()));
            items.AddRange(PrivacyItems());
            return items;
        }

        // ───────────────────────────── Windows ─────────────────────────────

        private static IEnumerable<CleanItem> WindowsItems()
        {
            string temp = Path.GetTempPath().TrimEnd('\\');
            string drive = Path.GetPathRoot(Win);

            yield return new CleanItem
            {
                Id = "win.systemp", Category = CatWindows, Name = Lang.T("系統暫存"), Icon = IconFolder,
                NeedsAdmin = true, RespectAge = true, RemoveEmptyDirs = true,
                Description = Win + @"\Temp",
                Rules = { new ScanRule(Win + @"\Temp") }
            };
            yield return new CleanItem
            {
                Id = "win.usertemp", Category = CatWindows, Name = Lang.T("使用者暫存"), Icon = IconFolder,
                RespectAge = true, RemoveEmptyDirs = true,
                Description = temp,
                Rules = { new ScanRule(temp) }
            };
            yield return new CleanItem
            {
                Id = "win.thumbcache", Category = CatWindows, Name = Lang.T("略圖快取"), Icon = IconImage,
                Description = Lang.T("檔案總管的縮圖與圖示快取（使用中的會略過，下次重開機後再清較完整）"),
                Rules =
                {
                    new ScanRule(Local + @"\Microsoft\Windows\Explorer", "thumbcache_*.db", false),
                    new ScanRule(Local + @"\Microsoft\Windows\Explorer", "iconcache_*.db", false)
                }
            };
            yield return new CleanItem
            {
                Id = "win.dumps", Category = CatWindows, Name = Lang.T("記憶體傾印檔案"), Icon = IconWarning,
                NeedsAdmin = true,
                Description = Lang.T("當機時寫出的 .dmp"),
                Rules =
                {
                    new ScanRule(Win, "MEMORY.DMP", false),
                    new ScanRule(Win + @"\Minidump", "*.dmp"),
                    new ScanRule(Win + @"\LiveKernelReports", "*.dmp"),
                    new ScanRule(Local + @"\CrashDumps", "*.dmp")
                }
            };
            yield return new CleanItem
            {
                Id = "win.wer", Category = CatWindows, Name = Lang.T("錯誤報告檔案"), Icon = IconWarning,
                RespectAge = true, RemoveEmptyDirs = true,
                Description = Lang.T("Windows 錯誤報告（WER）"),
                Rules =
                {
                    new ScanRule(Common + @"\Microsoft\Windows\WER\ReportArchive"),
                    new ScanRule(Common + @"\Microsoft\Windows\WER\ReportQueue"),
                    new ScanRule(Common + @"\Microsoft\Windows\WER\Temp"),
                    new ScanRule(Local + @"\Microsoft\Windows\WER\ReportArchive"),
                    new ScanRule(Local + @"\Microsoft\Windows\WER\ReportQueue")
                }
            };
            yield return new CleanItem
            {
                Id = "win.logs", Category = CatWindows, Name = Lang.T("系統日誌檔案"), Icon = IconDoc,
                NeedsAdmin = true, RespectAge = true,
                Description = Win + @"\Logs 與 Windows 資料夾下的 .log",
                Rules =
                {
                    new ScanRule(Win + @"\Logs", "*.log"),
                    new ScanRule(Win + @"\Logs", "*.cab"),
                    new ScanRule(Win, "*.log", false)
                }
            };
            yield return new CleanItem
            {
                Id = "win.rdpcache", Category = CatWindows, Name = Lang.T("遠端桌面快取"), Icon = IconDrive,
                DefaultChecked = false,
                Description = Lang.T("遠端桌面連線的點陣圖快取"),
                Rules = { new ScanRule(Local + @"\Microsoft\Terminal Server Client\Cache") }
            };
            yield return new CleanItem
            {
                Id = "win.recycle", Category = CatWindows, Name = Lang.T("資源回收筒"), Icon = IconRecycle,
                Kind = ItemKind.RecycleBin,
                Description = Lang.T("清空所有磁碟的資源回收筒（自動清理不會清）")
            };
            yield return new CleanItem
            {
                Id = "win.startmenu", Category = CatWindows, Name = Lang.T("開始選單無效的捷徑"), Icon = IconLink,
                Kind = ItemKind.BrokenShortcuts,
                Description = Lang.T("目標檔案已經不在本機磁碟上的捷徑"),
                Rules =
                {
                    new ScanRule(Roaming + @"\Microsoft\Windows\Start Menu", "*.lnk"),
                    new ScanRule(Common + @"\Microsoft\Windows\Start Menu", "*.lnk")
                }
            };
            yield return new CleanItem
            {
                Id = "win.desktop", Category = CatWindows, Name = Lang.T("桌面無效的捷徑"), Icon = IconLink,
                Kind = ItemKind.BrokenShortcuts, DefaultChecked = false,
                Description = Lang.T("桌面上目標已經不在的捷徑"),
                Rules =
                {
                    new ScanRule(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "*.lnk", false),
                    new ScanRule(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "*.lnk", false)
                }
            };
            yield return new CleanItem
            {
                Id = "win.iis", Category = CatWindows, Name = Lang.T("IIS 日誌檔案"), Icon = IconDoc,
                NeedsAdmin = true,
                Description = drive + @"inetpub\logs\LogFiles",
                Rules = { new ScanRule(drive + @"inetpub\logs\LogFiles", "*.log") }
            };
            yield return new CleanItem
            {
                Id = "win.prefetch", Category = CatWindows, Name = Lang.T("舊的預讀取數據"), Icon = IconApp,
                NeedsAdmin = true,
                Description = Lang.T("超過 30 天沒用到的 Prefetch（常用程式的留著，才不會拖慢開啟）"),
                Rules = { new ScanRule(Win + @"\Prefetch", "*.pf", false) { MinAgeDays = 30 } }
            };
            yield return new CleanItem
            {
                Id = "win.fontcache", Category = CatWindows, Name = Lang.T("字型快取"), Icon = IconDoc,
                NeedsAdmin = true, DefaultChecked = false,
                Description = Lang.T("字型快取服務的資料（服務執行中多半會被鎖住而略過）"),
                Rules = { new ScanRule(Win + @"\ServiceProfiles\LocalService\AppData\Local\FontCache") }
            };
            yield return new CleanItem
            {
                Id = "win.downloaded", Category = CatWindows, Name = Lang.T("下載的程式"), Icon = IconApp,
                Description = Win + @"\Downloaded Program Files（舊式 ActiveX）",
                Rules =
                {
                    new ScanRule(Win + @"\Downloaded Program Files")
                    {
                        Skip = p => Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                    }
                }
            };
            yield return new CleanItem
            {
                Id = "win.update", Category = CatWindows, Name = Lang.T("Windows 更新"), Icon = IconSettings,
                NeedsAdmin = true, RemoveEmptyDirs = true,
                Description = Lang.T("已下載的更新安裝檔（SoftwareDistribution\\Download），需要時 Windows 會重新下載"),
                Rules = { new ScanRule(Win + @"\SoftwareDistribution\Download") }
            };
            yield return new CleanItem
            {
                Id = "win.delivery", Category = CatWindows, Name = Lang.T("傳遞最佳化檔案"), Icon = IconSettings,
                NeedsAdmin = true, RemoveEmptyDirs = true,
                Description = Lang.T("Windows Update 分享給其他電腦用的快取"),
                Rules = { new ScanRule(Win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") }
            };
            yield return new CleanItem
            {
                Id = "win.installertemp", Category = CatWindows, Name = Lang.T("Windows 安裝程式暫存檔"), Icon = IconSettings,
                NeedsAdmin = true,
                Description = Lang.T("Windows Installer 留下的 .tmp（只清最上層的 .tmp，不碰安裝快取）"),
                Rules = { new ScanRule(Win + @"\Installer", "*.tmp", false) }
            };
            yield return new CleanItem
            {
                Id = "win.old", Category = CatWindows, Name = Lang.T("舊版 Windows（Windows.old）"), Icon = IconDrive,
                Kind = ItemKind.FolderTotal, NeedsAdmin = true, DefaultChecked = false,
                Target = drive + "Windows.old",
                Description = Lang.T("大型更新後留下的舊系統，刪了就不能退回舊版"),
                Warning = Lang.T("刪除 Windows.old 後就不能再退回之前的 Windows 版本，確定要勾選？")
            };
            yield return new CleanItem
            {
                Id = "win.components", Category = CatWindows, Name = Lang.T("Windows 元件存放區"), Icon = IconSettings,
                Kind = ItemKind.Command, NeedsAdmin = true, DefaultChecked = false,
                Target = Path.Combine(Environment.SystemDirectory, "dism.exe"),
                Arguments = "/Online /Cleanup-Image /StartComponentCleanup",
                Description = Lang.T("由 DISM 移除被取代的舊元件（大小執行完才知道，可能要好幾分鐘）"),
                Warning = Lang.T("清理元件存放區後，已安裝的更新就不能再解除安裝。確定要勾選？")
            };
        }

        // ───────────────────────────── 瀏覽器：只清快取 ─────────────────────────────

        private static IEnumerable<CleanItem> BrowserItems()
        {
            yield return new CleanItem
            {
                Id = "br.ie", Category = CatBrowser, Name = "Internet Explorer", Icon = IconWorld,
                RemoveEmptyDirs = true,
                Description = Lang.T("INetCache（IE 與部分 WebView 共用）"),
                Rules = { new ScanRule(Local + @"\Microsoft\Windows\INetCache") }
            };
            yield return ChromiumCache("br.edge", CatBrowser, Lang.T("Microsoft Edge 快取"), Local + @"\Microsoft\Edge\User Data", true);
            yield return ChromiumCache("br.chrome", CatBrowser, Lang.T("Google Chrome 快取"), Local + @"\Google\Chrome\User Data", true);
            yield return ChromiumCache("br.brave", CatBrowser, Lang.T("Brave 快取"), Local + @"\BraveSoftware\Brave-Browser\User Data", true);
            yield return new CleanItem
            {
                Id = "br.firefox", Category = CatBrowser, Name = Lang.T("Firefox 快取"), Icon = IconWorld,
                Description = Lang.T("各設定檔的 cache2"),
                Rules = { new ScanRule(Local + @"\Mozilla\Firefox\Profiles\*\cache2") }
            };
        }

        /// <summary>
        /// Chromium／Electron 系：Cache、Code Cache、GPUCache。
        /// perProfile＝瀏覽器那樣每個設定檔一個子資料夾（Default、Profile 1…）；Electron 程式則直接在根下面。
        /// </summary>
        private static CleanItem ChromiumCache(string id, string category, string name, string root, bool perProfile)
        {
            string p = perProfile ? root + @"\*" : root;
            return new CleanItem
            {
                Id = id, Category = category, Name = name, Icon = category == CatBrowser ? IconWorld : IconApp,
                Description = Lang.T("程式開著時使用中的快取檔會略過"),
                Rules =
                {
                    new ScanRule(p + @"\Cache"),
                    new ScanRule(p + @"\Code Cache"),
                    new ScanRule(p + @"\GPUCache")
                }
            };
        }

        // ───────────────────────────── 應用程式 ─────────────────────────────

        private static IEnumerable<CleanItem> AppItems()
        {
            var vscode = ChromiumCache("app.vscode", CatApps, Lang.T("VS Code 快取"), Roaming + @"\Code", false);
            vscode.Rules.Add(new ScanRule(Roaming + @"\Code\CachedData"));
            vscode.Rules.Add(new ScanRule(Roaming + @"\Code\CachedExtensionVSIXs"));
            vscode.Rules.Add(new ScanRule(Roaming + @"\Code\logs"));
            vscode.RemoveEmptyDirs = true;
            yield return vscode;

            yield return new CleanItem
            {
                Id = "app.vs", Category = CatApps, Name = Lang.T("Visual Studio 元件快取"), Icon = IconApp,
                RemoveEmptyDirs = true,
                Description = Lang.T("ComponentModelCache，VS 下次啟動會重建（VS 開著時會鎖住而略過）"),
                Rules = { new ScanRule(Local + @"\Microsoft\VisualStudio\*\ComponentModelCache") }
            };

            var teams = ChromiumCache("app.teams", CatApps, Lang.T("Microsoft Teams 快取"), Roaming + @"\Microsoft\Teams", false);
            teams.Rules.Add(new ScanRule(Roaming + @"\Microsoft\Teams\Service Worker\CacheStorage"));
            teams.Rules.Add(new ScanRule(Local + @"\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\Cache"));
            teams.Description = Lang.T("傳統版與新版 Teams 的網頁快取，不影響聊天紀錄");
            yield return teams;

            yield return ChromiumCache("app.discord", CatApps, Lang.T("Discord 快取"), Roaming + @"\discord", false);

            yield return new CleanItem
            {
                Id = "app.line", Category = CatApps, Name = Lang.T("LINE 快取"), Icon = IconApp,
                Description = Lang.T("圖片與貼圖快取，不影響聊天紀錄"),
                Rules = { new ScanRule(Local + @"\LINE\Cache") }
            };
            yield return new CleanItem
            {
                Id = "app.spotify", Category = CatApps, Name = Lang.T("Spotify 快取"), Icon = IconApp,
                DefaultChecked = false,
                Description = Lang.T("串流快取；下載供離線收聽的歌也放在這裡，清掉要重新下載"),
                Rules = { new ScanRule(Local + @"\Spotify\Storage"), new ScanRule(Local + @"\Spotify\Data") }
            };
            yield return new CleanItem
            {
                Id = "app.shader", Category = CatApps, Name = Lang.T("顯示卡著色器快取"), Icon = IconImage,
                RemoveEmptyDirs = true,
                Description = Lang.T("DirectX／NVIDIA／AMD／Intel 的著色器快取，清掉後遊戲第一次載入會稍慢"),
                Rules =
                {
                    new ScanRule(Local + @"\D3DSCache"),
                    new ScanRule(Local + @"\NVIDIA\DXCache"),
                    new ScanRule(Local + @"\NVIDIA\GLCache"),
                    new ScanRule(LocalLow + @"\NVIDIA\PerDriverVersion\DXCache"),
                    new ScanRule(Local + @"\AMD\DxCache"),
                    new ScanRule(Local + @"\AMD\DxcCache"),
                    new ScanRule(Local + @"\AMD\GLCache"),
                    new ScanRule(Local + @"\AMD\VkCache"),
                    new ScanRule(LocalLow + @"\Intel\ShaderCache")
                }
            };
        }

        // ───────────────────────────── 開發者 ─────────────────────────────

        private static IEnumerable<CleanItem> DevItems(IList<string> devRoots)
        {
            yield return new CleanItem
            {
                Id = "dev.pip", Category = CatDev, Name = Lang.T("pip 快取"), Icon = IconFolder,
                RemoveEmptyDirs = true,
                Description = Lang.T("pip 下載過的套件，需要時會重新下載"),
                Rules = { new ScanRule(Local + @"\pip\Cache") }
            };
            yield return new CleanItem
            {
                Id = "dev.nuget", Category = CatDev, Name = Lang.T("NuGet HTTP 快取"), Icon = IconFolder,
                RemoveEmptyDirs = true,
                Description = Lang.T("NuGet 的下載快取（v3-cache、plugins-cache），不影響已還原的套件"),
                Rules = { new ScanRule(Local + @"\NuGet\v3-cache"), new ScanRule(Local + @"\NuGet\plugins-cache") }
            };
            yield return new CleanItem
            {
                Id = "dev.nugetpkgs", Category = CatDev, Name = Lang.T("NuGet 全域套件"), Icon = IconFolder,
                DefaultChecked = false, RemoveEmptyDirs = true,
                Description = Home + @"\.nuget\packages；建置時會重新下載，沒網路時就建置不了",
                Warning = Lang.T("清掉 NuGet 全域套件後，每個專案下次建置都要重新下載套件，沒網路時無法建置。確定要勾選？"),
                Rules = { new ScanRule(Home + @"\.nuget\packages") }
            };
            yield return new CleanItem
            {
                Id = "dev.npm", Category = CatDev, Name = Lang.T("npm／Yarn 快取"), Icon = IconFolder,
                RemoveEmptyDirs = true,
                Description = Lang.T("npm-cache 與 Yarn 快取，需要時會重新下載"),
                Rules = { new ScanRule(Local + @"\npm-cache\_cacache"), new ScanRule(Local + @"\Yarn\Cache") }
            };
            yield return new CleanItem
            {
                Id = "dev.conda", Category = CatDev, Name = Lang.T("conda 安裝包"), Icon = IconFolder,
                Description = Lang.T("pkgs 裡下載的壓縮檔（等同 conda clean --tarballs），不動已解開的套件"),
                Rules = CondaRules()
            };

            string roots = devRoots.Count == 0 ? Lang.T("（還沒設定：設定 → 選項 → 開發專案資料夾）") : string.Join(Lang.T("、"), devRoots);
            var pycache = new CleanItem
            {
                Id = "dev.pycache", Category = CatDev, Name = "Python __pycache__", Icon = IconDoc,
                Description = Lang.T("專案資料夾裡的 .pyc 編譯快取，執行時會自動重建；搜尋範圍：") + roots
            };
            var tools = new CleanItem
            {
                Id = "dev.pytools", Category = CatDev, Name = Lang.T(".mypy_cache／.pytest_cache／.ruff_cache"), Icon = IconDoc,
                Description = Lang.T("型別檢查與測試工具的快取，下次執行會重建；搜尋範圍：") + roots
            };
            foreach (string root in devRoots)
            {
                pycache.Rules.Add(new ScanRule(root) { DirName = "__pycache__" });
                tools.Rules.Add(new ScanRule(root) { DirName = ".mypy_cache" });
                tools.Rules.Add(new ScanRule(root) { DirName = ".pytest_cache" });
                tools.Rules.Add(new ScanRule(root) { DirName = ".ruff_cache" });
            }
            yield return pycache;
            yield return tools;
        }

        private static List<ScanRule> CondaRules()
        {
            var rules = new List<ScanRule>();
            foreach (string dist in new[] { "anaconda3", "miniconda3", "miniforge3", "mambaforge" })
            {
                foreach (string root in new[] { Home, Local, Common })
                {
                    string pkgs = Path.Combine(root, dist, "pkgs");
                    rules.Add(new ScanRule(pkgs, "*.conda", false));
                    rules.Add(new ScanRule(pkgs, "*.tar.bz2", false));
                }
            }
            return rules;
        }

        // ───────────────────────────── 隱私（預設都不勾） ─────────────────────────────

        private static IEnumerable<CleanItem> PrivacyItems()
        {
            var browsers = new[]
            {
                Local + @"\Microsoft\Edge\User Data",
                Local + @"\Google\Chrome\User Data",
                Local + @"\BraveSoftware\Brave-Browser\User Data"
            };

            var history = new CleanItem
            {
                Id = "priv.history", Category = CatPrivacy, Name = Lang.T("瀏覽器歷史紀錄"), Icon = IconLock,
                Privacy = true, DefaultChecked = false,
                Description = Lang.T("Edge／Chrome／Brave 的瀏覽紀錄、常用網站（書籤另存，不受影響；Firefox 的紀錄與書籤同檔，不清）。請先關閉瀏覽器"),
                Warning = Lang.T("會刪除 Edge、Chrome、Brave 的瀏覽歷史紀錄（書籤不受影響）。\r\n瀏覽器開著時檔案會被鎖住而略過，請先關閉瀏覽器。確定要勾選？")
            };
            var cookies = new CleanItem
            {
                Id = "priv.cookies", Category = CatPrivacy, Name = Lang.T("瀏覽器 Cookie"), Icon = IconLock,
                Privacy = true, DefaultChecked = false,
                Description = Lang.T("Edge／Chrome／Brave／Firefox 的 Cookie；清掉後各網站都要重新登入。請先關閉瀏覽器"),
                Warning = Lang.T("清除 Cookie 後，所有網站都會被登出，需要重新登入。\r\n瀏覽器開著時檔案會被鎖住而略過，請先關閉瀏覽器。確定要勾選？")
            };

            foreach (string b in browsers)
            {
                foreach (string file in new[] { "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal" })
                {
                    history.Rules.Add(new ScanRule(b + @"\*", file, false));
                }
                cookies.Rules.Add(new ScanRule(b + @"\*\Network", "Cookies*", false));
                cookies.Rules.Add(new ScanRule(b + @"\*", "Cookies*", false));
            }
            cookies.Rules.Add(new ScanRule(Roaming + @"\Mozilla\Firefox\Profiles\*", "cookies.sqlite*", false));

            yield return history;
            yield return cookies;
            yield return new CleanItem
            {
                Id = "priv.recent", Category = CatPrivacy, Name = Lang.T("最近使用的檔案"), Icon = IconLock,
                Privacy = true, DefaultChecked = false,
                Description = Lang.T("檔案總管「最近使用的檔案」與工作列跳躍清單（不會刪到原本的檔案）"),
                Warning = Lang.T("會清掉「最近使用的檔案」與工作列跳躍清單（釘選的項目也會一起消失），原本的檔案不受影響。確定要勾選？"),
                Rules =
                {
                    new ScanRule(Roaming + @"\Microsoft\Windows\Recent", "*.lnk", false),
                    new ScanRule(Roaming + @"\Microsoft\Windows\Recent\AutomaticDestinations", "*", false),
                    new ScanRule(Roaming + @"\Microsoft\Windows\Recent\CustomDestinations", "*", false),
                    new ScanRule(Roaming + @"\Microsoft\Office\Recent", "*.lnk", false)
                }
            };
            yield return new CleanItem
            {
                Id = "priv.runmru", Category = CatPrivacy, Name = Lang.T("執行紀錄（Win+R）"), Icon = IconKey,
                Kind = ItemKind.RegistryValues, Privacy = true, DefaultChecked = false,
                Target = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
                Description = Lang.T("「執行」對話框輸入過的指令"),
                Warning = Lang.T("會清掉「執行」（Win+R）對話框的歷史指令。確定要勾選？")
            };
            yield return new CleanItem
            {
                Id = "priv.search", Category = CatPrivacy, Name = Lang.T("檔案總管搜尋與網址列紀錄"), Icon = IconKey,
                Kind = ItemKind.RegistryValues, Privacy = true, DefaultChecked = false,
                Target = @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery;Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
                Description = Lang.T("檔案總管搜尋框打過的字、網址列輸入過的路徑"),
                Warning = Lang.T("會清掉檔案總管的搜尋紀錄與網址列輸入過的路徑。確定要勾選？")
            };
            yield return new CleanItem
            {
                Id = "priv.office", Category = CatPrivacy, Name = Lang.T("Office 最近使用的檔案"), Icon = IconKey,
                Kind = ItemKind.RegistryValues, Privacy = true, DefaultChecked = false,
                Target = "office:",
                Description = Lang.T("Word、Excel、PowerPoint 等「最近」清單（檔案本身不受影響；請先關閉 Office）"),
                Warning = Lang.T("會清掉 Office 各程式「開啟舊檔」裡的最近清單（釘選的也會清掉），檔案本身不受影響。\r\n請先關閉 Office。確定要勾選？")
            };
            yield return new CleanItem
            {
                Id = "priv.clipboard", Category = CatPrivacy, Name = Lang.T("剪貼簿"), Icon = IconKey,
                Kind = ItemKind.Clipboard, Privacy = true, DefaultChecked = false,
                Description = Lang.T("清空目前複製的內容（Win+V 剪貼簿歷程記錄請在 Windows 設定裡清除）"),
                Warning = Lang.T("會清空目前複製的內容。確定要勾選？")
            };
        }

        // ───────────────────────────── 自訂 ─────────────────────────────

        /// <summary>使用者自訂的資料夾集中成一個項目。</summary>
        public static CleanItem Custom(IEnumerable<CustomFolder> folders)
        {
            var item = new CleanItem
            {
                Id = "custom", Category = CatCustom, Name = Lang.T("自訂檔案和資料夾"), Icon = IconFolder,
                RemoveEmptyDirs = true,
                Description = Lang.T("在「自訂義檔案和資料夾」加入的位置")
            };
            foreach (var f in folders)
            {
                if (IsProtected(f.Path)) continue;

                // 樣式可以用分號寫好幾個，例如「*.log;*.bak」，各拆成一條規則。
                var patterns = (f.Pattern ?? "*").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (patterns.Count == 0) patterns.Add("*");
                foreach (string pattern in patterns) item.Rules.Add(new ScanRule(f.Path, pattern, f.Recursive));
            }
            return item;
        }
    }
}
