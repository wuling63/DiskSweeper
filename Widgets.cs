using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>圓角搜尋框：左邊放大鏡、沒輸入時顯示灰色提示字、取得焦點時外框變主色。</summary>
    public class SearchBox : Control
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

        private readonly TextBox _box = new TextBox { BorderStyle = BorderStyle.None };
        private readonly string _cue;

        public TextBox Box { get { return _box; } }
        public override string Text { get { return _box.Text; } set { _box.Text = value; } }

        public SearchBox(string cue)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _cue = cue;
            BackColor = Theme.Background;
            Height = 32;
            _box.Font = Theme.Ui;
            _box.BackColor = Theme.Surface;
            _box.ForeColor = Theme.Text;
            Controls.Add(_box);

            _box.HandleCreated += delegate { SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, _cue); };
            _box.GotFocus += delegate { Invalidate(); };
            _box.LostFocus += delegate { Invalidate(); };
            _box.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _box.SetBounds(34, (Height - _box.PreferredHeight) / 2 + 1, Width - 46, _box.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Surface, r, Height / 2f);
            Theme.DrawRound(g, _box.Focused ? Theme.Accent : Theme.Border, r, Height / 2f, _box.Focused ? 1.6f : 1f);

            // 放大鏡
            using (var pen = new Pen(_box.Focused ? Theme.Accent : Theme.TextMuted, 1.8f))
            {
                g.DrawEllipse(pen, 13, Height / 2f - 7, 10, 10);
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 21.5f, Height / 2f + 1.5f, 25.5f, Height / 2f + 5.5f);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }
    }

    /// <summary>
    /// 跟主題走的 ListView：扁平表頭、間隔列、主色選取，深色模式也看得清楚。
    /// 有勾選框時自己畫圓角框（點擊勾選仍由系統處理）。用法跟一般 ListView 一樣。
    /// </summary>
    public class ThemedListView : ListView
    {
        public ThemedListView()
        {
            OwnerDraw = true;
            DoubleBuffered = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var back = new SolidBrush(Theme.HeaderRow)) e.Graphics.FillRectangle(back, e.Bounds);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.SmallBold, Rectangle.Inflate(e.Bounds, -6, 0), Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | Align(e.Header.TextAlign));
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            // 細節檢視交給 DrawSubItem；其他檢視用系統預設。
            if (View != View.Details) e.DrawDefault = true;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            bool selected = e.Item.Selected;
            Color back = selected ? Theme.AccentLight : e.ItemIndex % 2 == 1 ? Theme.SurfaceAlt : Theme.Surface;
            using (var brush = new SolidBrush(back)) g.FillRectangle(brush, e.Bounds);

            var r = Rectangle.Inflate(e.Bounds, -6, 0);
            if (e.ColumnIndex == 0 && CheckBoxes)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var box = new RectangleF(r.Left, r.Top + (r.Height - 14) / 2f, 14, 14);
                if (e.Item.Checked)
                {
                    Theme.FillRound(g, Theme.Accent, box, 4);
                    using (var pen = new Pen(Color.White, 2f))
                    {
                        g.DrawLines(pen, new[] { new PointF(box.Left + 3.5f, box.Top + 7.5f), new PointF(box.Left + 6, box.Top + 10), new PointF(box.Left + 11, box.Top + 4.5f) });
                    }
                }
                else Theme.DrawRound(g, Color.FromArgb(148, 163, 184), box, 4, 1.4f);
                r = new Rectangle(r.Left + 22, r.Top, r.Width - 22, r.Height);
            }

            // 呼叫端特別指定的字色（例如殘留項目的灰字）照用，否則主欄用主要文字色、其他欄用次要色。
            Color custom = e.Item.ForeColor;
            bool hasCustom = custom != ForeColor && custom != SystemColors.WindowText && custom != Color.Empty;
            Color fore = hasCustom ? custom : e.ColumnIndex == 0 ? Theme.Text : Theme.TextMuted;

            var align = Columns.Count > e.ColumnIndex ? Columns[e.ColumnIndex].TextAlign : HorizontalAlignment.Left;
            TextRenderer.DrawText(g, e.SubItem.Text, Font, r, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | Align(align));
        }

        private static TextFormatFlags Align(HorizontalAlignment a)
        {
            return a == HorizontalAlignment.Right ? TextFormatFlags.Right : a == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        }

        // 勾選框是自己畫的，勾選狀態變了要重畫那一列。
        protected override void OnItemChecked(ItemCheckedEventArgs e)
        {
            base.OnItemChecked(e);
            if (e.Item != null && e.Item.Index >= 0) Invalidate(e.Item.Bounds);
        }
    }

    /// <summary>沒有水平捲軸的 TreeView：文字自己畫、過長的用省略號，不需要左右捲。</summary>
    public class NoHScrollTreeView : TreeView
    {
        private const int TVS_NOHSCROLL = 0x8000;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= TVS_NOHSCROLL;
                return cp;
            }
        }
    }

    /// <summary>圓角提示卡：琥珀色底、左邊警告圖示。Text 空白時自動隱藏。</summary>
    public class Banner : Control
    {
        public Banner()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            Height = 44;
            Padding = new Padding(10, 8, 10, 0);
            Font = Theme.Ui;
            Visible = false;   // 一開始沒有文字；設了 Text 才出現
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Visible = !string.IsNullOrEmpty(Text);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(Padding.Left, Padding.Top, Width - Padding.Horizontal - 1, Height - Padding.Top - 2);
            Theme.FillRound(g, Theme.Dark ? Color.FromArgb(67, 33, 10) : Color.FromArgb(255, 247, 237), r, 8);
            Theme.DrawRound(g, Theme.Dark ? Color.FromArgb(154, 52, 18) : Color.FromArgb(253, 186, 116), r, 8);

            // 三角形警告
            float cx = r.Left + 20, cy = r.Top + r.Height / 2;
            var tri = new[] { new PointF(cx, cy - 8), new PointF(cx + 9, cy + 7), new PointF(cx - 9, cy + 7) };
            using (var brush = new SolidBrush(Theme.Orange)) g.FillPolygon(brush, tri);
            TextRenderer.DrawText(g, "!", Theme.SmallBold, Rectangle.Round(new RectangleF(cx - 6, cy - 6, 12, 14)), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(new RectangleF(r.Left + 38, r.Top, r.Width - 46, r.Height)),
                Theme.Dark ? Color.FromArgb(253, 186, 116) : Color.FromArgb(154, 52, 18),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}
