using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 圓角膠囊按鈕：漸層、柔和陰影、滑過與按下的漸變效果。背景透明，放在漸層橫幅上邊緣也是平滑的。
    /// 實作 IButtonControl，可以當 AcceptButton，也支援 Enter／空白鍵。
    /// </summary>
    public class RoundButton : Control, IButtonControl
    {
        public enum Kind { Primary, Light }

        private readonly Timer _anim = new Timer { Interval = 15 };
        private float _hover;          // 0～1，滑過時漸變
        private bool _hovering, _pressed;
        private Color _accent = Theme.Accent;

        public Kind Style { get; set; }
        public DialogResult DialogResult { get; set; }

        /// <summary>主色；換顏色時會漸變過去。</summary>
        public Color Accent
        {
            get { return _accent; }
            set { _accent = value; Invalidate(); }
        }

        public RoundButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = Theme.UiBold;
            Size = new Size(120, 36);

            _anim.Tick += delegate
            {
                float target = _hovering ? 1f : 0f;
                _hover += (target - _hover) * 0.25f;
                if (Math.Abs(target - _hover) < 0.02f) { _hover = target; _anim.Stop(); }
                Invalidate();
            };
        }

        public void NotifyDefault(bool value) { }

        public void PerformClick()
        {
            if (CanSelect && Enabled) OnClick(EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            var form = FindForm();
            if (form != null && DialogResult != DialogResult.None) form.DialogResult = DialogResult;
            base.OnClick(e);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovering = true; _anim.Start(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovering = false; _pressed = false; _anim.Start(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Focus(); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) PerformClick();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float press = _pressed ? 1f : 0f;
            var body = new RectangleF(2, 1 + press, Width - 4, Height - 6);
            float radius = body.Height / 2;

            if (Style == Kind.Primary)
            {
                Color disabled = Theme.Dark ? Color.FromArgb(51, 65, 85) : Color.FromArgb(203, 213, 225);
                Color top = Enabled ? Theme.Blend(_accent, Color.White, 0.12f + _hover * 0.1f) : disabled;
                Color bottom = Enabled ? Theme.Blend(_accent, Color.Black, 0.12f - _hover * 0.05f) : disabled;

                // 陰影：往下偏一點、半透明，按下時收起來。
                if (Enabled && !_pressed)
                {
                    var shadow = new RectangleF(body.X + 2, body.Y + 3, body.Width - 4, body.Height);
                    Theme.FillRound(g, Color.FromArgb(50 + (int)(_hover * 30), 0, 0, 0), shadow, radius);
                }

                using (var path = Theme.RoundRect(body, radius))
                using (var brush = new LinearGradientBrush(body, top, bottom, LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }

                // 上半部一道淡淡的高光。
                var gloss = new RectangleF(body.X + 1, body.Y + 1, body.Width - 2, body.Height / 2);
                using (var path = Theme.RoundRect(gloss, radius - 1))
                using (var brush = new LinearGradientBrush(gloss, Color.FromArgb(55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }

                TextRenderer.DrawText(g, Text, Font, Rectangle.Round(body), Enabled ? Color.White : Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                Color fill = Enabled ? Theme.Blend(Theme.Surface, Theme.AccentSoft, _hover) : Theme.SurfaceAlt;
                if (_pressed) fill = Theme.AccentLight;
                Theme.FillRound(g, fill, body, radius);
                Theme.DrawRound(g, Enabled ? Theme.Blend(Theme.Border, _accent, _hover * 0.7f) : Theme.Border, body, radius);
                TextRenderer.DrawText(g, Text, Font, Rectangle.Round(body), Enabled ? Theme.Text : Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            if (Focused && ShowFocusCues)
            {
                var ring = RectangleF.Inflate(body, -3, -3);
                using (var pen = new Pen(Color.FromArgb(160, 255, 255, 255)) { DashStyle = DashStyle.Dot })
                using (var path = Theme.RoundRect(ring, ring.Height / 2))
                {
                    if (Style == Kind.Light) pen.Color = Theme.TextMuted;
                    g.DrawPath(pen, path);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}
