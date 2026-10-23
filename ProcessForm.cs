using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 資源佔用排行：每 2 秒取樣一次各處理程序的 CPU 與記憶體，依 CPU（或記憶體）排序。
    /// CPU 百分比＝兩次取樣之間用掉的處理器時間 ÷（經過時間 × 核心數）。
    /// </summary>
    public class ProcessForm : ToolWindow
    {
        private class Row
        {
            public int Id;
            public string Name;
            public double Cpu;
            public long Memory;          // 私有工作集（跟工作管理員「記憶體」欄接近）
            public string Path;
            public string Company;
        }

        private readonly ThemedListView _list;
        private readonly Timer _timer = new Timer { Interval = 2000 };
        private readonly RadioButton _byCpu = new RadioButton { Text = Lang.T("依 CPU"), AutoSize = true, Checked = true };
        private readonly RadioButton _byMem = new RadioButton { Text = Lang.T("依記憶體"), AutoSize = true };
        private readonly Dictionary<int, TimeSpan> _lastCpu = new Dictionary<int, TimeSpan>();
        private readonly Dictionary<int, Tuple<string, string>> _info = new Dictionary<int, Tuple<string, string>>();   // 路徑、公司（查一次就記住）
        private DateTime _lastAt;
        private List<Row> _rows = new List<Row>();
        private bool _busy;

        public ProcessForm() : base(Lang.T("資源佔用"), Lang.T("哪些程式正在吃 CPU 與記憶體（每 2 秒更新）"), "", new Size(980, 620))
        {
            _list = NewList(Lang.T("名稱:200"), "PID:70:r", "CPU:80:r", Lang.T("記憶體:110:r"), Lang.T("發行者:180"), Lang.T("路徑:330"));
            var end = Light(Lang.T("結束處理程序"), 130);
            var locate = Light(Lang.T("開啟檔案位置"), 130);

            Content.Controls.Add(Card(_list));
            Content.Controls.Add(Toolbar(_byCpu, _byMem, end, locate));

            _byCpu.CheckedChanged += delegate { Fill(); };
            _timer.Tick += delegate { Sample(); };
            end.Click += delegate { EndProcess(); };
            locate.Click += delegate
            {
                var r = Selected();
                if (r != null && r.Path != null && File.Exists(r.Path)) Process.Start("explorer.exe", "/select,\"" + r.Path + "\"");
            };

            Load += delegate { Sample(); _timer.Start(); };
            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); };
        }

        private Row Selected()
        {
            return _list.SelectedItems.Count == 0 ? null : (Row)_list.SelectedItems[0].Tag;
        }

        private async void Sample()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var now = DateTime.UtcNow;
                double elapsed = _lastAt == default(DateTime) ? 0 : (now - _lastAt).TotalMilliseconds;
                _lastAt = now;

                _rows = await Task.Run(() => Collect(elapsed));
                Fill();
            }
            finally
            {
                _busy = false;
            }
        }

        private List<Row> Collect(double elapsedMs)
        {
            var rows = new List<Row>();
            var alive = new HashSet<int>();
            int cores = Environment.ProcessorCount;

            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == 0) continue;   // System Idle
                    alive.Add(p.Id);
                    var row = new Row { Id = p.Id, Name = p.ProcessName };
                    try { row.Memory = p.PrivateMemorySize64; } catch (Exception) { }

                    try
                    {
                        var cpu = p.TotalProcessorTime;
                        TimeSpan before;
                        if (elapsedMs > 0 && _lastCpu.TryGetValue(p.Id, out before))
                        {
                            row.Cpu = Math.Max(0, (cpu - before).TotalMilliseconds / (elapsedMs * cores) * 100);
                        }
                        _lastCpu[p.Id] = cpu;
                    }
                    catch (Exception) { }   // 系統處理程序沒權限讀 CPU 時間

                    Tuple<string, string> info;
                    if (!_info.TryGetValue(p.Id, out info))
                    {
                        string path = null, company = null;
                        try
                        {
                            path = p.MainModule.FileName;
                            company = FileVersionInfo.GetVersionInfo(path).CompanyName;
                        }
                        catch (Exception) { }
                        _info[p.Id] = info = Tuple.Create(path, company);
                    }
                    row.Path = info.Item1;
                    row.Company = info.Item2;
                    rows.Add(row);
                }
            }

            // 已經結束的處理程序不要一直留在快取裡。
            foreach (int id in _lastCpu.Keys.Where(k => !alive.Contains(k)).ToList()) { _lastCpu.Remove(id); _info.Remove(id); }
            return rows;
        }

        private void Fill()
        {
            var keep = Selected();
            var rows = _byCpu.Checked
                ? _rows.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.Memory)
                : _rows.OrderByDescending(r => r.Memory);

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in rows.Take(150))
            {
                var item = new ListViewItem(r.Name) { Tag = r };
                item.SubItems.Add(r.Id.ToString());
                item.SubItems.Add(r.Cpu >= 0.05 ? r.Cpu.ToString("0.0") + "%" : "0%");
                item.SubItems.Add(Format.Size(r.Memory));
                item.SubItems.Add(r.Company ?? string.Empty);
                item.SubItems.Add(r.Path ?? string.Empty);
                if (r.Cpu >= 20 || r.Memory > 2L * 1024 * 1024 * 1024) item.ForeColor = Theme.Orange;
                _list.Items.Add(item);
                if (keep != null && keep.Id == r.Id) item.Selected = true;
            }
            _list.EndUpdate();

            Status.Text = string.Format(Lang.T("{0} 個處理程序　│　CPU 合計 {1:0}%　│　記憶體合計 {2}"),
                _rows.Count, Math.Min(100, _rows.Sum(r => r.Cpu)), Format.Size(_rows.Sum(r => r.Memory)));
        }

        private void EndProcess()
        {
            var r = Selected();
            if (r == null) return;
            if (Msg.Show(this, string.Format(Lang.T("結束「{0}」（PID {1}）？\r\n\r\n沒存檔的資料會遺失；結束系統處理程序可能讓 Windows 不穩。"), r.Name, r.Id),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                using (var p = Process.GetProcessById(r.Id)) p.Kill();
                Status.Text = Lang.T("已結束 ") + r.Name;
            }
            catch (Exception ex)
            {
                Msg.Show(this, Lang.T("無法結束：") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Sample();
        }
    }
}
