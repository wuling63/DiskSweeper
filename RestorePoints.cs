using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    public class RestorePoint
    {
        public int Sequence;
        public string Description;
        public DateTime Created;
        public string Type;
    }

    /// <summary>
    /// 系統還原點：列出還原點與陰影複製佔用的空間，可以刪除舊的還原點。
    /// 讀寫都需要系統管理員。刪除用 srclient.dll 的 SRRemoveRestorePoint（跟「系統保護」裡的刪除是同一個機制）。
    /// </summary>
    public class RestorePointsForm : ToolWindow
    {
        [DllImport("srclient.dll")]
        private static extern int SRRemoveRestorePoint(int sequence);

        private readonly ThemedListView _list;
        private readonly RoundButton _deleteOld, _delete;
        private readonly Label _usage = new Label { AutoSize = true, ForeColor = Theme.TextMuted };
        private readonly bool _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public RestorePointsForm() : base(Lang.T("系統還原點"), Lang.T("還原點可以把 Windows 退回之前的狀態，但很佔空間；保留最新一個通常就夠"), "\uE777", new Size(860, 520))
        {
            _list = NewList(Lang.T("建立時間:170"), Lang.T("說明:420"), Lang.T("類型:150"));
            _deleteOld = Primary(Lang.T("只保留最新一個"), 150);
            _delete = Light(Lang.T("刪除選取"), 110);
            var settings = Light(Lang.T("系統保護設定…"), 140);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(_deleteOld, _delete, settings, _usage));

            _deleteOld.Click += delegate { DeleteAllButLatest(); };
            _delete.Click += delegate { DeleteSelected(); };
            settings.Click += delegate { Process.Start("SystemPropertiesProtection.exe"); };
            Load += delegate { Reload(); };
        }

        private async void Reload()
        {
            if (!_isAdmin)
            {
                _deleteOld.Enabled = _delete.Enabled = false;
                Status.Text = Lang.T("讀取與刪除還原點需要系統管理員權限：請在主視窗用「檔案 → 以系統管理員身分重新啟動」。");
                return;
            }

            Status.Text = Lang.T("讀取中…");
            List<RestorePoint> points = null;
            string usage = null;
            try
            {
                points = await Task.Run(() => LoadPoints());
                usage = await Task.Run(() => ShadowUsage());
            }
            catch (Exception ex)
            {
                Status.Text = Lang.T("讀取失敗：") + ex.Message;
                return;
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in points.OrderByDescending(p => p.Created))
            {
                var item = new ListViewItem(p.Created.ToString("yyyy/MM/dd HH:mm")) { Tag = p };
                item.SubItems.Add(p.Description);
                item.SubItems.Add(p.Type);
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            _usage.Text = usage ?? string.Empty;
            _deleteOld.Enabled = points.Count > 1;
            Status.Text = points.Count == 0 ? Lang.T("沒有還原點（或系統保護沒有開啟）") : string.Format(Lang.T("共 {0} 個還原點"), points.Count);
        }

        private static List<RestorePoint> LoadPoints()
        {
            var list = new List<RestorePoint>();
            var scope = new ManagementScope(@"\\.\root\default");
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM SystemRestore")))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    list.Add(new RestorePoint
                    {
                        Sequence = Convert.ToInt32(o["SequenceNumber"]),
                        Description = (string)o["Description"],
                        Created = ManagementDateTimeConverter.ToDateTime((string)o["CreationTime"]),
                        Type = TypeText(Convert.ToInt32(o["RestorePointType"]))
                    });
                }
            }
            return list;
        }

        private static string TypeText(int type)
        {
            switch (type)
            {
                case 0: return Lang.T("安裝應用程式");
                case 1: return Lang.T("解除安裝應用程式");
                case 10: return Lang.T("安裝驅動程式");
                case 12: return Lang.T("修改設定");
                case 13: return Lang.T("取消操作");
                default: return Lang.T("系統（") + type + Lang.T("）");
            }
        }

        /// <summary>陰影複製（還原點的實際資料）用了多少空間、上限多少。</summary>
        private static string ShadowUsage()
        {
            long used = 0, max = 0;
            using (var searcher = new ManagementObjectSearcher("SELECT UsedSpace, MaxSpace FROM Win32_ShadowStorage"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    used += Convert.ToInt64(o["UsedSpace"]);
                    max += Convert.ToInt64(o["MaxSpace"]);
                }
            }
            return max > 0 ? string.Format(Lang.T("還原點佔用 {0}（上限 {1}）"), Format.Size(used), Format.Size(max)) : Lang.T("還原點佔用 0");
        }

        private void DeleteAllButLatest()
        {
            var points = _list.Items.Cast<ListViewItem>().Select(i => (RestorePoint)i.Tag).OrderByDescending(p => p.Created).ToList();
            if (points.Count < 2) return;
            if (Msg.Show(this, string.Format(Lang.T("刪除 {0} 個舊的還原點，只保留最新的「{1}」（{2:yyyy/MM/dd HH:mm}）？"),
                    points.Count - 1, points[0].Description, points[0].Created), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Remove(points.Skip(1).ToList());
        }

        private void DeleteSelected()
        {
            var picked = _list.SelectedItems.Cast<ListViewItem>().Select(i => (RestorePoint)i.Tag).ToList();
            if (picked.Count == 0) return;
            bool latest = picked.Any(p => p.Created == _list.Items.Cast<ListViewItem>().Max(i => ((RestorePoint)i.Tag).Created));
            if (Msg.Show(this, string.Format(Lang.T("刪除選取的 {0} 個還原點？{1}"), picked.Count,
                    latest ? Lang.T("\r\n\r\n⚠ 包含最新的還原點，刪掉後就沒辦法退回目前這個狀態。") : string.Empty), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Remove(picked);
        }

        private void Remove(List<RestorePoint> points)
        {
            int failed = 0;
            foreach (var p in points)
            {
                if (SRRemoveRestorePoint(p.Sequence) != 0) failed++;
            }
            Reload();
            if (failed > 0) Msg.Show(this, string.Format(Lang.T("{0} 個還原點刪除失敗。"), failed), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
