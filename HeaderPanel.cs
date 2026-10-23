using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 上方橫幅：翡翠綠漸層＋半透明裝飾圓，左邊圓形圖示、跳動計數的大字容量與說明，
    /// 上排是「今天／總計」膠囊與「檢視歷史記錄」，右邊是系統磁碟使用量卡片與主按鈕。
    /// 忙碌時底部有一道流光。
    /// </summary>
    public class HeaderPanel : Panel
    {
        private static readonly Color G1 = Color.FromArgb(16, 185, 129);
        private static readonly Color G2 = Color.FromArgb(13, 148, 136);
        private static readonly Color G3 = Color.FromArgb(14, 94, 112);

        private readonly Font _hintFont = new Font("Microsoft JhengHei UI", 10f);
        private readonly Timer _timer = new Timer { Interval = 16 };

        private long _today, _total;
        private string _bigText = "0 MB";
        private string _hint = string.Empty;
        private double _shownBytes, _targetBytes;   // 跳動計數用
        private bool _animating;
        private bool _busy;
        private float _shimmer;                      // 0～1，流光位置
        private float _spin;                         // 忙碌時圖示外圈轉動
        private DateTime _diskAt = DateTime.MinValue;
        private long _diskFree, _diskTotal;
        private string _diskName = "C:";
        private Rectangle _historyRect;
        private bool _historyHover;

        public RoundButton Action { get; private set; }
        public event EventHandler HistoryClicked;

        public HeaderPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = 148;

            Action = new RoundButton
            {
                Text = Lang.T("掃瞄"),
                Size = new Size(176, 52),
                Font = new Font("Microsoft JhengHei UI", 12.5f, FontStyle.Bold),
                Accent = Color.FromArgb(34, 197, 94),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            Controls.Add(Action);

            _timer.Tick += delegate { Tick(); };
        }

        public bool Busy
        {
            get { return _busy; }
            set
            {
                _busy = value;
                if (value) _timer.Start();
                Invalidate();
            }
        }

        public void SetStats(long today, long total)
        {
            _today = today;
            _total = total;
            _diskAt = DateTime.MinValue;   // 清理後空間變了，下次重畫重新讀
            Invalidate();
        }

        /// <summary>bytes 有值時大字會從目前的數字跳到這個容量；沒值就直接顯示 text（例如「掃描中…」）。</summary>
        public void SetMain(long? bytes, string text, string hint)
        {
            _hint = hint ?? string.Empty;
            if (bytes.HasValue)
            {
                _targetBytes = bytes.Value;
                if (_bigText == null || !_animating && Math.Abs(_shownBytes - _targetBytes) > 0.5)
                {
                    _animating = true;
                    _timer.Start();
                }
                _bigText = null;
            }
            else
            {
                _bigText = text;
                _shownBytes = 0;
                _animating = false;
            }
            Invalidate();
        }

        private void Tick()
        {
            bool keep = false;
            if (_animating)
            {
                // 緩出：每格走剩下距離的 15%，最後幾個位元組直接到位。
                _shownBytes += (_targetBytes - _shownBytes) * 0.15;
                if (Math.Abs(_targetBytes - _shownBytes) < Math.Max(1, _targetBytes * 0.002)) { _shownBytes = _targetBytes; _animating = false; }
                else keep = true;
            }
            if (_busy)
            {
                _shimmer = (_shimmer + 0.012f) % 1.3f;
                _spin = (_spin + 6) % 360;
                keep = true;
            }
            if (!keep) _timer.Stop();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Action != null) Action.Location = new Point(Width - Action.Width - 28, (Height - Action.Height) / 2 + 8);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (var brush = new LinearGradientBrush(rect, G1, G3, 20f))
            {
                brush.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { G1, G2, G3 },
                    Positions = new[] { 0f, 0.55f, 1f }
                };
                g.FillRectangle(brush, rect);
            }

            // 幾個大大的半透明圓，讓橫幅不那麼平。
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b1 = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
            using (var b2 = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
            {
                g.FillEllipse(b1, Width - 360, -120, 300, 300);
                g.FillEllipse(b2, Width - 520, 40, 260, 260);
                g.FillEllipse(b2, -80, 70, 200, 200);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawLogo(g, new Rectangle(24, 40, 76, 76));

            // ── 上排膠囊：今天／總計／檢視歷史記錄 ──
            int x = 118;
            x = Chip(g, x, 14, Lang.T("今天清理  ") + Format.Size(_today)) + 8;
            x = Chip(g, x, 14, Lang.T("總計  ") + Format.Size(_total)) + 12;
            var link = Lang.T("檢視歷史記錄 ›");
            var size = TextRenderer.MeasureText(link, Theme.SmallBold);
            _historyRect = new Rectangle(x, 14, size.Width, 24);
            TextRenderer.DrawText(g, link, Theme.SmallBold, _historyRect,
                _historyHover ? Color.White : Color.FromArgb(220, 255, 255, 255), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (_historyHover)
            {
                using (var pen = new Pen(Color.White)) g.DrawLine(pen, _historyRect.Left, _historyRect.Bottom - 3, _historyRect.Right - 6, _historyRect.Bottom - 3);
            }

            // ── 大字容量與說明 ──
            string big = _bigText ?? Format.Size((long)_shownBytes);
            TextRenderer.DrawText(g, big, Theme.Number, new Point(112, 40), Color.White, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _hint, _hintFont, new Rectangle(118, 98, Math.Max(100, Width - 118 - 470), 40),
                Color.FromArgb(225, 255, 255, 255), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

            DrawDiskCard(g);

            // ── 忙碌：底部流光 ──
            if (_busy)
            {
                float w = Width * 0.35f;
                float left = (_shimmer - 0.3f) * Width;
                var bar = new RectangleF(left, Height - 4, w, 4);
                using (var brush = new LinearGradientBrush(new RectangleF(left - 1, Height - 4, w + 2, 4),
                    Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Horizontal))
                {
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(230, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    g.FillRectangle(brush, bar);
                }
            }
        }

        /// <summary>半透明白底的膠囊，回傳右緣 x。</summary>
        private static int Chip(Graphics g, int x, int y, string text)
        {
            var size = TextRenderer.MeasureText(text, Theme.SmallBold);
            var r = new Rectangle(x, y, size.Width + 16, 24);
            Theme.FillRound(g, Color.FromArgb(45, 255, 255, 255), r, 12);
            TextRenderer.DrawText(g, text, Theme.SmallBold, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return r.Right;
        }

        /// <summary>圓形玻璃底＋掃帚；忙碌時外圈有一段弧在轉。</summary>
        private void DrawLogo(Graphics g, Rectangle r)
        {
            using (var brush = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillEllipse(brush, r);
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 2f)) g.DrawEllipse(pen, r);

            if (_busy)
            {
                using (var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(pen, Rectangle.Inflate(r, 4, 4), _spin, 80);
                }
            }

            // 掃帚：一根斜柄＋扇形刷頭。
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, s = r.Width / 76f;
            using (var pen = new Pen(Color.White, 4.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx + 16 * s, cy - 22 * s, cx - 2 * s, cy + 4 * s);
            }
            var head = new[]
            {
                new PointF(cx - 8 * s, cy + 0 * s), new PointF(cx + 6 * s, cy + 8 * s),
                new PointF(cx - 2 * s, cy + 22 * s), new PointF(cx - 20 * s, cy + 14 * s)
            };
            using (var brush = new SolidBrush(Color.White)) g.FillPolygon(brush, head);
            using (var pen = new Pen(G2, 1.6f * s))
            {
                g.DrawLine(pen, cx - 6 * s, cy + 8 * s, cx - 12 * s, cy + 17 * s);
                g.DrawLine(pen, cx - 1 * s, cy + 10 * s, cx - 6 * s, cy + 19 * s);
            }
            // 幾顆閃亮的小星星。
            using (var brush = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
            {
                Sparkle(g, brush, cx + 14 * s, cy + 10 * s, 5 * s);
                Sparkle(g, brush, cx + 21 * s, cy - 2 * s, 3 * s);
            }
        }

        private static void Sparkle(Graphics g, Brush brush, float x, float y, float r)
        {
            g.FillPolygon(brush, new[]
            {
                new PointF(x, y - r), new PointF(x + r * 0.28f, y - r * 0.28f), new PointF(x + r, y),
                new PointF(x + r * 0.28f, y + r * 0.28f), new PointF(x, y + r), new PointF(x - r * 0.28f, y + r * 0.28f),
                new PointF(x - r, y), new PointF(x - r * 0.28f, y - r * 0.28f)
            });
        }

        /// <summary>系統磁碟卡片：可用空間＋使用量長條，剩不到 10% 變紅。</summary>
        private void DrawDiskCard(Graphics g)
        {
            // 動畫時一秒重畫幾十次，磁碟資訊不用每次都問系統。
            if (DateTime.Now - _diskAt > TimeSpan.FromSeconds(5))
            {
                _diskAt = DateTime.Now;
                try
                {
                    var d = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                    _diskFree = d.AvailableFreeSpace;
                    _diskTotal = d.TotalSize;
                    _diskName = d.Name.TrimEnd('\\');
                }
                catch (Exception) { _diskTotal = 0; }
            }
            long free = _diskFree, totalSize = _diskTotal;
            string name = _diskName;
            if (totalSize <= 0) return;

            var card = new Rectangle(Width - 28 - Action.Width - 24 - 220, 34, 220, 84);
            if (card.Left < 420) return;   // 視窗太窄就不畫

            Theme.FillRound(g, Color.FromArgb(38, 255, 255, 255), card, 12);
            Theme.DrawRound(g, Color.FromArgb(60, 255, 255, 255), card, 12);

            double used = 1 - (double)free / totalSize;
            bool low = free < totalSize * Background.LowSpaceRatio;

            TextRenderer.DrawText(g, Lang.T("系統磁碟 ") + name, Theme.SmallBold, new Point(card.Left + 14, card.Top + 10), Color.FromArgb(230, 255, 255, 255));
            TextRenderer.DrawText(g, Format.Size(free) + Lang.T(" 可用"), Theme.NumberSmall, new Point(card.Left + 12, card.Top + 28), Color.White);

            var track = new RectangleF(card.Left + 14, card.Bottom - 20, card.Width - 28, 8);
            Theme.FillRound(g, Color.FromArgb(70, 255, 255, 255), track, 4);
            var fill = new RectangleF(track.Left, track.Top, (float)(track.Width * used), track.Height);
            Theme.FillRound(g, low ? Color.FromArgb(254, 202, 202) : Color.White, fill, 4);
            if (low) Theme.FillRound(g, Theme.Red, new RectangleF(fill.Right - 8, fill.Top, 8, fill.Height), 4);

            string pct = string.Format(Lang.T("{0:0}% 已用"), used * 100);
            var size = TextRenderer.MeasureText(pct, Theme.Small);
            TextRenderer.DrawText(g, pct, Theme.Small, new Point(card.Right - 14 - size.Width, card.Top + 10),
                low ? Color.FromArgb(254, 226, 226) : Color.FromArgb(220, 255, 255, 255));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = _historyRect.Contains(e.Location);
            if (hover == _historyHover) return;
            _historyHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate(_historyRect);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_historyHover) return;
            _historyHover = false;
            Cursor = Cursors.Default;
            Invalidate(_historyRect);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var handler = HistoryClicked;
            if (_historyRect.Contains(e.Location) && handler != null) handler(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
