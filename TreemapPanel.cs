using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 樹狀圖（treemap）：每個子項目一個矩形，面積跟大小成正比，一眼看出誰最占空間。
    /// 用 squarified 排法，矩形盡量接近正方形，比一條條細長的好讀。點矩形觸發 ItemClicked。
    /// </summary>
    public class TreemapPanel : Control
    {
        public class Item
        {
            public string Name;
            public long Size;
            public object Tag;
            public RectangleF Bounds;
        }

        private static readonly Color[] Palette =
        {
            Color.FromArgb(91, 155, 213), Color.FromArgb(237, 125, 49), Color.FromArgb(112, 173, 71),
            Color.FromArgb(255, 192, 0), Color.FromArgb(68, 114, 196), Color.FromArgb(165, 165, 165),
            Color.FromArgb(158, 72, 14), Color.FromArgb(99, 99, 199), Color.FromArgb(38, 166, 154),
            Color.FromArgb(214, 96, 141)
        };

        private readonly ToolTip _tip = new ToolTip();
        private List<Item> _items = new List<Item>();
        private Item _hover;

        public event Action<object> ItemClicked;

        public TreemapPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void SetItems(IEnumerable<Item> items)
        {
            _items = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
            _hover = null;
            Arrange();
            Invalidate();
        }

        private void Arrange()
        {
            var rect = new RectangleF(1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2));
            double total = _items.Sum(i => (double)i.Size);
            if (total <= 0 || rect.Width < 1 || rect.Height < 1) return;

            // 把大小換算成面積，再一列一列排。
            double scale = rect.Width * rect.Height / total;
            var areas = _items.Select(i => i.Size * scale).ToList();
            Squarify(areas, 0, rect);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Arrange();
        }

        private void Squarify(List<double> areas, int start, RectangleF rect)
        {
            while (start < areas.Count && rect.Width > 0.5f && rect.Height > 0.5f)
            {
                float side = Math.Min(rect.Width, rect.Height);
                int end = start + 1;
                double best = Worst(areas, start, end, side);

                // 一直加進同一列，直到最扁的那個矩形變得更扁為止。
                while (end < areas.Count)
                {
                    double next = Worst(areas, start, end + 1, side);
                    if (next > best) break;
                    best = next;
                    end++;
                }

                double sum = 0;
                for (int i = start; i < end; i++) sum += areas[i];

                if (rect.Width >= rect.Height)
                {
                    // 寬的區域：這一列是左邊一欄，由上往下排。
                    float w = (float)(sum / rect.Height);
                    float y = rect.Top;
                    for (int i = start; i < end; i++)
                    {
                        float h = (float)(areas[i] / w);
                        _items[i].Bounds = new RectangleF(rect.Left, y, w, h);
                        y += h;
                    }
                    rect = new RectangleF(rect.Left + w, rect.Top, rect.Width - w, rect.Height);
                }
                else
                {
                    // 高的區域：這一列是上面一排，由左往右排。
                    float h = (float)(sum / rect.Width);
                    float x = rect.Left;
                    for (int i = start; i < end; i++)
                    {
                        float w = (float)(areas[i] / h);
                        _items[i].Bounds = new RectangleF(x, rect.Top, w, h);
                        x += w;
                    }
                    rect = new RectangleF(rect.Left, rect.Top + h, rect.Width, rect.Height - h);
                }
                start = end;
            }

            // 太小排不進去的就不畫。
            for (int i = start; i < areas.Count; i++) _items[i].Bounds = RectangleF.Empty;
        }

        /// <summary>這一列裡最扁的矩形的長寬比（越接近 1 越好）。</summary>
        private static double Worst(List<double> areas, int start, int end, float side)
        {
            double sum = 0, max = 0, min = double.MaxValue;
            for (int i = start; i < end; i++)
            {
                sum += areas[i];
                max = Math.Max(max, areas[i]);
                min = Math.Min(min, areas[i]);
            }
            double s2 = (double)side * side, sum2 = sum * sum;
            return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, Lang.T("選左邊的資料夾，這裡會用面積顯示底下各項目的大小"), Font, ClientRectangle,
                    Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Bounds.Width < 1 || item.Bounds.Height < 1) continue;

                var r = Rectangle.Round(item.Bounds);
                Color color = Palette[i % Palette.Length];
                if (item == _hover) color = ControlPaint.Light(color, 0.4f);

                using (var brush = new SolidBrush(color)) g.FillRectangle(brush, r);
                g.DrawRectangle(Pens.White, r);

                if (r.Width > 50 && r.Height > 18)
                {
                    string label = r.Height > 34 ? item.Name + "\n" + Format.Size(item.Size) : item.Name;
                    TextRenderer.DrawText(g, label, Font, Rectangle.Inflate(r, -3, -2), Color.White,
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                }
            }
        }

        private Item HitTest(Point p)
        {
            return _items.FirstOrDefault(i => i.Bounds.Contains(p));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (hit == _hover) return;
            _hover = hit;
            _tip.SetToolTip(this, hit == null ? null : hit.Name + "\r\n" + Format.Size(hit.Size));
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
            if (hit != null && hit.Tag != null && handler != null) handler(hit.Tag);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
