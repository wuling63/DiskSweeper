using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskCleaner
{
    public enum StartupSource { RegistryUser, RegistryMachine64, RegistryMachine32, FolderUser, FolderCommon, Task }

    public class StartupEntry
    {
        public string Name;
        public string Command;
        public string ExePath;
        public string Publisher;
        public bool Enabled;
        public bool Missing;           // 指向的程式已經不在
        public StartupSource Source;
        public string SourceText;
        public string ValuePath;       // 登錄：Run 機碼；資料夾：.lnk 完整路徑；工作：工作路徑
        public bool NeedsAdmin;
    }

    /// <summary>
    /// 開機啟動項目：登錄 Run、啟動資料夾、登入／開機時執行的排程工作。
    /// 啟用／停用跟工作管理員用同一套 StartupApproved 旗標（不刪項目，隨時可以改回來）；排程工作直接切換 Enabled。
    /// </summary>
    public static class StartupManager
    {
        private const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupEntry> Load()
        {
            var list = new List<StartupEntry>();
            ReadRun(list, RegistryHive.CurrentUser, RegistryView.Registry64, StartupSource.RegistryUser, Lang.T("登錄（目前使用者）"), "Run");
            ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, StartupSource.RegistryMachine64, Lang.T("登錄（所有使用者）"), "Run");
            if (Environment.Is64BitOperatingSystem)
            {
                ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, StartupSource.RegistryMachine32, Lang.T("登錄（所有使用者，32 位元）"), "Run32");
            }
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.FolderUser, Lang.T("啟動資料夾（目前使用者）"));
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.FolderCommon, Lang.T("啟動資料夾（所有使用者）"));
            ReadTasks(list);

            foreach (var e in list)
            {
                if (string.IsNullOrEmpty(e.ExePath)) continue;
                try
                {
                    if (File.Exists(e.ExePath)) e.Publisher = FileVersionInfo.GetVersionInfo(e.ExePath).CompanyName;
                    else if (Path.IsPathRooted(e.ExePath)) e.Missing = true;
                }
                catch (Exception) { }
            }
            return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ── 登錄 Run ──

        private static void ReadRun(List<StartupEntry> list, RegistryHive hive, RegistryView view, StartupSource source, string text, string approvedName)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var run = root.OpenSubKey(Run))
                using (var approved = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(Approved + approvedName))
                {
                    if (run == null) return;
                    foreach (string name in run.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        string command = Convert.ToString(run.GetValue(name));
                        list.Add(new StartupEntry
                        {
                            Name = name,
                            Command = command,
                            ExePath = InstalledApps.SplitCommand(command).Item1,
                            Enabled = IsApproved(approved, name),
                            Source = source,
                            SourceText = text,
                            NeedsAdmin = hive == RegistryHive.LocalMachine
                        });
                    }
                }
            }
            catch (Exception) { }
        }

        // ── 啟動資料夾 ──

        private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource source, string text)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            var hive = source == StartupSource.FolderUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            try
            {
                using (var approved = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(Approved + "StartupFolder"))
                {
                    foreach (string file in Directory.GetFiles(folder))
                    {
                        string name = Path.GetFileName(file);
                        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        string target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.Target(file) : file;
                        list.Add(new StartupEntry
                        {
                            Name = Path.GetFileNameWithoutExtension(file),
                            Command = target ?? file,
                            ExePath = target,
                            Enabled = IsApproved(approved, name),
                            Source = source,
                            SourceText = text,
                            ValuePath = file,
                            NeedsAdmin = hive == RegistryHive.LocalMachine
                        });
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// StartupApproved 的值是 12 位元組：第一個位元組是偶數（02、06）＝啟用，奇數（03、07）＝停用。
        /// 沒有這個值代表沒被工作管理員動過，也就是啟用。
        /// </summary>
        private static bool IsApproved(RegistryKey approved, string name)
        {
            var data = approved == null ? null : approved.GetValue(name) as byte[];
            return data == null || data.Length == 0 || data[0] % 2 == 0;
        }

        // ── 排程工作（登入或開機時執行的） ──

        private static void ReadTasks(List<StartupEntry> list)
        {
            object service = null;
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return;
                service = Activator.CreateInstance(type);
                dynamic svc = service;
                svc.Connect();
                WalkTasks(svc.GetFolder("\\"), list);
            }
            catch (Exception) { }
            finally
            {
                if (service != null) Marshal.ReleaseComObject(service);
            }
        }

        private static void WalkTasks(dynamic folder, List<StartupEntry> list)
        {
            string path = folder.Path;
            // 系統內建的一大堆工作都在 \Microsoft 底下，不列出來。
            if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

            foreach (dynamic task in folder.GetTasks(1))   // 1＝含隱藏的工作
            {
                try
                {
                    bool atStartup = false;
                    foreach (dynamic trigger in task.Definition.Triggers)
                    {
                        int t = trigger.Type;
                        if (t == 8 || t == 9) { atStartup = true; break; }   // 8＝開機、9＝登入
                    }
                    if (!atStartup) continue;

                    string exe = null, args = null;
                    foreach (dynamic action in task.Definition.Actions)
                    {
                        if ((int)action.Type == 0) { exe = Environment.ExpandEnvironmentVariables(((string)action.Path ?? string.Empty).Trim('"')); args = action.Arguments; break; }
                    }

                    list.Add(new StartupEntry
                    {
                        Name = task.Name,
                        Command = exe == null ? Lang.T("（非執行程式的動作）") : (exe + " " + args).Trim(),
                        ExePath = exe,
                        Enabled = task.Enabled,
                        Source = StartupSource.Task,
                        SourceText = Lang.T("排程工作（") + (string)task.Path + Lang.T("）"),
                        ValuePath = task.Path
                    });
                }
                catch (Exception) { }
            }

            foreach (dynamic sub in folder.GetFolders(0)) WalkTasks(sub, list);
        }

        // ── 切換 ──

        /// <summary>啟用或停用；所有使用者的項目需要系統管理員（沒權限會丟 UnauthorizedAccessException）。</summary>
        public static void SetEnabled(StartupEntry e, bool enabled)
        {
            if (e.Source == StartupSource.Task)
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                object service = Activator.CreateInstance(type);
                try
                {
                    dynamic svc = service;
                    svc.Connect();
                    string path = e.ValuePath;
                    int cut = path.LastIndexOf('\\');
                    dynamic folder = svc.GetFolder(cut <= 0 ? "\\" : path.Substring(0, cut));
                    dynamic task = folder.GetTask(path.Substring(cut + 1));
                    task.Enabled = enabled;
                }
                catch (COMException ex) when ((uint)ex.HResult == 0x80070005)
                {
                    throw new UnauthorizedAccessException(ex.Message, ex);
                }
                finally
                {
                    Marshal.ReleaseComObject(service);
                }
                e.Enabled = enabled;
                return;
            }

            string sub;
            string name = e.Name;
            switch (e.Source)
            {
                case StartupSource.RegistryMachine32: sub = "Run32"; break;
                case StartupSource.FolderUser:
                case StartupSource.FolderCommon: sub = "StartupFolder"; name = Path.GetFileName(e.ValuePath); break;
                default: sub = "Run"; break;
            }
            var hive = e.NeedsAdmin ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

            // 跟工作管理員寫一樣的格式：啟用 02 加 11 個 0；停用 03 加 3 個 0 再加停用時間（FILETIME）。
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);

            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var key = root.CreateSubKey(Approved + sub))
            {
                key.SetValue(name, data, RegistryValueKind.Binary);
            }
            e.Enabled = enabled;
        }

        /// <summary>移除指向不存在程式的項目：登錄值先備份成 .reg 再刪，捷徑送進資源回收筒，排程工作不刪（請用工作排程器）。</summary>
        public static string RemoveMissing(StartupEntry e)
        {
            if (e.Source == StartupSource.FolderUser || e.Source == StartupSource.FolderCommon)
            {
                RecycleBin.Send(new[] { e.ValuePath });
                if (File.Exists(e.ValuePath)) throw new UnauthorizedAccessException(Lang.T("沒有權限移除這個捷徑。"));
                return Lang.T("捷徑已送進資源回收筒");
            }
            if (e.Source == StartupSource.Task) throw new InvalidOperationException(Lang.T("排程工作請用 Windows 的「工作排程器」刪除。"));

            var hive = e.NeedsAdmin ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var view = e.Source == StartupSource.RegistryMachine32 ? RegistryView.Registry32 : RegistryView.Registry64;

            string folder = Path.Combine(Settings.Folder, "RegistryBackup");
            Directory.CreateDirectory(folder);
            string backup = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_startup_" + string.Concat(e.Name.Split(Path.GetInvalidFileNameChars())) + ".reg");
            string regPath = (hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE\\" : "HKEY_CURRENT_USER\\")
                           + (e.Source == StartupSource.RegistryMachine32 ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" : Run);
            File.WriteAllText(backup, "Windows Registry Editor Version 5.00\r\n\r\n[" + regPath + "]\r\n\"" + Escape(e.Name) + "\"=\"" + Escape(e.Command) + "\"\r\n",
                System.Text.Encoding.Unicode);

            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var key = root.OpenSubKey(Run, true))
            {
                if (key != null) key.DeleteValue(e.Name, false);
            }
            return Lang.T("已移除，備份：") + backup;
        }

        private static string Escape(string s)
        {
            return (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>開機啟動項目管理視窗。</summary>
    public class StartupForm : ToolWindow
    {
        private readonly ThemedListView _list;
        private readonly RoundButton _toggle, _remove;
        private List<StartupEntry> _entries = new List<StartupEntry>();

        public StartupForm() : base(Lang.T("開機啟動項目"), Lang.T("停用不需要開機就執行的程式，開機會比較快；跟工作管理員的「啟動應用程式」是同一套設定"), "", new Size(1060, 600))
        {
            _list = NewList(Lang.T("名稱:220"), Lang.T("狀態:70"), Lang.T("發行者:180"), Lang.T("來源:220"), Lang.T("命令:420"));
            _toggle = Primary(Lang.T("停用"), 110);
            _remove = Light(Lang.T("移除失效項目"), 130);
            var locate = Light(Lang.T("開啟檔案位置"), 130);
            var refresh = Light(Lang.T("重新整理"), 100);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(_toggle, locate, _remove, refresh));

            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _list.DoubleClick += delegate { Toggle(); };
            _toggle.Click += delegate { Toggle(); };
            _remove.Click += delegate { Remove(); };
            locate.Click += delegate
            {
                var e = Selected();
                if (e != null && e.ExePath != null && File.Exists(e.ExePath)) Process.Start("explorer.exe", "/select,\"" + e.ExePath + "\"");
            };
            refresh.Click += delegate { Reload(); };
            Load += delegate { Reload(); };
        }

        private StartupEntry Selected()
        {
            return _list.SelectedItems.Count == 0 ? null : (StartupEntry)_list.SelectedItems[0].Tag;
        }

        private void Reload()
        {
            var keep = Selected();
            _entries = StartupManager.Load();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in _entries)
            {
                var item = new ListViewItem(e.Name) { Tag = e };
                item.SubItems.Add(e.Enabled ? Lang.T("啟用") : Lang.T("已停用"));
                item.SubItems.Add(e.Publisher ?? string.Empty);
                item.SubItems.Add(e.SourceText);
                item.SubItems.Add(e.Command);
                if (e.Missing) item.ForeColor = Theme.Red;
                else if (!e.Enabled) item.ForeColor = Theme.TextMuted;
                _list.Items.Add(item);
                if (keep != null && keep.Name == e.Name && keep.Source == e.Source) item.Selected = true;
            }
            _list.EndUpdate();

            Status.Text = string.Format(Lang.T("共 {0} 個啟動項目，啟用 {1} 個、停用 {2} 個{3}"),
                _entries.Count, _entries.Count(e => e.Enabled), _entries.Count(e => !e.Enabled),
                _entries.Any(e => e.Missing) ? string.Format(Lang.T("；{0} 個指向的程式已不存在（紅字）"), _entries.Count(e => e.Missing)) : string.Empty);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var e = Selected();
            _toggle.Enabled = e != null;
            _toggle.Text = e != null && !e.Enabled ? Lang.T("啟用") : Lang.T("停用");
            _remove.Enabled = e != null && e.Missing && e.Source != StartupSource.Task;
        }

        private void Toggle()
        {
            var e = Selected();
            if (e == null) return;
            try
            {
                StartupManager.SetEnabled(e, !e.Enabled);
                Status.Text = string.Format(Lang.T("「{0}」已{1}，下次開機生效"), e.Name, e.Enabled ? Lang.T("啟用") : Lang.T("停用"));
                Reload();
            }
            catch (UnauthorizedAccessException)
            {
                Msg.Show(this, Lang.T("「所有使用者」的啟動項目需要系統管理員權限才能修改。\r\n請在主視窗用「檔案 → 以系統管理員身分重新啟動」。"),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("修改失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Remove()
        {
            var e = Selected();
            if (e == null || !e.Missing) return;
            if (Msg.Show(this, string.Format(Lang.T("「{0}」指向的程式已經不存在：\r\n{1}\r\n\r\n要移除這個啟動項目嗎？（登錄項目會先備份成 .reg）"), e.Name, e.ExePath),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                Status.Text = StartupManager.RemoveMissing(e);
                Reload();
            }
            catch (UnauthorizedAccessException)
            {
                Msg.Show(this, Lang.T("需要系統管理員權限才能移除這個項目。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("移除失敗：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
