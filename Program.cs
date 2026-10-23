using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DiskCleaner
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), Lang.T("未預期的錯誤"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // 語言要在建立任何畫面、讀任何字串之前決定。
            Lang.Init(Settings.Load().Language);

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
            if (mode == "/auto") { Background.AutoClean(); return; }
            if (mode == "/lowspace") { Background.LowSpaceCheck(); return; }

            Application.Run(new MainForm());
        }
    }

    /// <summary>排程執行的兩種模式：沒有視窗，做完在系統匣跳通知就結束。</summary>
    internal static class Background
    {
        /// <summary>系統磁碟剩不到這個比例就警告。</summary>
        public const double LowSpaceRatio = 0.10;

        /// <summary>
        /// 用使用者勾選的項目清理，但只清檔案類：不清空回收筒、不碰隱私、不跑 DISM／Windows.old。
        /// 結果記進歷史，另外寫一行到 auto.log。
        /// </summary>
        public static void AutoClean()
        {
            var settings = Settings.Load();
            SpaceLog.Record();
            var result = SafeClean(settings, CancellationToken.None);
            string message = string.Format(Lang.T("自動清理完成：刪除 {0:N0} 個檔案，釋放 {1}{2}"), result.Deleted, Format.Size(result.Bytes),
                result.Failed > 0 ? string.Format(Lang.T("；{0:N0} 個使用中已略過"), result.Failed) : string.Empty);
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(Path.Combine(Settings.Folder, "auto.log"),
                    DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }

            Notify(Lang.T("磁碟清理"), message + LowSpaceNote(), ToolTipIcon.Info);
        }

        /// <summary>
        /// 排程自動清理與系統匣「快速清理」共用：用勾選的項目，但只清檔案類
        /// （不清空回收筒、不碰隱私、不跑 DISM／Windows.old）。有設定就先備份，結果記進歷史。
        /// </summary>
        public static CleanResult SafeClean(Settings settings, CancellationToken token)
        {
            var items = Cleaner.BuiltIn(settings.DevRoots);
            if (settings.CustomFolders.Count > 0) items.Add(Cleaner.Custom(settings.CustomFolders));

            var picked = items.Where(i => i.SafeForAuto && IsChecked(settings, i)).ToList();
            var options = new ScanOptions { MinAgeHours = settings.MinAgeHours, Ignore = settings.Ignore };
            Action<string> none = delegate { };

            foreach (var item in picked) Cleaner.Scan(item, options, token, none);
            var found = picked.Where(i => i.Found.Count > 0).ToList();
            if (settings.BackupBeforeClean && !settings.UseRecycleBin)
            {
                Backup.Purge(settings.BackupDays);
                Backup.Create(found, token, none);
            }
            var result = Cleaner.Clean(found, settings.UseRecycleBin, token, none);

            if (result.Deleted > 0)
            {
                settings.History.Add(new HistoryEntry { Time = DateTime.Now, Files = result.Deleted, Bytes = result.Bytes, Failed = result.Failed });
                settings.Save();
            }
            return result;
        }

        /// <summary>系統磁碟空間不足才通知，夠的話安靜結束。</summary>
        public static void LowSpaceCheck()
        {
            SpaceLog.Record();
            string note = LowSpaceNote();
            if (note.Length > 0) Notify(Lang.T("磁碟空間不足"), note.Trim() + Lang.T("，點這裡開啟磁碟清理"), ToolTipIcon.Warning);
        }

        /// <summary>系統磁碟剩下的空間；不足時回傳一句提醒，足夠時回傳空字串。</summary>
        public static string LowSpaceNote()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                double ratio = (double)drive.AvailableFreeSpace / drive.TotalSize;
                if (ratio >= LowSpaceRatio) return string.Empty;
                return string.Format(Lang.T("\r\n{0} 只剩 {1}（{2:0}%）"), drive.Name.TrimEnd('\\'), Format.Size(drive.AvailableFreeSpace), ratio * 100);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsChecked(Settings settings, CleanItem item)
        {
            bool on;
            return settings.Checked.TryGetValue(item.Id, out on) ? on : item.DefaultChecked;
        }

        /// <summary>系統匣通知；點通知會開主視窗。顯示 15 秒後結束。</summary>
        private static void Notify(string title, string text, ToolTipIcon icon)
        {
            using (var tray = new NotifyIcon { Icon = AppIcon.Get(), Text = Lang.T("磁碟清理"), Visible = true })
            using (var timer = new System.Windows.Forms.Timer { Interval = 15000 })
            {
                tray.BalloonTipClicked += delegate
                {
                    System.Diagnostics.Process.Start(Application.ExecutablePath);
                    Application.ExitThread();
                };
                timer.Tick += delegate { Application.ExitThread(); };
                tray.ShowBalloonTip(15000, title, text, icon);
                timer.Start();
                Application.Run();
                tray.Visible = false;
            }
        }
    }

    /// <summary>
    /// 程式圖示：內嵌的 app.ico（跟執行檔圖示同一個檔，16～256 都有），視窗與系統匣共用。
    /// 讀不到時退回系統預設的應用程式圖示。
    /// </summary>
    internal static class AppIcon
    {
        private static Icon _icon;

        public static Icon Get()
        {
            if (_icon != null) return _icon;
            try
            {
                using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("DiskCleaner.app.ico"))
                {
                    if (stream != null) _icon = new Icon(stream);
                }
            }
            catch (Exception) { }
            return _icon ?? (_icon = SystemIcons.Application);
        }
    }
}
