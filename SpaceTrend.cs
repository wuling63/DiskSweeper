using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace DiskCleaner
{
    public class SpacePoint
    {
        public DateTime Date { get; set; }     // 只取日期
        public string Drive { get; set; }      // C:
        public long Free { get; set; }
        public long Total { get; set; }
    }

    /// <summary>
    /// 每天記一筆各固定磁碟的可用空間（開程式、排程清理、空間檢查時都會記），保留一年。
    /// 同一天記多次只留最後一次。
    /// </summary>
    public static class SpaceLog
    {
        private static string FilePath { get { return Path.Combine(Settings.Folder, "space.json"); } }

        public static List<SpacePoint> Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<List<SpacePoint>>(File.ReadAllText(FilePath)) ?? new List<SpacePoint>();
            }
            catch (Exception) { }
            return new List<SpacePoint>();
        }

        public static void Record()
        {
            try
            {
                var points = Load();
                var today = DateTime.Today;
                foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    string name = d.Name.TrimEnd('\\');
                    points.RemoveAll(p => p.Date == today && p.Drive == name);
                    points.Add(new SpacePoint { Date = today, Drive = name, Free = d.AvailableFreeSpace, Total = d.TotalSize });
                }
                points = points.Where(p => p.Date >= today.AddDays(-365)).OrderBy(p => p.Date).ToList();
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(points, Formatting.Indented));
            }
            catch (Exception) { }
        }
    }

    /// <summary>可用空間折線圖：漸層填色、清理那天標綠點、滑鼠移上去顯示數值。</summary>
    public class TrendChart : Control
    {
        private List<SpacePoint> _points = new List<SpacePoint>();
        private List<HistoryEntry> _cleans = new List<HistoryEntry>();
        private int _hover = -1;
        private RectangleF _plot;

        public TrendChart()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetData(List<SpacePoint> points, List<HistoryEntry> cleans)
        {
            _points = points.OrderBy(p => p.Date).ToList();
            _cleans = cleans;
            _hover = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_points.Count < 2)
            {
                TextRenderer.DrawText(g, Lang.T("資料還不夠畫圖：程式每天會記一筆可用空間，過幾天再來看。\r\n（開這個程式、排程清理與每日空間檢查時都會記錄）"),
                    Theme.Ui, ClientRectangle, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            _plot = new RectangleF(70, 20, Width - 100, Height - 60);
            double max = _points.Max(p => p.Free), min = _points.Min(p => p.Free);
            double pad = Math.Max((max - min) * 0.15, 1L << 30);
            max += pad;
            min = Math.Max(0, min - pad);

            // 格線與 Y 軸
            using (var grid = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = _plot.Top + _plot.Height * i / 4f;
                    g.DrawLine(grid, _plot.Left, y, _plot.Right, y);
                    double v = max - (max - min) * i / 4;
                    TextRenderer.DrawText(g, Format.Size((long)v), Theme.Small, new Rectangle(0, (int)y - 9, 64, 18), Theme.TextMuted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }

            DateTime first = _points[0].Date, last = _points[_points.Count - 1].Date;
            double span = Math.Max(1, (last - first).TotalDays);
            Func<SpacePoint, PointF> at = p => new PointF(
                _plot.Left + (float)((p.Date - first).TotalDays / span * _plot.Width),
                _plot.Bottom - (float)((p.Free - min) / (max - min) * _plot.Height));
            var pts = _points.Select(at).ToArray();

            // 漸層填色＋折線
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, _plot.Bottom));
                path.AddLine(new PointF(pts[pts.Length - 1].X, _plot.Bottom), new PointF(pts[0].X, _plot.Bottom));
                path.CloseFigure();
                using (var brush = new LinearGradientBrush(_plot, Color.FromArgb(90, Theme.Accent), Color.FromArgb(5, Theme.Accent), 90f))
                {
                    g.FillPath(brush, path);
                }
            }
            using (var pen = new Pen(Theme.Accent, 2.5f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);

            // X 軸日期（最多 6 個）
            int step = Math.Max(1, _points.Count / 6);
            for (int i = 0; i < _points.Count; i += step)
            {
                TextRenderer.DrawText(g, _points[i].Date.ToString("MM/dd"), Theme.Small,
                    new Rectangle((int)pts[i].X - 30, (int)_plot.Bottom + 6, 60, 18), Theme.TextMuted, TextFormatFlags.HorizontalCenter);
            }

            // 清理那天標一個點
            var cleanDays = new HashSet<DateTime>(_cleans.Select(c => c.Time.Date));
            for (int i = 0; i < _points.Count; i++)
            {
                bool cleaned = cleanDays.Contains(_points[i].Date);
                float r = i == _hover ? 6 : cleaned ? 5 : 3;
                using (var b = new SolidBrush(cleaned ? Theme.Accent : Theme.Surface)) g.FillEllipse(b, pts[i].X - r, pts[i].Y - r, r * 2, r * 2);
                using (var pen = new Pen(Theme.Accent, 2f)) g.DrawEllipse(pen, pts[i].X - r, pts[i].Y - r, r * 2, r * 2);
            }

            if (_hover >= 0)
            {
                var p = _points[_hover];
                long cleanedBytes = _cleans.Where(c => c.Time.Date == p.Date).Sum(c => c.Bytes);
                string tip = string.Format(Lang.T("{0:yyyy/MM/dd}\r\n可用 {1}／{2}{3}"), p.Date, Format.Size(p.Free), Format.Size(p.Total),
                    cleanedBytes > 0 ? Lang.T("\r\n當天清理 ") + Format.Size(cleanedBytes) : string.Empty);
                var size = TextRenderer.MeasureText(tip, Theme.Small);
                var box = new RectangleF(Math.Min(pts[_hover].X + 10, Width - size.Width - 24), Math.Max(4, pts[_hover].Y - size.Height - 18), size.Width + 16, size.Height + 10);
                Theme.FillRound(g, Theme.Text, box, 6);
                TextRenderer.DrawText(g, tip, Theme.Small, Rectangle.Round(new RectangleF(box.X + 8, box.Y + 5, size.Width, size.Height)), Theme.Surface);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_points.Count < 2) return;
            DateTime first = _points[0].Date, last = _points[_points.Count - 1].Date;
            double span = Math.Max(1, (last - first).TotalDays);
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _points.Count; i++)
            {
                float x = _plot.Left + (float)((_points[i].Date - first).TotalDays / span * _plot.Width);
                float d = Math.Abs(x - e.X);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (bestDist > 30) best = -1;
            if (best != _hover) { _hover = best; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }
    }

    public class TrendForm : ToolWindow
    {
        private readonly ComboBox _drive = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly ComboBox _range = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly TrendChart _chart = new TrendChart();
        private readonly List<SpacePoint> _all;
        private readonly List<HistoryEntry> _history;

        public TrendForm(List<HistoryEntry> history) : base(Lang.T("磁碟空間趨勢"), Lang.T("每天記一筆可用空間；綠色實心點是有清理的日子"), "", new Size(900, 560))
        {
            SpaceLog.Record();
            _all = SpaceLog.Load();
            _history = history;

            foreach (string d in _all.Select(p => p.Drive).Distinct().OrderBy(d => d)) _drive.Items.Add(d);
            _range.Items.AddRange(new object[] { Lang.T("最近 30 天"), Lang.T("最近 90 天"), Lang.T("最近一年") });
            _range.SelectedIndex = 0;
            if (_drive.Items.Count > 0) _drive.SelectedIndex = 0;

            Content.Controls.Add(Card(_chart));
            Content.Controls.Add(Toolbar(new Label { Text = Lang.T("磁碟"), AutoSize = true }, _drive, new Label { Text = Lang.T("範圍"), AutoSize = true }, _range));

            _drive.SelectedIndexChanged += delegate { Fill(); };
            _range.SelectedIndexChanged += delegate { Fill(); };
            Load += delegate { Fill(); };
        }

        private void Fill()
        {
            string drive = _drive.SelectedItem as string;
            int days = _range.SelectedIndex == 2 ? 365 : _range.SelectedIndex == 1 ? 90 : 30;
            var points = _all.Where(p => p.Drive == drive && p.Date >= DateTime.Today.AddDays(-days)).ToList();
            _chart.SetData(points, _history);

            if (points.Count >= 2)
            {
                long change = points[points.Count - 1].Free - points[0].Free;
                Status.Text = string.Format(Lang.T("{0} 天內可用空間{1} {2}（{3:yyyy/MM/dd} 起，共 {4} 筆紀錄）"),
                    (points[points.Count - 1].Date - points[0].Date).Days, change >= 0 ? Lang.T("增加") : Lang.T("減少"), Format.Size(Math.Abs(change)), points[0].Date, points.Count);
            }
            else Status.Text = Lang.T("紀錄從今天開始累積");
        }
    }
}
