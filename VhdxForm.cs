using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskCleaner
{
    public class VirtualDisk
    {
        public string Name;          // WSL 發行版名稱，或 Docker 的用途
        public string Kind;          // WSL／Docker
        public string Path;
        public long Size;
        public DateTime Modified;
        public string Distro;        // 可以在裡面跑 fstrim 的 WSL 發行版（Docker 的不跑）
    }

    /// <summary>
    /// WSL／Docker 的虛擬磁碟（.vhdx）只會長大不會縮：Linux 裡刪掉的檔案，Windows 這邊的檔案大小不會變小。
    /// 壓縮步驟：（可選）在發行版裡 fstrim 標記空白區 → wsl --shutdown → diskpart「compact vdisk」。
    /// Windows 家用版沒有 Optimize-VHD，所以用 diskpart；需要系統管理員，Docker Desktop 要先結束。
    /// </summary>
    public static class VirtualDisks
    {
        public static List<VirtualDisk> Find()
        {
            var list = new List<VirtualDisk>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<VirtualDisk> add = d =>
            {
                if (!File.Exists(d.Path) || !seen.Add(d.Path)) return;
                var info = new FileInfo(d.Path);
                d.Size = info.Length;
                d.Modified = info.LastWriteTime;
                list.Add(d);
            };

            // WSL 發行版登記在 HKCU\...\Lxss\{guid}：DistributionName、BasePath（資料夾，裡面是 ext4.vhdx）。
            try
            {
                using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (lxss != null)
                    {
                        foreach (string id in lxss.GetSubKeyNames())
                        {
                            using (var key = lxss.OpenSubKey(id))
                            {
                                string name = key == null ? null : key.GetValue("DistributionName") as string;
                                string basePath = key == null ? null : key.GetValue("BasePath") as string;
                                if (string.IsNullOrEmpty(basePath)) continue;
                                if (basePath.StartsWith(@"\\?\", StringComparison.Ordinal)) basePath = basePath.Substring(4);
                                string vhdx = key.GetValue("VhdFileName") as string ?? "ext4.vhdx";
                                bool docker = (name ?? string.Empty).StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);
                                add(new VirtualDisk
                                {
                                    Name = name ?? id,
                                    Kind = docker ? "Docker" : "WSL",
                                    Path = System.IO.Path.Combine(basePath, vhdx),
                                    Distro = docker ? null : name
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            // Docker Desktop 的資料磁碟（新舊版位置不同）。
            string docker2 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Docker\wsl");
            if (Directory.Exists(docker2))
            {
                try
                {
                    foreach (string f in Directory.GetFiles(docker2, "*.vhdx", SearchOption.AllDirectories))
                    {
                        add(new VirtualDisk { Name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f)), Kind = "Docker", Path = f });
                    }
                }
                catch (Exception) { }
            }
            return list.OrderByDescending(d => d.Size).ToList();
        }

        /// <summary>跑一個命令列程式（不開視窗），回傳 (結束代碼, 輸出)。</summary>
        public static Tuple<int, string> Run(string file, string args, int timeoutMs)
        {
            var info = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                // wsl.exe 的輸出是 UTF-16
                StandardOutputEncoding = file.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase) ? Encoding.Unicode : null
            };
            using (var p = Process.Start(info))
            {
                var output = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return Tuple.Create(-1, output + "(timeout)"); }
                p.WaitForExit();
                return Tuple.Create(p.ExitCode, output.ToString());
            }
        }

        /// <summary>檔案有沒有被別人開著（WSL 還在跑時 vhdx 是鎖住的）。</summary>
        public static bool IsLocked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>用 diskpart 壓縮一個 vhdx：唯讀掛上 → compact → 卸下。回傳 diskpart 的輸出。</summary>
        public static Tuple<int, string> Compact(string vhdx)
        {
            string script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "disksweeper_compact.txt");
            File.WriteAllText(script,
                "select vdisk file=\"" + vhdx + "\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n",
                Encoding.Default);
            try { return Run("diskpart.exe", "/s \"" + script + "\"", 60 * 60 * 1000); }
            finally { try { File.Delete(script); } catch (Exception) { } }
        }
    }

    public class VhdxForm : ToolWindow
    {
        private readonly ThemedListView _list;
        private readonly CheckBox _trim = new CheckBox { Text = Lang.T("壓縮前先在 Linux 裡執行 fstrim（建議，壓得比較多）"), AutoSize = true, Checked = true };
        private readonly RoundButton _compact;
        private readonly bool _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        private bool _busy;

        public VhdxForm() : base(Lang.T("WSL／Docker 虛擬磁碟"), Lang.T("Linux 裡刪掉的檔案不會讓虛擬磁碟變小；壓縮可以把空出來的空間還給 Windows"), "\uE8B7", new Size(960, 520))
        {
            _list = NewList(Lang.T("名稱:200"), Lang.T("類型:80"), Lang.T("大小:110:r"), Lang.T("最後修改:140"), Lang.T("路徑:400"));
            _list.CheckBoxes = true;
            _compact = Primary(Lang.T("壓縮勾選的磁碟"), 160);
            var open = Light(Lang.T("開啟資料夾"), 110);
            var refresh = Light(Lang.T("重新整理"), 100);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(_compact, open, refresh, _trim));

            _compact.Click += delegate { CompactSelected(); };
            open.Click += delegate
            {
                if (_list.SelectedItems.Count == 0) return;
                string path = ((VirtualDisk)_list.SelectedItems[0].Tag).Path;
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            };
            refresh.Click += delegate { Reload(); };
            Load += delegate { Reload(); };
        }

        private void Reload()
        {
            var disks = VirtualDisks.Find();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var d in disks)
            {
                var item = new ListViewItem(d.Name) { Tag = d, Checked = true };
                item.SubItems.Add(d.Kind);
                item.SubItems.Add(Format.Size(d.Size));
                item.SubItems.Add(d.Modified.ToString("yyyy/MM/dd HH:mm"));
                item.SubItems.Add(d.Path);
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            Status.Text = disks.Count == 0
                ? Lang.T("沒有找到 WSL 或 Docker 的虛擬磁碟")
                : string.Format(Lang.T("找到 {0} 個虛擬磁碟，共 {1}{2}"), disks.Count, Format.Size(disks.Sum(d => d.Size)),
                    _isAdmin ? string.Empty : Lang.T("　│　壓縮需要系統管理員權限（檔案 → 以系統管理員身分重新啟動）"));
            _compact.Enabled = disks.Count > 0 && _isAdmin;
        }

        private async void CompactSelected()
        {
            if (_busy) return;
            var picked = _list.CheckedItems.Cast<ListViewItem>().Select(i => (VirtualDisk)i.Tag).ToList();
            if (picked.Count == 0) return;

            if (Process.GetProcessesByName("Docker Desktop").Length > 0 && picked.Any(d => d.Kind == "Docker"))
            {
                Msg.Show(this, Lang.T("Docker Desktop 正在執行，請先從系統匣結束 Docker Desktop 再壓縮。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Msg.Show(this, string.Format(Lang.T("壓縮 {0} 個虛擬磁碟（共 {1}）？\r\n\r\n會先關閉所有 WSL 發行版（wsl --shutdown），正在 Linux 裡執行的程式會被中斷。\r\n大的磁碟可能要好幾分鐘。"),
                    picked.Count, Format.Size(picked.Sum(d => d.Size))), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            _busy = true;
            _compact.Enabled = false;
            bool trim = _trim.Checked;
            var progress = new Progress<string>(t => Status.Text = t);
            IProgress<string> report = progress;
            var log = new StringBuilder();
            long before = picked.Sum(d => d.Size);

            await Task.Run(() =>
            {
                if (trim)
                {
                    foreach (var d in picked.Where(x => x.Distro != null))
                    {
                        report.Report(string.Format(Lang.T("在 {0} 裡執行 fstrim…"), d.Distro));
                        var r = VirtualDisks.Run("wsl.exe", "-d \"" + d.Distro + "\" -u root -e fstrim -av", 5 * 60 * 1000);
                        log.AppendLine("fstrim " + d.Distro + ": " + r.Item2.Trim());
                    }
                }

                report.Report(Lang.T("關閉 WSL（wsl --shutdown）…"));
                VirtualDisks.Run("wsl.exe", "--shutdown", 60 * 1000);
                // 等虛擬磁碟真的被放開（最多 30 秒）
                for (int i = 0; i < 30 && picked.Any(d => VirtualDisks.IsLocked(d.Path)); i++) System.Threading.Thread.Sleep(1000);

                foreach (var d in picked)
                {
                    if (VirtualDisks.IsLocked(d.Path)) { log.AppendLine(d.Path + ": " + Lang.T("還在使用中，略過")); continue; }
                    report.Report(string.Format(Lang.T("壓縮 {0}（{1}）…"), d.Name, Format.Size(d.Size)));
                    var r = VirtualDisks.Compact(d.Path);
                    log.AppendLine(d.Path + ": exit " + r.Item1);
                }
            });

            Reload();
            long after = _list.Items.Cast<ListViewItem>().Select(i => (VirtualDisk)i.Tag).Where(d => picked.Any(p => p.Path == d.Path)).Sum(d => d.Size);
            Status.Text = string.Format(Lang.T("壓縮完成：{0} → {1}，省下 {2}"), Format.Size(before), Format.Size(after), Format.Size(Math.Max(0, before - after)));
            _busy = false;
        }
    }
}
