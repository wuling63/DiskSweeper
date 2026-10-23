using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskCleaner
{
    /// <summary>全程式共用的配色、字型與元件樣式。</summary>
    public static class Theme
    {
        // ── 外觀模式 ──

        /// <summary>目前是不是深色。由 SetMode 決定；所有顏色屬性都依它回傳淺色或深色版。</summary>
        public static bool Dark { get; private set; }

        /// <summary>"light"、"dark" 或 "system"（跟著 Windows 的「應用程式模式」）。</summary>
        public static bool ResolveDark(string mode)
        {
            if (mode == "dark") return true;
            if (mode == "light") return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception) { return false; }
        }

        // ── 配色（淺色／深色） ──
        public static Color Accent { get { return Color.FromArgb(16, 185, 129); } }                    // 翡翠綠
        public static Color AccentDark { get { return Dark ? Color.FromArgb(52, 211, 153) : Color.FromArgb(4, 120, 87); } }
        public static Color AccentLight { get { return Dark ? Color.FromArgb(6, 78, 59) : Color.FromArgb(209, 250, 229); } }
        public static Color AccentSoft { get { return Dark ? Color.FromArgb(6, 55, 45) : Color.FromArgb(236, 253, 245); } }
        public static Color Orange { get { return Color.FromArgb(249, 115, 22); } }
        public static Color Red { get { return Color.FromArgb(239, 68, 68); } }
        public static Color Amber { get { return Color.FromArgb(245, 158, 11); } }
        public static Color Blue { get { return Color.FromArgb(59, 130, 246); } }

        public static Color Background { get { return Dark ? Color.FromArgb(15, 23, 42) : Color.FromArgb(241, 245, 249); } }   // 視窗底
        public static Color Surface { get { return Dark ? Color.FromArgb(30, 41, 59) : Color.White; } }                       // 卡片
        public static Color SurfaceAlt { get { return Dark ? Color.FromArgb(36, 49, 69) : Color.FromArgb(248, 250, 252); } }  // 間隔列
        public static Color Border { get { return Dark ? Color.FromArgb(51, 65, 85) : Color.FromArgb(226, 232, 240); } }
        public static Color Text { get { return Dark ? Color.FromArgb(226, 232, 240) : Color.FromArgb(30, 41, 59); } }
        public static Color TextMuted { get { return Dark ? Color.FromArgb(148, 163, 184) : Color.FromArgb(100, 116, 139); } }
        public static Color HeaderRow { get { return Dark ? Color.FromArgb(39, 52, 74) : Color.FromArgb(241, 245, 249); } }

        /// <summary>淡色底的標籤（例如容量膠囊）：淺色模式混白、深色模式混卡片色。</summary>
        public static Color Tint(Color c) { return Blend(c, Surface, Dark ? 0.75f : 0.82f); }

        /// <summary>標籤上的字：淺色模式壓暗、深色模式提亮。</summary>
        public static Color TintText(Color c) { return Dark ? Blend(c, Color.White, 0.35f) : Blend(c, Color.Black, 0.35f); }

        /// <summary>切換模式：先記下舊的配色，換好後把所有開著的視窗裡用到舊顏色的地方換成新顏色，再重畫。</summary>
        public static void SetMode(bool dark)
        {
            if (dark == Dark && _applied) return;
            var before = Palette();
            Dark = dark;
            _applied = true;
            var after = Palette();

            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < before.Length; i++)
            {
                if (!map.ContainsKey(before[i])) map[before[i]] = after[i];
            }
            // 對話框裡寫死的幾種灰色說明字，也一起換成新的次要文字色。
            foreach (var gray in new[] { Color.FromArgb(90, 90, 100), Color.FromArgb(80, 80, 90), Color.FromArgb(120, 120, 130) })
            {
                map[gray] = TextMuted;
            }

            foreach (Form f in Application.OpenForms)
            {
                Recolor(f, map);
                Apply(f);
                f.Invalidate(true);
            }
        }

        private static bool _applied;

        private static Color[] Palette()
        {
            return new[] { Background, Surface, SurfaceAlt, Border, Text, TextMuted, HeaderRow, AccentLight, AccentSoft, AccentDark };
        }

        private static void Recolor(Control c, Dictionary<Color, Color> map)
        {
            Color to;
            if (map.TryGetValue(c.BackColor, out to) && c.BackColor != Color.Transparent) c.BackColor = to;
            if (map.TryGetValue(c.ForeColor, out to)) c.ForeColor = to;
            var link = c as LinkLabel;
            if (link != null) { link.LinkColor = AccentDark; link.ActiveLinkColor = Accent; }
            foreach (Control child in c.Controls) Recolor(child, map);
        }

        /// <summary>分類顏色：環圈圖、圖例、樹狀清單的圓點共用。</summary>
        public static Color CategoryColor(string category)
        {
            switch (category)
            {
                case Cleaner.CatWindows: return Blue;
                case Cleaner.CatBrowser: return Amber;
                case Cleaner.CatApps: return Color.FromArgb(139, 92, 246);
                case Cleaner.CatDev: return Accent;
                case Cleaner.CatPrivacy: return Color.FromArgb(244, 63, 94);
                default: return Color.FromArgb(100, 116, 139);
            }
        }

        // ── 字型 ──
        public static readonly Font Ui = new Font("Microsoft JhengHei UI", 9.5f);
        public static readonly Font UiBold = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Bold);
        public static readonly Font Small = new Font("Microsoft JhengHei UI", 8.5f);
        public static readonly Font SmallBold = new Font("Microsoft JhengHei UI", 8.5f, FontStyle.Bold);
        public static readonly Font Title = new Font("Microsoft JhengHei UI", 11f, FontStyle.Bold);
        public static readonly Font Number = new Font("Segoe UI Semibold", 30f);
        public static readonly Font NumberSmall = new Font("Segoe UI Semibold", 12f);

        // ── 繪圖小工具 ──

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        public static void DrawRound(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
        {
            using (var path = RoundRect(r, radius))
            using (var pen = new Pen(color, width))
            {
                g.DrawPath(pen, path);
            }
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ── 套用到整個視窗 ──

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int LVM_GETHEADER = 0x101F;

        /// <summary>
        /// 給視窗套用目前的配色：底色、文字色、選單配色、一般按鈕改成扁平圓潤的樣子，
        /// 深色模式時標題列、捲軸、清單也換成深色。在 Load 時呼叫（控制項都建好了）。
        /// </summary>
        public static void Apply(Control root)
        {
            var form = root as Form;
            if (form != null)
            {
                form.BackColor = Background;
                form.ForeColor = Text;
                // Windows 10 1809 起是 20，更早的版本是 19；兩個都試。
                if (form.IsHandleCreated)
                {
                    int on = Dark ? 1 : 0;
                    if (DwmSetWindowAttribute(form.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(form.Handle, 19, ref on, 4);
                }
            }
            Style(root, false);
        }

        /// <param name="inCard">在白色卡片裡面的容器維持卡片底色，外面的用視窗底色。</param>
        private static void Style(Control c, bool inCard)
        {
            var strip = c as ToolStrip;
            if (strip != null)
            {
                strip.Renderer = new ModernRenderer();
                strip.BackColor = Surface;
                strip.ForeColor = Text;
            }
            if (c.ContextMenuStrip != null) c.ContextMenuStrip.Renderer = new ModernRenderer();

            var button = c as Button;
            if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Surface;
                button.ForeColor = Text;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = AccentSoft;
                button.FlatAppearance.MouseDownBackColor = AccentLight;
                button.Cursor = Cursors.Hand;
            }

            if (c is FlowLayoutPanel || (c is Panel && !(c is CardPanel) && !(c is HeaderPanel))) c.BackColor = inCard ? Surface : Background;
            if (c is TabPage) c.BackColor = Surface;
            if (c is ListView || c is TreeView || c is ListBox || c is TextBox || c is ComboBox || c is NumericUpDown)
            {
                c.BackColor = Surface;
                c.ForeColor = Text;
            }

            // 清單、樹、捲軸用系統的深色樣式（Windows 10 1809 起支援）。
            if ((c is ListView || c is TreeView || c is ListBox || c is TextBox) && c.IsHandleCreated)
            {
                SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
                if (c is ListView)
                {
                    // 表頭是另一個子視窗；最後一欄右邊的空白區由它自己畫，不換樣式會留一塊白的。
                    IntPtr header = SendMessage(c.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, Dark ? "DarkMode_ItemsView" : "ItemsView", null);
                }
            }

            bool childInCard = inCard || c is CardPanel || c is TabPage;
            foreach (Control child in c.Controls) Style(child, childInCard);
        }
    }

    /// <summary>選單、右鍵選單、狀態列的配色：淡綠選取、細邊框、圓角高亮。</summary>
    public class ModernRenderer : ToolStripProfessionalRenderer
    {
        public ModernRenderer() : base(new ModernColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) { base.OnRenderMenuItemBackground(e); return; }
            if (!e.Item.Enabled) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            Theme.FillRound(g, e.Item.Pressed && e.Item.IsOnDropDown == false ? Theme.AccentLight : Theme.AccentLight, r, 4);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextMuted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                using (var pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                }
            }
        }

        private class ModernColors : ProfessionalColorTable
        {
            public override Color MenuStripGradientBegin { get { return Theme.Surface; } }
            public override Color MenuStripGradientEnd { get { return Theme.Surface; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
            public override Color MenuItemSelected { get { return Theme.AccentSoft; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.AccentSoft; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.AccentSoft; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.AccentLight; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.AccentLight; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Surface; } }
            public override Color StatusStripGradientBegin { get { return Theme.Surface; } }
            public override Color StatusStripGradientEnd { get { return Theme.Surface; } }
        }
    }

    /// <summary>白底圓角卡片，裡面的控制項靠 Padding 留出邊框。</summary>
    public class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Padding = new Padding(1);
            BackColor = Theme.Background;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Surface, r, 8);
            Theme.DrawRound(g, Theme.Border, r, 8);
        }
    }
}
