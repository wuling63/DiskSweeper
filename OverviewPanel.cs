using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 掃描結果總覽：左邊環圈圖（各分類佔多少），中間圖例，右邊前五大項目的長條。
    /// 環圈出現時有一段展開動畫；點圖例或長條觸發 ItemClicked（Tag 是分類名稱或 CleanItem）。
    /// </summary>
    public class OverviewPanel : Control
    {
        public class Slice
        {
            public string Label;
            public long Bytes;
            public Color Color;
            public object Tag;
        }

        private readonly Timer _anim = new Timer { Interval = 15 };
        private List<Slice> _slices = new List<Slice>();
        private List<Slice> _top = new List<Slice>();
        private float _progress = 1f;   // 環圈展開 0～1
        private readonly List<KeyValuePair<RectangleF, object>> _hits = new List<KeyValuePair<RectangleF, object>>();
        private object _hover;

        public event Action<object> ItemClicked;

        public OverviewPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = 168;
            _anim.Tick += delegate
            {
                _progress += (1f - _progress) * 0.12f + 0.01f;
                if (_progress >= 1f) { _progress = 1f; _anim.Stop(); }
                Invalidate();
            };
        }

        public void SetData(List<Slice> slices, List<Slice> top)
        {
            bool fresh = _slices.Count == 0 && slices.Count > 0;
            _slices = slices.Where(s => s.Bytes > 0).OrderByDescending(s => s.Bytes).ToList();
            _top = top.Where(s => s.Bytes > 0).OrderByDescending(s => s.Bytes).Take(5).ToList();
            if (fresh) { _progress = 0f; _anim.Start(); }
            Invalidate();
        }

        public void Clear()
        {
            _slices.Clear();
            _top.Clear();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            _hits.Clear();

            long total = _slices.Sum(s => s.Bytes);

            // ── 環圈 ──
            var ring = new RectangleF(18, 16, Height - 32, Height - 32);
            using (var pen = new Pen(Theme.HeaderRow, 18f)) g.DrawEllipse(pen, RectangleF.Inflate(ring, -9, -9));
            if (total > 0)
            {
                float start = -90f, sweepAll = 360f * _progress;
                foreach (var s in _slices)
                {
                    float sweep = (float)(sweepAll * s.Bytes / total);
                    if (sweep < 0.5f) continue;
                    bool hot = Equals(_hover, s.Tag);
                    using (var pen = new Pen(s.Color, hot ? 22f : 18f)) g.DrawArc(pen, RectangleF.Inflate(ring, -9, -9), start, Math.Max(0.5f, sweep - 1.2f));
                    start += sweep;
                }
            }
            string center = total > 0 ? Format.Size(total) : "—";
            TextRenderer.DrawText(g, center, Theme.Title, Rectangle.Round(new RectangleF(ring.X, ring.Y + ring.Height / 2 - 18, ring.Width, 22)),
                Theme.Text, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Lang.T("可清理"), Theme.Small, Rectangle.Round(new RectangleF(ring.X, ring.Y + ring.Height / 2 + 4, ring.Width, 18)),
                Theme.TextMuted, TextFormatFlags.HorizontalCenter);

            // ── 圖例 ──
            float x = ring.Right + 22, y = 20;
            TextRenderer.DrawText(g, Lang.T("分類"), Theme.SmallBold, new Point((int)x, (int)y - 4), Theme.TextMuted);
            y += 18;
            foreach (var s in _slices.Take(6))
            {
                var row = new RectangleF(x - 4, y - 2, 190, 20);
                if (Equals(_hover, s.Tag)) Theme.FillRound(g, Theme.AccentSoft, row, 5);
                using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, x, y + 4, 10, 10);
                TextRenderer.DrawText(g, s.Label, Theme.Small, new Point((int)x + 16, (int)y + 1), Theme.Text);
                string v = string.Format("{0}  {1:0}%", Format.Size(s.Bytes), total > 0 ? s.Bytes * 100.0 / total : 0);
                var size = TextRenderer.MeasureText(v, Theme.Small);
                TextRenderer.DrawText(g, v, Theme.Small, new Point((int)(x + 182 - size.Width), (int)y + 1), Theme.TextMuted);
                _hits.Add(new KeyValuePair<RectangleF, object>(row, s.Tag));
                y += 21;
            }

            // ── 前五大項目 ──
            float bx = x + 214, bw = Width - bx - 20;
            if (bw < 160 || _top.Count == 0) return;
            TextRenderer.DrawText(g, Lang.T("最大的項目"), Theme.SmallBold, new Point((int)bx, 16), Theme.TextMuted);
            long max = _top[0].Bytes;
            float by = 38;
            foreach (var s in _top)
            {
                var row = new RectangleF(bx - 4, by - 3, bw + 8, 25);
                if (Equals(_hover, s.Tag)) Theme.FillRound(g, Theme.AccentSoft, row, 5);

                // 由右往左排：數值固定 76 px，名稱佔四成，中間剩下的給長條，三者不會疊在一起。
                const float valueW = 76;
                float labelW = bw * 0.40f;
                float trackX = bx + labelW + 8, trackW = Math.Max(20, bw - labelW - valueW - 16);
                TextRenderer.DrawText(g, s.Label, Theme.Small, new Rectangle((int)bx, (int)by - 1, (int)labelW, 18), Theme.Text,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                var track = new RectangleF(trackX, by + 4, trackW, 8);
                Theme.FillRound(g, Theme.HeaderRow, track, 4);
                var fill = new RectangleF(trackX, by + 4, Math.Max(8, trackW * s.Bytes / max * _progress), 8);
                using (var brush = new LinearGradientBrush(new RectangleF(fill.X - 1, fill.Y, fill.Width + 2, fill.Height),
                    Theme.Blend(s.Color, Color.White, 0.25f), s.Color, LinearGradientMode.Horizontal))
                using (var path = Theme.RoundRect(fill, 4))
                {
                    g.FillPath(brush, path);
                }
                string v = Format.Size(s.Bytes);
                var size = TextRenderer.MeasureText(v, Theme.Small);
                TextRenderer.DrawText(g, v, Theme.Small, new Point((int)(bx + bw - size.Width + 4), (int)by - 1), Theme.TextMuted);
                _hits.Add(new KeyValuePair<RectangleF, object>(row, s.Tag));
                by += 25;
            }
        }

        private object HitTest(Point p)
        {
            foreach (var h in _hits) if (h.Key.Contains(p)) return h.Value;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (Equals(hit, _hover)) return;
            _hover = hit;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = null;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var hit = HitTest(e.Location);
            var handler = ItemClicked;
            if (hit != null && handler != null) handler(hit);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}
