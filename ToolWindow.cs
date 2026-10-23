using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 工具視窗的共用外框：上方漸層標題列（圖示、標題、說明）、中間內容區、下方狀態列，
    /// 開啟時自動套用目前的主題。子類別把控制項加進 Content。
    /// </summary>
    public class ToolWindow : Form
    {
        protected readonly Panel Content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 10) };
        protected readonly ToolStripStatusLabel Status = new ToolStripStatusLabel(Lang.T("就緒")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly DialogHeader _header = new DialogHeader { Dock = DockStyle.Top, Height = 68 };

        public ToolWindow(string title, string subtitle, string glyph, Size size)
        {
            Text = title;
            _header.Title = title;
            _header.Subtitle = subtitle;
            _header.Glyph = glyph;
            Font = Theme.Ui;
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = size;
            MinimumSize = new Size(640, 420);

            var status = new StatusStrip { SizingGrip = true };
            status.Items.Add(Status);

            Controls.Add(Content);
            Controls.Add(status);
            Controls.Add(_header);

            Load += delegate { Theme.Apply(this); };
        }

        /// <summary>上方一列按鈕／輸入元件（靠左排）。</summary>
        protected FlowLayoutPanel Toolbar(params Control[] controls)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false, Padding = new Padding(0, 4, 0, 6) };
            foreach (var c in controls)
            {
                c.Margin = new Padding(0, c is Label ? 8 : c is CheckBox ? 6 : 0, 8, 0);
                bar.Controls.Add(c);
            }
            return bar;
        }

        protected static RoundButton Primary(string text, int width = 120)
        {
            return new RoundButton { Text = text, Size = new Size(width, 38) };
        }

        protected static RoundButton Light(string text, int width = 120)
        {
            return new RoundButton { Text = text, Size = new Size(width, 38), Style = RoundButton.Kind.Light, Font = Theme.Ui };
        }

        /// <summary>白色卡片包起來的清單。</summary>
        protected static CardPanel Card(Control inner)
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1, 1, 1, 6) };
            inner.Dock = DockStyle.Fill;
            card.Controls.Add(inner);
            return card;
        }

        protected static ThemedListView NewList(params string[] columns)
        {
            var list = new ThemedListView { View = View.Details, FullRowSelect = true, HideSelection = false };
            foreach (string c in columns)
            {
                // 「名稱:寬度:r」＝靠右的欄位
                var parts = c.Split(':');
                int width = parts.Length > 1 ? int.Parse(parts[1]) : 120;
                list.Columns.Add(parts[0], width, parts.Length > 2 && parts[2] == "r" ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            }
            return list;
        }
    }
}
