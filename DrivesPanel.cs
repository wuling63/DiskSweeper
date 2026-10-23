using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 主頁的磁碟卡片列：每個磁碟一張卡片（代號、名稱、類型、可用／總容量、使用量長條），
    /// 用量 90% 以上紅色、80% 以上琥珀色。卡片多時可以左右捲；點卡片觸發 DriveClicked（傳回磁碟根目錄）。
    /// </summary>
    public class DrivesPanel : Control
    {
        private class Card
        {
            public string Root;
            public string Name;       // C:
            public string Label;      // 磁碟名稱
            public string Kind;       // 本機磁碟／抽取式／網路
            public long Free, Total;
            public string Format;
            public Rectangle Bounds;
        }

        private const int CardWidth = 228, CardHeight = 74, Gap = 12;

        private readonly Timer _refresh = new Timer { Interval = 30000 };
        private readonly ToolTip _tip = new ToolTip();
        private List<Card> _cards = new List<Card>();
        private int _scroll;          // 往左捲了多少像素
        private Card _hover;
        private Rectangle _left, _right;

        public event Action<string> DriveClicked;

        public DrivesPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = CardHeight + 20;
            _refresh.Tick += delegate { RefreshDrives(); };
            _refresh.Start();
            RefreshDrives();
        }

        /// <summary>重新讀取所有磁碟（清理後、插拔隨身碟後都可以叫）。</summary>
        public void RefreshDrives()
        {
            var cards = new List<Card>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable && d.DriveType != DriveType.Network) continue;
                    cards.Add(new Card
                    {
                        Root = d.RootDirectory.FullName,
                        Name = d.Name.TrimEnd('\\'),
                        Label = string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel,
                        Kind = d.DriveType == DriveType.Fixed ? Lang.T("本機磁碟") : d.DriveType == DriveType.Removable ? Lang.T("抽取式磁碟") : Lang.T("網路磁碟"),
                        Free = d.AvailableFreeSpace,
                        Total = d.TotalSize,
                        Format = d.DriveFormat
                    });
                }
                catch (Exception) { }   // 光碟機沒片、網路磁碟斷線之類
            }
            _cards = cards;
            _hover = null;
            ClampScroll();
            Invalidate();
        }

        private int ContentWidth
        {
            get { return _cards.Count * (CardWidth + Gap) - Gap + 28; }
        }

        private void ClampScroll()
        {
            _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, ContentWidth - Width)));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int x = 14 - _scroll;
            foreach (var c in _cards)
            {
                c.Bounds = new Rectangle(x, 10, CardWidth, CardHeight);
                DrawCard(g, c);
                x += CardWidth + Gap;
            }

            // 放不下時左右兩端畫捲動箭頭。
            _left = _right = Rectangle.Empty;
            if (_scroll > 0) _left = DrawArrow(g, new Rectangle(0, 10, 28, CardHeight), "‹");
            if (_scroll < ContentWidth - Width) _right = DrawArrow(g, new Rectangle(Width - 28, 10, 28, CardHeight), "›");
        }

        private Rectangle DrawArrow(Graphics g, Rectangle r, string glyph)
        {
            using (var b = new SolidBrush(Color.FromArgb(230, Theme.Background))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, glyph, Theme.Title, r, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return r;
        }

        private void DrawCard(Graphics g, Card c)
        {
            var r = c.Bounds;
            bool hot = c == _hover;
            Theme.FillRound(g, hot ? Theme.Blend(Theme.Surface, Theme.AccentSoft, 0.7f) : Theme.Surface, r, 10);
            Theme.DrawRound(g, hot ? Theme.Accent : Theme.Border, r, 10, hot ? 1.5f : 1f);

            double used = c.Total > 0 ? 1 - (double)c.Free / c.Total : 0;
            Color barColor = used >= 0.9 ? Theme.Red : used >= 0.8 ? Theme.Amber : Theme.Accent;

            // 左邊的磁碟圖示：圓角方塊＋一條指示燈。
            var icon = new RectangleF(r.Left + 12, r.Top + 14, 30, 22);
            Theme.FillRound(g, Theme.Tint(barColor), icon, 5);
            Theme.DrawRound(g, barColor, icon, 5, 1.4f);
            using (var b = new SolidBrush(barColor)) g.FillEllipse(b, icon.Right - 9, icon.Bottom - 8, 4, 4);
            using (var pen = new Pen(barColor, 1.4f)) g.DrawLine(pen, icon.Left + 5, icon.Bottom - 6, icon.Left + 14, icon.Bottom - 6);

            // 代號＋名稱、類型
            string title = c.Label != null ? c.Name + "  " + c.Label : c.Name;
            TextRenderer.DrawText(g, title, Theme.UiBold, new Rectangle(r.Left + 50, r.Top + 9, r.Width - 104, 20), Theme.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, c.Kind, Theme.Small, new Rectangle(r.Left + 50, r.Top + 28, 120, 16), Theme.TextMuted, TextFormatFlags.SingleLine);

            string pct = string.Format("{0:0}%", used * 100);
            var size = TextRenderer.MeasureText(pct, Theme.UiBold);
            TextRenderer.DrawText(g, pct, Theme.UiBold, new Point(r.Right - 12 - size.Width, r.Top + 9), used >= 0.8 ? barColor : Theme.TextMuted);

            // 使用量長條與可用空間
            var track = new RectangleF(r.Left + 12, r.Top + 48, r.Width - 24, 7);
            Theme.FillRound(g, Theme.HeaderRow, track, 3.5f);
            var fill = new RectangleF(track.Left, track.Top, Math.Max(7, (float)(track.Width * used)), track.Height);
            using (var path = Theme.RoundRect(fill, 3.5f))
            using (var brush = new LinearGradientBrush(new RectangleF(fill.X - 1, fill.Y, fill.Width + 2, fill.Height),
                Theme.Blend(barColor, Color.White, 0.25f), barColor, LinearGradientMode.Horizontal))
            {
                g.FillPath(brush, path);
            }
            string free = string.Format(Lang.T("{0} 可用／共 {1}"), DiskCleaner.Format.Size(c.Free), DiskCleaner.Format.Size(c.Total));
            TextRenderer.DrawText(g, free, Theme.Small, new Rectangle(r.Left + 12, r.Top + 56, r.Width - 24, 16), Theme.TextMuted,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        private Card HitTest(Point p)
        {
            if (_left.Contains(p) || _right.Contains(p)) return null;
            return _cards.FirstOrDefault(c => c.Bounds.Contains(p));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            Cursor = hit != null || _left.Contains(e.Location) || _right.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (hit == _hover) return;
            _hover = hit;
            _tip.SetToolTip(this, hit == null ? null : string.Format(Lang.T("{0}（{1}）\r\n已用 {2}／共 {3}\r\n點一下分析這個磁碟的大檔案"),
                hit.Name, hit.Format, DiskCleaner.Format.Size(hit.Total - hit.Free), DiskCleaner.Format.Size(hit.Total)));
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
            if (_left.Contains(e.Location)) { _scroll -= CardWidth + Gap; ClampScroll(); Invalidate(); return; }
            if (_right.Contains(e.Location)) { _scroll += CardWidth + Gap; ClampScroll(); Invalidate(); return; }
            var hit = HitTest(e.Location);
            var handler = DriveClicked;
            if (hit != null && handler != null) handler(hit.Root);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll -= e.Delta / 2;
            ClampScroll();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refresh.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
