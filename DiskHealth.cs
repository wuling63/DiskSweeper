using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    public class PhysicalDiskInfo
    {
        public string Number;
        public string Name;
        public string MediaType;       // SSD／HDD
        public string Bus;             // NVMe、SATA、USB…
        public long Size;
        public int Health;             // 0 健康、1 警告、2 不良、其他未知
        public string Letters;         // C: D:
        // 可靠性計數器（要系統管理員才讀得到；讀不到是 null）
        public int? Temperature, TemperatureMax, Wear;
        public long? PowerOnHours, ReadErrors, WriteErrors;
        public bool CountersDenied;

        public string HealthText
        {
            get { return Health == 0 ? Lang.T("健康") : Health == 1 ? Lang.T("警告") : Health == 2 ? Lang.T("不良") : Lang.T("未知"); }
        }
    }

    /// <summary>
    /// 磁碟健康：Windows 儲存管理的 WMI（root\Microsoft\Windows\Storage）。
    /// MSFT_PhysicalDisk 給健康狀態、類型、介面；關聯的 MSFT_StorageReliabilityCounter 給溫度、
    /// SSD 已用壽命（Wear）、開機時數、讀寫錯誤——這部分需要系統管理員。
    /// </summary>
    public static class DiskHealth
    {
        private const string Scope = @"\\.\root\Microsoft\Windows\Storage";

        public static List<PhysicalDiskInfo> Load()
        {
            var list = new List<PhysicalDiskInfo>();
            var scope = new ManagementScope(Scope);
            var letters = DriveLetters(scope);

            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    var d = new PhysicalDiskInfo
                    {
                        Number = Convert.ToString(o["DeviceId"]),
                        Name = (string)o["FriendlyName"],
                        MediaType = MediaText(Convert.ToInt32(o["MediaType"] ?? 0)),
                        Bus = BusText(Convert.ToInt32(o["BusType"] ?? 0)),
                        Size = Convert.ToInt64(o["Size"] ?? 0L),
                        Health = Convert.ToInt32(o["HealthStatus"] ?? 5)
                    };
                    string l;
                    d.Letters = letters.TryGetValue(d.Number, out l) ? l : string.Empty;

                    try
                    {
                        foreach (ManagementObject c in o.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            d.Temperature = NullableInt(c["Temperature"]);
                            d.TemperatureMax = NullableInt(c["TemperatureMax"]);
                            d.Wear = NullableInt(c["Wear"]);
                            d.PowerOnHours = NullableLong(c["PowerOnHours"]);
                            d.ReadErrors = NullableLong(c["ReadErrorsTotal"]);
                            d.WriteErrors = NullableLong(c["WriteErrorsTotal"]);
                        }
                    }
                    catch (ManagementException) { d.CountersDenied = true; }
                    catch (UnauthorizedAccessException) { d.CountersDenied = true; }
                    list.Add(d);
                }
            }
            return list.OrderBy(d => d.Number).ToList();
        }

        /// <summary>磁碟編號 → 「C: D:」。</summary>
        private static Dictionary<string, string> DriveLetters(ManagementScope scope)
        {
            var map = new Dictionary<string, string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                {
                    foreach (ManagementObject p in searcher.Get())
                    {
                        char letter = Convert.ToChar(p["DriveLetter"] ?? '\0');
                        if (letter == '\0') continue;
                        string disk = Convert.ToString(p["DiskNumber"]);
                        string text;
                        map[disk] = map.TryGetValue(disk, out text) ? text + " " + letter + ":" : letter + ":";
                    }
                }
            }
            catch (Exception) { }
            return map;
        }

        private static int? NullableInt(object v) { return v == null ? (int?)null : Convert.ToInt32(v); }
        private static long? NullableLong(object v) { return v == null ? (long?)null : Convert.ToInt64(v); }

        private static string MediaText(int t)
        {
            return t == 4 ? "SSD" : t == 3 ? "HDD" : t == 5 ? "SCM" : Lang.T("未知");
        }

        private static string BusText(int t)
        {
            switch (t)
            {
                case 1: return "SCSI";
                case 3: return "ATA";
                case 6: return "Fibre Channel";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 15: return Lang.T("虛擬");
                case 17: return "NVMe";
                default: return Lang.T("其他");
            }
        }
    }

    public class DiskHealthForm : ToolWindow
    {
        private readonly ThemedListView _list;
        private readonly bool _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public DiskHealthForm() : base(Lang.T("磁碟健康"), Lang.T("每顆實體磁碟的健康狀態、溫度、SSD 已用壽命與錯誤次數（SMART）"), "\uEDA2", new Size(1040, 440))
        {
            _list = NewList(Lang.T("磁碟:240"), Lang.T("代號:80"), Lang.T("類型:60"), Lang.T("介面:70"), Lang.T("容量:90:r"),
                Lang.T("健康狀態:80"), Lang.T("溫度:70:r"), Lang.T("已用壽命:80:r"), Lang.T("開機時數:90:r"), Lang.T("讀寫錯誤:90:r"));
            var refresh = Light(Lang.T("重新整理"), 100);
            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(refresh));
            refresh.Click += delegate { Reload(); };
            Load += delegate { Reload(); };
        }

        private async void Reload()
        {
            Status.Text = Lang.T("讀取中…");
            List<PhysicalDiskInfo> disks;
            try
            {
                disks = await Task.Run(() => DiskHealth.Load());
            }
            catch (Exception ex)
            {
                Status.Text = Lang.T("讀取失敗：") + ex.Message;
                return;
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var d in disks)
            {
                var item = new ListViewItem(d.Name) { Tag = d };
                item.SubItems.Add(d.Letters);
                item.SubItems.Add(d.MediaType);
                item.SubItems.Add(d.Bus);
                item.SubItems.Add(Format.Size(d.Size));
                item.SubItems.Add(d.HealthText);
                item.SubItems.Add(d.Temperature.HasValue && d.Temperature > 0 ? d.Temperature + " °C" : "－");
                item.SubItems.Add(d.Wear.HasValue && d.MediaType == "SSD" ? d.Wear + "%" : "－");
                item.SubItems.Add(d.PowerOnHours.HasValue ? d.PowerOnHours.Value.ToString("N0") : "－");
                item.SubItems.Add(d.ReadErrors.HasValue || d.WriteErrors.HasValue ? ((d.ReadErrors ?? 0) + (d.WriteErrors ?? 0)).ToString("N0") : "－");
                if (d.Health == 2 || (d.Wear ?? 0) >= 90) item.ForeColor = Theme.Red;
                else if (d.Health == 1 || (d.Wear ?? 0) >= 70 || (d.Temperature ?? 0) >= 70) item.ForeColor = Theme.Orange;
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            int bad = disks.Count(d => d.Health != 0);
            Status.Text = string.Format(Lang.T("{0} 顆磁碟，{1}{2}"), disks.Count,
                bad == 0 ? Lang.T("全部健康") : string.Format(Lang.T("{0} 顆有警告，請盡快備份資料"), bad),
                _isAdmin ? string.Empty : Lang.T("　│　溫度、壽命、錯誤次數需要系統管理員權限才讀得到"));
        }
    }
}
