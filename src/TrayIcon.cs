using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WinForms = System.Windows.Forms;

namespace ClipHelper
{
    /// <summary>托盘图标与右键菜单：菜单配色跟随应用主题（深/浅两套），文字、悬停、禁用、分隔线分别处理。</summary>
    public class TrayIcon : IDisposable
    {
        WinForms.NotifyIcon _ni;
        WinForms.ContextMenuStrip _menu;
        WinForms.ToolStripMenuItem _pause;
        WinForms.ToolStripMenuItem _autoStart;
        WinForms.ToolStripMenuItem _clear;
        WinForms.ToolStripMenuItem _themeAuto;
        WinForms.ToolStripMenuItem _themeLight;
        WinForms.ToolStripMenuItem _themeDark;
        bool _paused;
        float _scale = 1f;

        /// <summary>由宿主提供当前状态，供菜单打开时刷新勾选。</summary>
        public Func<bool> GetAutoStart;
        public Func<string> GetTheme;
        public Func<bool> GetCanClear;

        public event Action OpenRequested;
        public event Action SettingsRequested;
        public event Action UpdateRequested;
        public event Action ClearRequested;
        public event Action PauseRequested;
        public event Action ExitRequested;
        public event Action<bool> AutoStartChanged;
        public event Action<string> ThemeChanged;

        public TrayIcon(bool paused)
        {
            _paused = paused;
            _ni = new WinForms.NotifyIcon();
            _ni.Icon = CreateIcon();
            _ni.Text = "剪贴板助手 · Alt+V 呼出";
            _ni.Visible = true;
            _ni.DoubleClick += delegate { if (OpenRequested != null) OpenRequested(); };
            BuildMenu();
        }

        // ------------------------------------------------------------------ 菜单构建
        void BuildMenu()
        {
            DisposeMenu();
            MenuSkin skin = MenuSkin.For(Theme.IsLight);
            _scale = CurrentScale();

            _menu = new WinForms.ContextMenuStrip();
            _menu.BackColor = skin.Bg;
            _menu.ForeColor = skin.Text;
            _menu.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = true;
            _menu.ShowItemToolTips = false;
            _menu.DropShadowEnabled = true;
            _menu.Padding = new WinForms.Padding(Px(3), Px(4), Px(3), Px(4));

            MenuRenderer renderer = new MenuRenderer(skin, _scale);
            _menu.Renderer = renderer;
            RoundCorners(_menu, Px(7));

            WinForms.ToolStripMenuItem open = NewItem("打开剪贴板历史", skin, false);
            open.Font = new Font(_menu.Font, FontStyle.Bold);
            open.ShortcutKeyDisplayString = "Alt+V";
            open.Click += delegate { if (OpenRequested != null) OpenRequested(); };
            _menu.Items.Add(open);
            _menu.Items.Add(NewSeparator(skin));

            _pause = NewItem("暂停记录", skin, true);
            _pause.Click += delegate { if (PauseRequested != null) PauseRequested(); };
            _menu.Items.Add(_pause);

            _autoStart = NewItem("开机自启", skin, true);
            _autoStart.Click += delegate
            {
                bool on = !_autoStart.Checked;
                _autoStart.Checked = on;
                if (AutoStartChanged != null) AutoStartChanged(on);
            };
            _menu.Items.Add(_autoStart);

            WinForms.ToolStripMenuItem appearance = NewItem("外观", skin, false);
            _themeAuto = NewItem("跟随系统", skin, true);
            _themeLight = NewItem("浅色", skin, true);
            _themeDark = NewItem("深色", skin, true);
            appearance.DropDownItems.Add(_themeAuto);
            appearance.DropDownItems.Add(_themeLight);
            appearance.DropDownItems.Add(_themeDark);
            StyleSubMenu(appearance, skin, renderer);
            Action<string> pick = delegate(string v)
            {
                if (ThemeChanged != null) ThemeChanged(v);
            };
            _themeAuto.Click += delegate { pick("auto"); };
            _themeLight.Click += delegate { pick("light"); };
            _themeDark.Click += delegate { pick("dark"); };
            _menu.Items.Add(appearance);
            _menu.Items.Add(NewSeparator(skin));

            WinForms.ToolStripMenuItem settings = NewItem("设置…", skin, false);
            settings.ShortcutKeyDisplayString = "";
            settings.Click += delegate { if (SettingsRequested != null) SettingsRequested(); };
            _menu.Items.Add(settings);

            WinForms.ToolStripMenuItem checkUpdate = NewItem("检查更新", skin, false);
            checkUpdate.Click += delegate { if (UpdateRequested != null) UpdateRequested(); };
            _menu.Items.Add(checkUpdate);

            _clear = NewItem("清空历史", skin, false);
            _clear.Click += delegate { if (ClearRequested != null) ClearRequested(); };
            _menu.Items.Add(_clear);
            _menu.Items.Add(NewSeparator(skin));

            WinForms.ToolStripMenuItem exit = NewItem("退出", skin, false);
            exit.Click += delegate { if (ExitRequested != null) ExitRequested(); };
            _menu.Items.Add(exit);

            _menu.Opening += delegate { SyncState(); };

            _ni.ContextMenuStrip = _menu;
        }

        void SyncState()
        {
            bool autoStart = GetAutoStart == null ? false : GetAutoStart();
            string theme = GetTheme == null ? "auto" : GetTheme();
            if (theme != "light" && theme != "dark") theme = "auto";

            _pause.Checked = _paused;
            _pause.Text = _paused ? "继续记录" : "暂停记录";
            _autoStart.Checked = autoStart;
            _themeAuto.Checked = theme == "auto";
            _themeLight.Checked = theme == "light";
            _themeDark.Checked = theme == "dark";
            if (_clear != null) _clear.Enabled = GetCanClear == null || GetCanClear();
        }

        /// <summary>主题变化后重建菜单（配色、圆角、勾选态都随主题切换）。</summary>
        public void ApplyTheme()
        {
            BuildMenu();
        }

        WinForms.ToolStripMenuItem NewItem(string text, MenuSkin skin, bool checkable)
        {
            WinForms.ToolStripMenuItem it = new WinForms.ToolStripMenuItem(text);
            it.ForeColor = skin.Text;
            it.Padding = new WinForms.Padding(Px(10), Px(5), Px(10), Px(5));
            it.CheckOnClick = false;
            if (!checkable) it.Checked = false;
            return it;
        }

        WinForms.ToolStripSeparator NewSeparator(MenuSkin skin)
        {
            WinForms.ToolStripSeparator sp = new WinForms.ToolStripSeparator();
            sp.Margin = new WinForms.Padding(Px(6), Px(3), Px(6), Px(3));
            return sp;
        }

        void StyleSubMenu(WinForms.ToolStripMenuItem owner, MenuSkin skin, MenuRenderer renderer)
        {
            WinForms.ToolStripDropDownMenu dd = owner.DropDown as WinForms.ToolStripDropDownMenu;
            if (dd == null) return;
            dd.BackColor = skin.Bg;
            dd.ForeColor = skin.Text;
            dd.Font = _menu.Font;
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = true;
            dd.DropShadowEnabled = true;
            dd.Padding = new WinForms.Padding(Px(3), Px(4), Px(3), Px(4));
            dd.Renderer = renderer;
            RoundCorners(dd, Px(7));
        }

        void RoundCorners(WinForms.ToolStripDropDown dd, int radius)
        {
            dd.SizeChanged += delegate
            {
                if (dd.Width <= 0 || dd.Height <= 0) return;
                try
                {
                    using (GraphicsPath p = Rounded(new Rectangle(0, 0, dd.Width, dd.Height), radius))
                    {
                        Region old = dd.Region;
                        dd.Region = new Region(p);
                        if (old != null) old.Dispose();
                    }
                }
                catch { }
            };
        }

        int Px(float v)
        {
            return (int)Math.Round(v * _scale);
        }

        static float CurrentScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return Math.Max(1f, g.DpiX / 96f);
                }
            }
            catch { return 1f; }
        }

        // ------------------------------------------------------------------ 状态
        public void SetPaused(bool paused)
        {
            _paused = paused;
            _ni.Text = paused ? "剪贴板助手 · 已暂停记录" : "剪贴板助手 · Alt+V 呼出";
        }

        public void Balloon(string title, string text)
        {
            try
            {
                _ni.BalloonTipTitle = title;
                _ni.BalloonTipText = text;
                _ni.ShowBalloonTip(3000);
            }
            catch { }
        }

        // ------------------------------------------------------------------ 图标
        static Icon CreateIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    RectangleF bg = new RectangleF(1.5f, 1.5f, 29f, 29f);
                    using (GraphicsPath path = Rounded(bg, 8f))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 15, 108, 189)))
                    {
                        g.FillPath(b, path);
                    }

                    using (Pen pen = new Pen(Color.FromArgb(255, 255, 255, 255), 1.9f))
                    {
                        pen.LineJoin = LineJoin.Round;
                        RectangleF body = new RectangleF(9f, 9.5f, 14f, 15f);
                        using (GraphicsPath bp = Rounded(body, 2.2f))
                        {
                            g.DrawPath(pen, bp);
                        }
                        RectangleF clip = new RectangleF(12.5f, 6.8f, 7f, 4.6f);
                        using (GraphicsPath cp = Rounded(clip, 1.4f))
                        using (SolidBrush wb = new SolidBrush(Color.White))
                        {
                            g.FillPath(wb, cp);
                        }
                        g.DrawLine(pen, 12f, 15f, 20f, 15f);
                        g.DrawLine(pen, 12f, 18.5f, 18f, 18.5f);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            try
            {
                DisposeMenu();
                _ni.Visible = false;
                _ni.Dispose();
            }
            catch { }
        }

        void DisposeMenu()
        {
            try
            {
                if (_menu != null)
                {
                    _ni.ContextMenuStrip = null;
                    _menu.Dispose();
                    _menu = null;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ 配色
        class MenuSkin
        {
            public Color Bg;
            public Color Text;
            public Color TextDim;
            public Color Disabled;
            public Color Hover;
            public Color Border;
            public Color Sep;
            public Color Accent;

            public static MenuSkin For(bool light)
            {
                MenuSkin s = new MenuSkin();
                if (light)
                {
                    s.Bg = Color.FromArgb(0xFC, 0xFC, 0xFD);
                    s.Text = Color.FromArgb(0x14, 0x17, 0x1C);
                    s.TextDim = Color.FromArgb(0x56, 0x5C, 0x66);
                    s.Disabled = Color.FromArgb(0x9A, 0xA1, 0xAA);
                    s.Hover = Color.FromArgb(0xED, 0xF0, 0xF4);
                    s.Border = Color.FromArgb(0xDC, 0xE0, 0xE6);
                    s.Sep = Color.FromArgb(0xE3, 0xE7, 0xEC);
                    s.Accent = Color.FromArgb(0x0F, 0x6C, 0xBD);
                }
                else
                {
                    s.Bg = Color.FromArgb(0x2C, 0x2E, 0x32);
                    s.Text = Color.FromArgb(0xF2, 0xF4, 0xF7);
                    s.TextDim = Color.FromArgb(0xA2, 0xA8, 0xB3);
                    s.Disabled = Color.FromArgb(0x7A, 0x82, 0x8E);
                    s.Hover = Color.FromArgb(0x3A, 0x3D, 0x42);
                    s.Border = Color.FromArgb(0x3E, 0x40, 0x45);
                    s.Sep = Color.FromArgb(0x3A, 0x3D, 0x42);
                    s.Accent = Color.FromArgb(0x60, 0xCD, 0xFF);
                }
                return s;
            }
        }

        /// <summary>自绘渲染器：保证深/浅两套下文字、悬停、禁用、分隔线、勾选、快捷键都有对比度。</summary>
        class MenuRenderer : WinForms.ToolStripProfessionalRenderer
        {
            MenuSkin _skin;
            float _scale;

            public MenuRenderer(MenuSkin skin, float scale)
                : base(new MenuTable(skin))
            {
                _skin = skin;
                _scale = scale;
                RoundedEdges = false;
            }

            int Px(float v) { return (int)Math.Round(v * _scale); }

            protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(_skin.Bg))
                {
                    e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
                }
            }

            protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(_skin.Bg))
                {
                    e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
                }
            }

            protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
            {
                Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                using (Pen p = new Pen(_skin.Border))
                {
                    e.Graphics.DrawRectangle(p, r);
                }
            }

            protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled || e.Item.IsOnDropDown == false) return;
                Rectangle r = new Rectangle(Px(2), 0, Math.Max(0, e.Item.Width - Px(4)), e.Item.Height);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = RoundedFill(r, Px(4)))
                using (SolidBrush b = new SolidBrush(_skin.Hover))
                {
                    e.Graphics.FillPath(b, p);
                }
            }

            static GraphicsPath RoundedFill(Rectangle r, float radius)
            {
                GraphicsPath p = new GraphicsPath();
                float d = radius * 2;
                if (d > r.Width) d = r.Width;
                if (d > r.Height) d = r.Height;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
            {
                WinForms.ToolStripMenuItem mi = e.Item as WinForms.ToolStripMenuItem;
                bool isShortcut = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
                                  && e.Text == mi.ShortcutKeyDisplayString;
                if (!e.Item.Enabled) e.TextColor = _skin.Disabled;
                else if (isShortcut) e.TextColor = _skin.TextDim;
                else e.TextColor = _skin.Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                if (r.IsEmpty) return;
                using (SolidBrush bg = new SolidBrush(_skin.Bg))
                {
                    e.Graphics.FillRectangle(bg, r);
                }
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = r.X + r.Width / 2f;
                float cy = r.Y + r.Height / 2f;
                using (Pen p = new Pen(_skin.Accent, 1.7f * _scale))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    PointF[] pts = new PointF[]
                    {
                        new PointF(cx - 4.5f * _scale, cy + 0.2f * _scale),
                        new PointF(cx - 1.4f * _scale, cy + 3.2f * _scale),
                        new PointF(cx + 4.8f * _scale, cy - 3.4f * _scale)
                    };
                    e.Graphics.DrawLines(p, pts);
                }
            }

            protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
            {
                if (e.Item.Width <= 0) return;
                int y = e.Item.Height / 2;
                using (Pen p = new Pen(_skin.Sep))
                {
                    e.Graphics.DrawLine(p, Px(8), y, e.Item.Width - Px(8), y);
                }
            }

            protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? _skin.TextDim : _skin.Disabled;
                base.OnRenderArrow(e);
            }

            class MenuTable : WinForms.ProfessionalColorTable
            {
                MenuSkin _s;
                public MenuTable(MenuSkin s) { _s = s; }

                public override Color ToolStripDropDownBackground { get { return _s.Bg; } }
                public override Color ImageMarginGradientBegin { get { return _s.Bg; } }
                public override Color ImageMarginGradientMiddle { get { return _s.Bg; } }
                public override Color ImageMarginGradientEnd { get { return _s.Bg; } }
                public override Color MenuBorder { get { return _s.Border; } }
                public override Color MenuItemBorder { get { return _s.Hover; } }
                public override Color MenuItemSelected { get { return _s.Hover; } }
                public override Color MenuItemSelectedGradientBegin { get { return _s.Hover; } }
                public override Color MenuItemSelectedGradientEnd { get { return _s.Hover; } }
                public override Color MenuItemPressedGradientBegin { get { return _s.Hover; } }
                public override Color MenuItemPressedGradientMiddle { get { return _s.Hover; } }
                public override Color MenuItemPressedGradientEnd { get { return _s.Hover; } }
                public override Color CheckBackground { get { return _s.Bg; } }
                public override Color CheckSelectedBackground { get { return _s.Hover; } }
                public override Color CheckPressedBackground { get { return _s.Hover; } }
                public override Color SeparatorDark { get { return _s.Sep; } }
                public override Color SeparatorLight { get { return _s.Bg; } }
            }
        }
    }
}
