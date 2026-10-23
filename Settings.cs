using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DiskCleaner
{
    public class CustomFolder
    {
        public string Path { get; set; }
        public string Pattern { get; set; }
        public bool Recursive { get; set; }
    }

    /// <summary>一次清理的紀錄。</summary>
    public class HistoryEntry
    {
        public DateTime Time { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>
    /// 勾選狀態、忽略清單、自訂資料夾與選項，以及清理歷史。
    /// 存在 %LOCALAPPDATA%\DiskCleaner\settings.json 與 history.json。
    /// </summary>
    public class Settings
    {
        public Dictionary<string, bool> Checked { get; set; }
        public List<string> Ignore { get; set; }
        public List<CustomFolder> CustomFolders { get; set; }

        /// <summary>暫存類只清超過幾小時沒動的檔案；0 代表不限。</summary>
        public int MinAgeHours { get; set; }

        /// <summary>清理時送進資源回收筒而不是直接刪除。</summary>
        public bool UseRecycleBin { get; set; }

        /// <summary>開發專案資料夾：__pycache__、.mypy_cache 這類只在這些地方找。</summary>
        public List<string> DevRoots { get; set; }

        // 排程（實際排程存在 Windows 工作排程器，這裡只記使用者選的值，好在選項畫面還原）。
        public bool AutoClean { get; set; }
        public DayOfWeek AutoCleanDay { get; set; }
        public string AutoCleanTime { get; set; }
        public bool LowSpaceCheck { get; set; }

        /// <summary>外觀："system"（跟著 Windows）、"light"、"dark"。</summary>
        public string Appearance { get; set; }

        /// <summary>介面語言："zh" 或 "en"，重新啟動後生效。</summary>
        public string Language { get; set; }

        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }

        /// <summary>清理前先把要刪的檔案備份成 zip，保留 BackupDays 天。</summary>
        public bool BackupBeforeClean { get; set; }
        public int BackupDays { get; set; }

        [JsonIgnore]
        public List<HistoryEntry> History { get; private set; }

        public Settings()
        {
            Checked = new Dictionary<string, bool>();
            Ignore = new List<string>();
            CustomFolders = new List<CustomFolder>();
            MinAgeHours = 24;
            DevRoots = new List<string>();
            AutoCleanDay = DayOfWeek.Sunday;
            AutoCleanTime = "21:00";
            Appearance = "system";
            Language = "zh";
            BackupDays = 7;
            History = new List<HistoryEntry>();
        }

        public static string Folder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner");
            }
        }

        private static string SettingsPath { get { return Path.Combine(Folder, "settings.json"); } }
        private static string HistoryPath { get { return Path.Combine(Folder, "history.json"); } }

        public static Settings Load()
        {
            Settings settings = null;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsPath));
                }
            }
            catch (Exception) { }

            if (settings == null) settings = new Settings();
            if (settings.Checked == null) settings.Checked = new Dictionary<string, bool>();
            if (settings.DevRoots == null) settings.DevRoots = new List<string>();
            if (string.IsNullOrEmpty(settings.AutoCleanTime)) settings.AutoCleanTime = "21:00";
            if (string.IsNullOrEmpty(settings.Appearance)) settings.Appearance = "system";
            if (string.IsNullOrEmpty(settings.Language)) settings.Language = "zh";
            if (settings.BackupDays < 1 || settings.BackupDays > 90) settings.BackupDays = 7;
            if (settings.Ignore == null) settings.Ignore = new List<string>();
            if (settings.CustomFolders == null) settings.CustomFolders = new List<CustomFolder>();
            if (settings.MinAgeHours < 0) settings.MinAgeHours = 24;

            try
            {
                if (File.Exists(HistoryPath))
                {
                    settings.History = JsonConvert.DeserializeObject<List<HistoryEntry>>(File.ReadAllText(HistoryPath))
                                       ?? new List<HistoryEntry>();
                }
            }
            catch (Exception) { }

            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
                File.WriteAllText(HistoryPath, JsonConvert.SerializeObject(History, Formatting.Indented));
            }
            catch (Exception) { }
        }

        [JsonIgnore]
        public long TodayBytes
        {
            get { return History.Where(h => h.Time.Date == DateTime.Today).Sum(h => h.Bytes); }
        }

        [JsonIgnore]
        public long TotalBytes
        {
            get { return History.Sum(h => h.Bytes); }
        }
    }

    public static class Format
    {
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes / 1024.0;
            if (v < 1024) return v.ToString("0.##") + " KB";
            v /= 1024;
            if (v < 1024) return v.ToString("0.##") + " MB";
            v /= 1024;
            return v.ToString("0.##") + " GB";
        }
    }
}
