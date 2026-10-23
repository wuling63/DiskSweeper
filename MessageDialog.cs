using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 取代系統 MessageBox 的訊息視窗：左邊彩色圓形圖示（資訊藍、詢問綠、警告琥珀、錯誤紅），
    /// 右邊標題與內文，下方膠囊按鈕。呼叫方式跟 MessageBox.Show 一樣。
    /// </summary>
    public static class Msg
    {
        public static DialogResult Show(IWin32Window owner, string text, string title)
        {
            return Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string title, MessageBoxButtons buttons)
        {
            return Show(owner, text, title, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(owner, text, title, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string title, MessageBoxButtons buttons,
                                        MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            using (var form = new MessageForm(text, title, buttons, icon, defaultButton))
            {
                return form.ShowDialog(owner);
            }
        }

        private class MessageForm : Form
        {
            private readonly Color _color;
            private readonly string _glyph;

            public MessageForm(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
            {
                Text = title;
                Font = Theme.Ui;
                Icon = AppIcon.Get();
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Theme.Surface;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

                // MessageBoxIcon 的 Error／Hand／Stop、Warning／Exclamation、Information／Asterisk 是同一個值。
                switch (icon)
                {
                    case MessageBoxIcon.Error: _color = Theme.Red; _glyph = "✕"; break;
                    case MessageBoxIcon.Warning: _color = Theme.Amber; _glyph = "!"; break;
                    case MessageBoxIcon.Question: _color = Theme.Accent; _glyph = "?"; break;
                    case MessageBoxIcon.Information: _color = Theme.Blue; _glyph = "i"; break;
                    default: _color = Theme.Accent; _glyph = "i"; break;
                }

                // 內文高度依字數算，太長就加捲軸。
                const int textWidth = 380;
                var textSize = TextRenderer.MeasureText(text ?? string.Empty, Theme.Ui, new Size(textWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                int textHeight = Math.Min(360, Math.Max(44, textSize.Height + 8));

                var body = new Label
                {
                    Text = text,
                    Font = Theme.Ui,
                    ForeColor = Theme.Text,
                    Location = new Point(96, 56),
                    Size = new Size(textWidth + 8, textHeight),
                    UseMnemonic = false,
                    AutoEllipsis = textSize.Height + 8 > 360
                };
                var heading = new Label
                {
                    Text = title,
                    Font = Theme.Title,
                    ForeColor = Theme.Text,
                    Location = new Point(96, 22),
                    AutoSize = true,
                    UseMnemonic = false
                };

                var bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 66,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(16, 12, 16, 0),
                    BackColor = Theme.SurfaceAlt
                };
                bar.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0); };

                var made = MakeButtons(buttons);
                // 照 Windows 慣例「是／確定」在左、「否／取消」在右；RightToLeft 版面先加的在最右邊，所以倒著加。
                for (int i = made.Count - 1; i >= 0; i--) bar.Controls.Add(made[i]);

                Controls.Add(heading);
                Controls.Add(body);
                Controls.Add(bar);
                ClientSize = new Size(textWidth + 120, 56 + textHeight + 20 + bar.Height);

                int index = defaultButton == MessageBoxDefaultButton.Button2 ? 1 : defaultButton == MessageBoxDefaultButton.Button3 ? 2 : 0;
                var focus = made[Math.Min(index, made.Count - 1)];
                AcceptButton = focus;
                CancelButton = made.Find(b => b.DialogResult == DialogResult.Cancel || b.DialogResult == DialogResult.No) ?? made[made.Count - 1];
                Shown += delegate { focus.Focus(); };
                Load += delegate
                {
                    Theme.Apply(this);
                    BackColor = Theme.Surface;
                    bar.BackColor = Theme.SurfaceAlt;
                };
            }

            /// <summary>依序回傳按鈕（第一顆是主要動作，用主色）。</summary>
            private static List<RoundButton> MakeButtons(MessageBoxButtons buttons)
            {
                var list = new List<RoundButton>();
                Action<string, DialogResult> add = (text, result) => list.Add(new RoundButton
                {
                    Text = text,
                    DialogResult = result,
                    Size = new Size(104, 42),
                    Margin = new Padding(6, 0, 0, 0),
                    Style = list.Count == 0 ? RoundButton.Kind.Primary : RoundButton.Kind.Light,
                    Font = list.Count == 0 ? Theme.UiBold : Theme.Ui
                });

                switch (buttons)
                {
                    case MessageBoxButtons.OKCancel: add(Lang.T("確定"), DialogResult.OK); add(Lang.T("取消"), DialogResult.Cancel); break;
                    case MessageBoxButtons.YesNo: add(Lang.T("是"), DialogResult.Yes); add(Lang.T("否"), DialogResult.No); break;
                    case MessageBoxButtons.YesNoCancel: add(Lang.T("是"), DialogResult.Yes); add(Lang.T("否"), DialogResult.No); add(Lang.T("取消"), DialogResult.Cancel); break;
                    case MessageBoxButtons.RetryCancel: add(Lang.T("重試"), DialogResult.Retry); add(Lang.T("取消"), DialogResult.Cancel); break;
                    default: add(Lang.T("確定"), DialogResult.OK); break;
                }
                return list;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 左上角圓形圖示：淡色外圈＋實心內圈＋白字符號。
                var outer = new RectangleF(22, 20, 56, 56);
                using (var b = new SolidBrush(Theme.Tint(_color))) g.FillEllipse(b, outer);
                var inner = RectangleF.Inflate(outer, -8, -8);
                using (var b = new LinearGradientBrush(inner, Theme.Blend(_color, Color.White, 0.15f), Theme.Blend(_color, Color.Black, 0.1f), 90f))
                {
                    g.FillEllipse(b, inner);
                }
                using (var f = new Font("Segoe UI", _glyph == "✕" ? 15f : 18f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, _glyph, f, Rectangle.Round(inner), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }
}
