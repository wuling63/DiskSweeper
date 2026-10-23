using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    public class ServiceInfo
    {
        public string Name;
        public string DisplayName;
        public string State;          // Running / Stopped …
        public string StartMode;      // Auto / Manual / Disabled
        public bool DelayedStart;
        public string Path;
        public string Exe;
        public string Company;
        public string Description;
        public bool IsMicrosoft;
    }

    /// <summary>
    /// 背景服務：列出服務的狀態、啟動類型、發行者。預設只顯示非微軟的服務（微軟的大多不該動）。
    /// 改啟動類型或啟停需要系統管理員，而且每次都要確認。
    /// </summary>
    public class ServicesForm : ToolWindow
    {
        private readonly ThemedListView _list;
        private readonly CheckBox _showMs = new CheckBox { Text = Lang.T("顯示 Microsoft 服務"), AutoSize = true };
        private readonly ComboBox _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly RoundButton _apply, _start, _stop;
        private readonly bool _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        private List<ServiceInfo> _all = new List<ServiceInfo>();

        public ServicesForm() : base(Lang.T("背景服務"), Lang.T("非微軟的背景服務；不常用的可以改成「手動」，需要時 Windows 才啟動它"), "", new Size(1080, 620))
        {
            _list = NewList(Lang.T("顯示名稱:260"), Lang.T("狀態:80"), Lang.T("啟動類型:100"), Lang.T("發行者:180"), Lang.T("說明:440"));
            _mode.Items.AddRange(new object[] { Lang.T("自動"), Lang.T("自動（延遲）"), Lang.T("手動"), Lang.T("停用") });
            _apply = Primary(Lang.T("套用啟動類型"), 130);
            _start = Light(Lang.T("啟動"), 80);
            _stop = Light(Lang.T("停止"), 80);
            var locate = Light(Lang.T("開啟檔案位置"), 130);
            var refresh = Light(Lang.T("重新整理"), 100);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(new Label { Text = Lang.T("啟動類型"), AutoSize = true }, _mode, _apply, _start, _stop, locate, refresh, _showMs));

            _list.SelectedIndexChanged += delegate { OnSelect(); };
            _showMs.CheckedChanged += delegate { Fill(); };
            _apply.Click += delegate { ApplyMode(); };
            _start.Click += delegate { StartStop(true); };
            _stop.Click += delegate { StartStop(false); };
            locate.Click += delegate
            {
                var s = Selected();
                if (s != null && s.Exe != null && File.Exists(s.Exe)) Process.Start("explorer.exe", "/select,\"" + s.Exe + "\"");
            };
            refresh.Click += delegate { Reload(); };
            Load += delegate { Reload(); };
        }

        private ServiceInfo Selected()
        {
            return _list.SelectedItems.Count == 0 ? null : (ServiceInfo)_list.SelectedItems[0].Tag;
        }

        private async void Reload()
        {
            Status.Text = Lang.T("讀取服務中…");
            _all = await Task.Run(() => Load_());
            Fill();
        }

        /// <summary>用 WMI 拿啟動類型與執行檔路徑，再讀執行檔的公司名稱判斷是不是微軟的。</summary>
        private static List<ServiceInfo> Load_()
        {
            var list = new List<ServiceInfo>();
            var delayed = DelayedStartNames();
            using (var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, PathName, Description FROM Win32_Service"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    var s = new ServiceInfo
                    {
                        Name = (string)o["Name"],
                        DisplayName = (string)o["DisplayName"],
                        State = (string)o["State"],
                        StartMode = (string)o["StartMode"],
                        Path = (string)o["PathName"],
                        Description = (string)o["Description"]
                    };
                    s.DelayedStart = delayed.Contains(s.Name);
                    s.Exe = InstalledApps.SplitCommand(s.Path).Item1;
                    try
                    {
                        if (s.Exe != null && File.Exists(s.Exe)) s.Company = FileVersionInfo.GetVersionInfo(s.Exe).CompanyName;
                    }
                    catch (Exception) { }

                    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    s.IsMicrosoft = (s.Company ?? string.Empty).IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0
                                    || (s.Company == null && s.Exe != null && s.Exe.StartsWith(win, StringComparison.OrdinalIgnoreCase));
                    list.Add(s);
                }
            }
            return list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static HashSet<string> DelayedStartNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    foreach (string name in services.GetSubKeyNames())
                    {
                        using (var key = services.OpenSubKey(name))
                        {
                            if (key != null && key.GetValue("DelayedAutostart") is int && (int)key.GetValue("DelayedAutostart") == 1) set.Add(name);
                        }
                    }
                }
            }
            catch (Exception) { }
            return set;
        }

        private static string ModeText(ServiceInfo s)
        {
            switch (s.StartMode)
            {
                case "Auto": return s.DelayedStart ? Lang.T("自動（延遲）") : Lang.T("自動");
                case "Manual": return Lang.T("手動");
                case "Disabled": return Lang.T("停用");
                case "Boot": return Lang.T("開機");
                case "System": return Lang.T("系統");
                default: return s.StartMode;
            }
        }

        private static string StateText(string state)
        {
            switch (state)
            {
                case "Running": return Lang.T("執行中");
                case "Stopped": return Lang.T("已停止");
                case "Paused": return Lang.T("已暫停");
                case "Start Pending": return Lang.T("啟動中");
                case "Stop Pending": return Lang.T("停止中");
                default: return state;
            }
        }

        private void Fill()
        {
            var keep = Selected();
            var shown = _all.Where(s => _showMs.Checked || !s.IsMicrosoft).ToList();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var s in shown)
            {
                var item = new ListViewItem(s.DisplayName) { Tag = s };
                item.SubItems.Add(StateText(s.State));
                item.SubItems.Add(ModeText(s));
                item.SubItems.Add(s.Company ?? string.Empty);
                item.SubItems.Add((s.Description ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
                if (s.State == "Running") item.ForeColor = Theme.AccentDark;
                _list.Items.Add(item);
                if (keep != null && keep.Name == s.Name) item.Selected = true;
            }
            _list.EndUpdate();

            Status.Text = string.Format(Lang.T("顯示 {0} 個服務（執行中 {1} 個）{2}"), shown.Count, shown.Count(s => s.State == "Running"),
                _isAdmin ? string.Empty : Lang.T("　│　一般權限：只能檢視，修改需要系統管理員"));
            OnSelect();
        }

        private void OnSelect()
        {
            var s = Selected();
            bool ok = s != null && _isAdmin;
            _mode.Enabled = _apply.Enabled = ok;
            _start.Enabled = ok && s.State == "Stopped" && s.StartMode != "Disabled";
            _stop.Enabled = ok && s.State == "Running";
            if (s != null) _mode.SelectedItem = ModeText(s);
        }

        private void ApplyMode()
        {
            var s = Selected();
            if (s == null || _mode.SelectedItem == null) return;
            string target = (string)_mode.SelectedItem;
            if (target == ModeText(s)) return;

            string warn = s.IsMicrosoft ? Lang.T("\r\n\r\n⚠ 這是 Microsoft 的服務，改錯可能讓 Windows 功能失常。") : string.Empty;
            if (Msg.Show(this, string.Format(Lang.T("把「{0}」的啟動類型從「{1}」改成「{2}」？{3}"), s.DisplayName, ModeText(s), target, warn),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            string mode = target == Lang.T("停用") ? "disabled" : target == Lang.T("手動") ? "demand" : target == Lang.T("自動（延遲）") ? "delayed-auto" : "auto";
            string error = RunSc(string.Format("config \"{0}\" start= {1}", s.Name, mode));
            if (error != null) Msg.Show(this, Lang.T("修改失敗：") + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Status.Text = string.Format(Lang.T("「{0}」已改成「{1}」"), s.DisplayName, target);
            Reload();
        }

        private async void StartStop(bool start)
        {
            var s = Selected();
            if (s == null) return;
            if (Msg.Show(this, string.Format(Lang.T("{0}「{1}」？"), start ? Lang.T("啟動") : Lang.T("停止"), s.DisplayName), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Status.Text = (start ? Lang.T("啟動") : Lang.T("停止")) + Lang.T("中…");
            try
            {
                await Task.Run(() =>
                {
                    using (var sc = new ServiceController(s.Name))
                    {
                        if (start) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                        else { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                    }
                });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Msg.Show(this, (start ? Lang.T("啟動") : Lang.T("停止")) + Lang.T("失敗：") + inner.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Reload();
        }

        private static string RunSc(string arguments)
        {
            var info = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(info))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? null : output.Trim();
            }
        }
    }
}
