using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace ClipHelper
{
    public class OverlayWindow : Window
    {
        public const double CardW = 452;
        public const double CardH = 606;

        Store _store;
        AppSettings _cfg;

        TextBox _search;
        TextBlock _placeholder;
        Border _searchBox;
        StackPanel _chips;
        StackPanel _list;
        ScrollViewer _scroll;
        TextBlock _footCount;
        Border _banner;
        Border _syncDot;
        Border _toast;
        TextBlock _toastText;
        ClickBox _toastUndo;
        Grid _contentGrid;
        Border _empty;

        List<ClipItem> _view = new List<ClipItem>();
        List<Row> _rows = new List<Row>();
        int _sel = -1;
        string _filter = "all";
        string _timeMode = "all";              // all | today | yesterday | week | custom
        DateTime _customDate = DateTime.Today;
        Grid _timeBar;
        Border _sticky;
        TextBlock _stickyText;
        Popup _datePopup;
        Grid _calDays;
        TextBlock _calTitle;
        TextBox _dateInput;
        Border _dateInputBox;
        DateTime _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        List<Border> _groupHeaders = new List<Border>();
        List<string> _groupLabels = new List<string>();
        ClickBox _emptyToday;
        IntPtr _target;
        DispatcherTimer _clickTimer;
        DispatcherTimer _toastTimer;
        ClipItem _pendingClick;
        ClipItem _delItem;
        int _delIndex;

        // 分页渲染（虚拟化-lite）：先只为第一屏建视觉元素，滚动接近底部再追加
        const int PageSize = 40;
        List<object> _plan = new List<object>();   // string = 分组标题，ClipItem = 记录行
        int _planPos;
        int _renderTarget;
        bool _rendering;
        TextBlock _moreHint;

        // 日期切换：首次按键立即生效；90ms 内的连按合并为一次刷新
        DispatcherTimer _dayTimer;
        System.Diagnostics.Stopwatch _daySw;
        bool _dayPending;
        int _dayMerged;
        DateTime _dayLastApply = DateTime.MinValue;

        class Row
        {
            public Border Root;
            public Border Bar;
            public Border ActionBox;
            public ClipItem Item;
            public StackPanel Actions;
        }

        public OverlayWindow(Store store, AppSettings cfg)
        {
            _store = store;
            _cfg = cfg;

            Ui.SetupWindow(this, true);
            WindowStyle = WindowStyle.None;
            Width = CardW;
            Height = CardH;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            BuildChrome();
            Ui.SlimScroll(this);

            _clickTimer = new DispatcherTimer();
            _clickTimer.Interval = TimeSpan.FromMilliseconds(230);
            _clickTimer.Tick += delegate
            {
                _clickTimer.Stop();
                if (_pendingClick != null) Activate(_pendingClick, false);
                _pendingClick = null;
            };

            _toastTimer = new DispatcherTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(3);
            _toastTimer.Tick += delegate
            {
                _toastTimer.Stop();
                HideToast();
                if (_delItem != null)
                {
                    _store.DeleteFilesOnly(_delItem);
                    _delItem = null;
                }
            };

            Deactivated += delegate
            {
                if (IsVisible) HideOverlay();
            };

            Loaded += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Native.RoundCorners(hwnd);
            };
        }

        // ------------------------------------------------------------------ 界面骨架
        void BuildChrome()
        {
            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(33) });

            // ---- 头部：搜索 + 操作
            Grid head = new Grid();
            head.Margin = new Thickness(12, 12, 12, 8);
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _searchBox = new Border();
            _searchBox.Height = 34;
            _searchBox.CornerRadius = new CornerRadius(6);
            _searchBox.Background = Theme.Field;
            _searchBox.BorderBrush = Theme.FieldBorder;
            _searchBox.BorderThickness = new Thickness(1);
            _searchBox.Padding = new Thickness(10, 0, 6, 0);

            Grid sgrid = new Grid();
            sgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement sico = Ui.Icon("M2.6,7 A4.4,4.4 0 1 1 11.4,7 A4.4,4.4 0 1 1 2.6,7 M10.4,10.4 L14,14", 15, Theme.Txt3, 1.4);
            sico.Margin = new Thickness(0, 0, 8, 0);
            sico.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sico, 0);
            sgrid.Children.Add(sico);

            _search = new TextBox();
            _search.BorderThickness = new Thickness(0);
            _search.Background = Brushes.Transparent;
            _search.Foreground = Theme.Txt;
            _search.CaretBrush = Theme.Acc;
            _search.FontSize = 13;
            _search.FontFamily = Theme.Font;
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.FocusVisualStyle = null;
            _search.Padding = new Thickness(0);
            _search.TextChanged += delegate
            {
                _placeholder.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
                ClearButtonVisibility();
                Rebuild();
            };
            _search.GotKeyboardFocus += delegate { _searchBox.BorderBrush = Theme.Acc; };
            _search.LostKeyboardFocus += delegate { _searchBox.BorderBrush = Theme.FieldBorder; };
            _search.PreviewKeyDown += SearchKeyDown;
            Grid.SetColumn(_search, 1);
            sgrid.Children.Add(_search);

            _placeholder = Ui.Text("搜索你复制过的内容…", 13, Theme.Txt3);
            _placeholder.IsHitTestVisible = false;
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            _placeholder.Margin = new Thickness(0, 0, 0, 1);
            Grid.SetColumn(_placeholder, 1);
            sgrid.Children.Add(_placeholder);

            ClickBox clear = new ClickBox();
            clear.Width = 18;
            clear.Height = 18;
            clear.CornerRadius = new CornerRadius(9);
            clear.HoverBg = Theme.Hover;
            clear.VerticalAlignment = VerticalAlignment.Center;
            clear.Child = Ui.Icon("M4,4 L12,12 M12,4 L4,12", 10, Theme.Txt3, 1.6);
            clear.Clicked = delegate { _search.Text = ""; _search.Focus(); };
            clear.Visibility = Visibility.Collapsed;
            clear.Name = "ClearBtn";
            Grid.SetColumn(clear, 2);
            sgrid.Children.Add(clear);

            _searchBox.Child = sgrid;
            Grid.SetColumn(_searchBox, 0);
            head.Children.Add(_searchBox);

            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.Margin = new Thickness(6, 0, 0, 0);
            actions.VerticalAlignment = VerticalAlignment.Center;

            // 同步状态点：灰=未启用/未配对，绿=已同步，琥珀=进行中/离线/积压，红=失败
            _syncDot = new Border();
            _syncDot.Width = 7;
            _syncDot.Height = 7;
            _syncDot.CornerRadius = new CornerRadius(4);
            _syncDot.Margin = new Thickness(0, 0, 9, 0);
            _syncDot.VerticalAlignment = VerticalAlignment.Center;
            _syncDot.Visibility = Visibility.Collapsed;
            actions.Children.Add(_syncDot);

            ClickBox pauseBtn = Ui.IconBtn(PauseIcon(Theme.Txt2), "暂停 / 恢复记录", delegate
            {
                _cfg.Paused = !_cfg.Paused;
                _cfg.Save();
                RefreshBanner();
                AppHost.Current.NotifyPauseChanged();
            });
            actions.Children.Add(pauseBtn);

            ClickBox setBtn = Ui.IconBtn(Ui.Icon(GearPath, 16, Theme.Txt2, 1.3), "设置", delegate
            {
                HideOverlay();
                AppHost.Current.ShowSettings();
            });
            actions.Children.Add(setBtn);

            Grid.SetColumn(actions, 1);
            head.Children.Add(actions);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // ---- 暂停提示条
            _banner = new Border();
            _banner.CornerRadius = new CornerRadius(6);
            _banner.Background = Theme.AccSoft;
            _banner.Padding = new Thickness(10, 7, 10, 7);
            _banner.Margin = new Thickness(12, 0, 12, 8);
            _banner.Visibility = Visibility.Collapsed;

            Grid bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement bp = PauseIcon(Theme.Acc);
            bp.VerticalAlignment = VerticalAlignment.Center;
            bp.Margin = new Thickness(0, 0, 8, 0);
            bg.Children.Add(bp);
            TextBlock btxt = Ui.Text("记录已暂停 · 新的复制不会被记录", 11.5, Theme.Acc);
            btxt.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(btxt, 1);
            bg.Children.Add(btxt);
            ClickBox resume = new ClickBox();
            resume.Child = Ui.Text("恢复", 11.5, Theme.Acc);
            resume.VerticalAlignment = VerticalAlignment.Center;
            resume.Padding = new Thickness(6, 2, 6, 2);
            resume.Clicked = delegate
            {
                _cfg.Paused = false;
                _cfg.Save();
                RefreshBanner();
                AppHost.Current.NotifyPauseChanged();
            };
            Grid.SetColumn(resume, 2);
            bg.Children.Add(resume);
            _banner.Child = bg;
            Grid.SetRow(_banner, 1);
            root.Children.Add(_banner);

            // ---- 筛选
            _chips = new StackPanel();
            _chips.Orientation = Orientation.Horizontal;
            _chips.Margin = new Thickness(12, 0, 12, 8);
            BuildChips();
            Grid.SetRow(_chips, 2);
            root.Children.Add(_chips);

            // ---- 时间筛选（第二行，窄窗口下也不换行）
            _timeBar = new Grid();
            _timeBar.Margin = new Thickness(12, 0, 8, 8);
            BuildTimeBar();
            Grid.SetRow(_timeBar, 3);
            root.Children.Add(_timeBar);

            // ---- 内容
            _contentGrid = new Grid();
            _scroll = new ScrollViewer();
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Padding = new Thickness(8, 0, 8, 8);
            _scroll.CanContentScroll = false;
            _list = new StackPanel();
            _scroll.Content = _list;
            _scroll.ScrollChanged += delegate
            {
                UpdateSticky();
                // 接近底部时追加下一批行，避免一次性为上千条建元素
                if (_planPos < _plan.Count && !_rendering && _scroll.ExtentHeight > 0
                    && _scroll.VerticalOffset + _scroll.ViewportHeight > _scroll.ExtentHeight - 240)
                {
                    _renderTarget = _rows.Count + PageSize;
                    RenderMore();
                }
            };
            _contentGrid.Children.Add(_scroll);

            // 吸顶的日期标题
            _sticky = new Border();
            _sticky.Background = Theme.PanelSolid;
            _sticky.VerticalAlignment = VerticalAlignment.Top;
            _sticky.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sticky.Margin = new Thickness(8, 0, 8, 0);
            _sticky.Padding = new Thickness(0, 6, 0, 5);
            _sticky.IsHitTestVisible = false;
            _sticky.Visibility = Visibility.Collapsed;
            Grid inner = new Grid();
            _stickyText = Ui.Text("", 10.5, Theme.Txt2);
            _stickyText.FontWeight = FontWeights.SemiBold;
            inner.Children.Add(_stickyText);
            Border stickLine = new Border();
            stickLine.Height = 1;
            stickLine.Background = Theme.Line;
            stickLine.VerticalAlignment = VerticalAlignment.Bottom;
            inner.Children.Add(stickLine);
            _sticky.Child = inner;
            Panel.SetZIndex(_sticky, 5);
            _contentGrid.Children.Add(_sticky);

            _empty = BuildEmpty();
            _empty.Visibility = Visibility.Collapsed;
            _contentGrid.Children.Add(_empty);

            _toast = new Border();
            _toast.Background = Theme.Menu;
            _toast.BorderBrush = Theme.PanelBorder;
            _toast.BorderThickness = new Thickness(1);
            _toast.CornerRadius = new CornerRadius(6);
            _toast.Padding = new Thickness(12, 7, 8, 7);
            _toast.HorizontalAlignment = HorizontalAlignment.Center;
            _toast.VerticalAlignment = VerticalAlignment.Bottom;
            _toast.Margin = new Thickness(0, 0, 0, 10);
            _toast.Effect = Theme.Shadow(16, 0.3);
            _toast.Visibility = Visibility.Collapsed;
            _toast.MaxWidth = CardW - 40;
            StackPanel tsp = new StackPanel();
            tsp.Orientation = Orientation.Horizontal;
            _toastText = Ui.Text("已删除", 11.5, Theme.Txt);
            _toastText.VerticalAlignment = VerticalAlignment.Center;
            // 提示类文案可能较长，限制宽度并允许换行，避免撑出浮层卡片
            _toastText.MaxWidth = CardW - 100;
            _toastText.TextWrapping = TextWrapping.Wrap;
            tsp.Children.Add(_toastText);
            ClickBox undo = new ClickBox();
            undo.Child = Ui.Text("撤销", 11.5, Theme.Acc);
            undo.Margin = new Thickness(12, 0, 4, 0);
            undo.VerticalAlignment = VerticalAlignment.Center;
            undo.Clicked = UndoDelete;
            _toastUndo = undo;
            tsp.Children.Add(undo);
            _toast.Child = tsp;
            _contentGrid.Children.Add(_toast);

            Grid.SetRow(_contentGrid, 4);
            root.Children.Add(_contentGrid);

            // ---- 底部
            Grid foot = new Grid();
            foot.Height = 33;
            foot.Margin = new Thickness(12, 0, 12, 0);
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition footRight = new ColumnDefinition();
            footRight.Width = GridLength.Auto;
            foot.ColumnDefinitions.Add(footRight);
            StackPanel hints = new StackPanel();
            hints.Orientation = Orientation.Horizontal;
            hints.VerticalAlignment = VerticalAlignment.Center;
            hints.Children.Add(Hint("↑", "↓", " 选择"));
            hints.Children.Add(Hint("Enter", null, " 粘贴"));
            hints.Children.Add(Hint("Ctrl", "C", " 仅复制"));
            hints.Children.Add(Hint("Del", null, " 删除"));
            Grid.SetColumn(hints, 0);
            foot.Children.Add(hints);
            _footCount = Ui.Text("—", 10.5, Theme.Txt3);
            _footCount.VerticalAlignment = VerticalAlignment.Center;
            _footCount.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(_footCount, 1);
            foot.Children.Add(_footCount);
            Grid.SetRow(foot, 5);
            root.Children.Add(foot);

            // 顶部分隔线
            Border topLine = new Border();
            topLine.Height = 1;
            topLine.Background = Theme.Line;
            topLine.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRowSpan(topLine, 6);
            root.Children.Add(topLine);

            card.Child = root;
            Content = card;
            Ui.EnableDrag(this, card, 56, _searchBox);

            PreviewKeyDown += WindowKeyDown;
        }

        FrameworkElement Hint(string k1, string k2, string label)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.Margin = new Thickness(0, 0, 10, 0);
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(Ui.Kbd(k1));
            if (!string.IsNullOrEmpty(k2)) sp.Children.Add(Ui.Kbd(k2));
            TextBlock t = Ui.Text(label, 10.5, Theme.Txt3);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(2, 0, 0, 0);
            sp.Children.Add(t);
            return sp;
        }

        const string GearPath = "M10.2,8 A2.2,2.2 0 1 1 5.8,8 A2.2,2.2 0 1 1 10.2,8 M8,1.6 L8,3.3 M8,12.7 L8,14.4 M1.6,8 L3.3,8 M12.7,8 L14.4,8 M3.5,3.5 L4.7,4.7 M11.3,11.3 L12.5,12.5 M12.5,3.5 L11.3,4.7 M4.7,11.3 L3.5,12.5";

        FrameworkElement PauseIcon(Brush b)
        {
            Grid g = new Grid();
            g.Width = 14;
            g.Height = 14;
            Border r1 = new Border();
            r1.Width = 2.6;
            r1.Height = 10;
            r1.CornerRadius = new CornerRadius(1);
            r1.Background = b;
            r1.HorizontalAlignment = HorizontalAlignment.Left;
            r1.Margin = new Thickness(3, 2, 0, 2);
            Border r2 = new Border();
            r2.Width = 2.6;
            r2.Height = 10;
            r2.CornerRadius = new CornerRadius(1);
            r2.Background = b;
            r2.HorizontalAlignment = HorizontalAlignment.Right;
            r2.Margin = new Thickness(0, 2, 3, 2);
            g.Children.Add(r1);
            g.Children.Add(r2);
            return g;
        }

        void ClearButtonVisibility()
        {
            Grid g = _searchBox.Child as Grid;
            if (g == null) return;
            foreach (UIElement el in g.Children)
            {
                ClickBox cb = el as ClickBox;
                if (cb != null && cb.Name == "ClearBtn")
                {
                    cb.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        Border BuildEmpty()
        {
            Border wrap = new Border();
            StackPanel sp = new StackPanel();
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Margin = new Thickness(24, 0, 24, 30);

            Border icoBox = new Border();
            icoBox.Width = 44;
            icoBox.Height = 44;
            icoBox.CornerRadius = new CornerRadius(12);
            icoBox.Background = Theme.Card;
            icoBox.BorderBrush = Theme.CardBorder;
            icoBox.BorderThickness = new Thickness(1);
            icoBox.HorizontalAlignment = HorizontalAlignment.Center;
            icoBox.Child = Ui.Icon("M2.6,11 A6.5,6.5 0 1 1 15.6,11 A6.5,6.5 0 1 1 2.6,11 M16,16 L20.5,20.5", 22, Theme.Txt3, 1.5);
            sp.Children.Add(icoBox);

            TextBlock title = Ui.Text("还没有记录", 13, Theme.Txt);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(title);

            TextBlock sub = Ui.Wrap("复制一段文字或一张图片，它会出现在这里", 11.5, Theme.Txt3);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.TextAlignment = TextAlignment.Center;
            sub.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(sub);

            _emptyToday = Ui.Btn("回到今天", false, delegate { SetTime("today", DateTime.Today); });
            _emptyToday.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyToday.Margin = new Thickness(0, 12, 0, 0);
            _emptyToday.Visibility = Visibility.Collapsed;
            sp.Children.Add(_emptyToday);

            wrap.Child = sp;
            return wrap;
        }

        void BuildChips()
        {
            _chips.Children.Clear();
            ChipBox all = new ChipBox("全部");
            all.SetCount(_store.Items.Count);
            all.Active = _filter == "all";
            all.Clicked = delegate { SetFilter("all"); };
            _chips.Children.Add(all);

            ChipBox text = new ChipBox("文本");
            text.Active = _filter == "text";
            text.Clicked = delegate { SetFilter("text"); };
            _chips.Children.Add(text);

            ChipBox image = new ChipBox("图片");
            image.Active = _filter == "image";
            image.Clicked = delegate { SetFilter("image"); };
            _chips.Children.Add(image);

            ChipBox fav = new ChipBox("收藏");
            fav.Active = _filter == "fav";
            fav.Clicked = delegate { SetFilter("fav"); };
            _chips.Children.Add(fav);
        }

        void SetFilter(string f)
        {
            _filter = f;
            BuildChips();
            Rebuild();
        }

        // ------------------------------------------------------------------ 时间筛选
        void BuildTimeBar()
        {
            _timeBar.Children.Clear();
            _timeBar.ColumnDefinitions.Clear();
            _timeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _timeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;

            TextBlock lbl = Ui.Text("时间", 10.5, Theme.Txt3);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.Margin = new Thickness(0, 0, 6, 0);
            left.Children.Add(lbl);

            left.Children.Add(TimeChip("all", "不限时间"));
            left.Children.Add(TimeChip("today", "今天"));
            left.Children.Add(TimeChip("yesterday", "昨天"));
            left.Children.Add(TimeChip("week", "近 7 天"));
            ChipBox custom = TimeChip("custom", CustomLabel());
            custom.ToolTip = "自定义日期（Ctrl+D）";
            left.Children.Add(custom);
            Grid.SetColumn(left, 0);
            _timeBar.Children.Add(left);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;

            ClickBox prev = Ui.IconBtn(Ui.Icon("M9,2 L4,7 L9,12", 12, Theme.Txt3, 1.4), "上一天（← / PageUp）", delegate { ShiftDay(-1); });
            prev.Width = 20;
            prev.Height = 24;
            right.Children.Add(prev);
            ClickBox next = Ui.IconBtn(Ui.Icon("M5,2 L10,7 L5,12", 12, Theme.Txt3, 1.4), "下一天（→ / PageDown）", delegate { ShiftDay(1); });
            next.Width = 20;
            next.Height = 24;
            right.Children.Add(next);
            Grid.SetColumn(right, 1);
            _timeBar.Children.Add(right);
        }

        ChipBox TimeChip(string mode, string label)
        {
            ChipBox c = new ChipBox(label);
            c.Height = 24;
            c.Padding = new Thickness(10, 0, 10, 0);
            c.Margin = new Thickness(0, 0, 6, 0);
            c.Active = _timeMode == mode;
            c.Clicked = delegate
            {
                if (mode == "custom") OpenDatePicker();
                else SetTime(mode, DateTime.Today);
            };
            return c;
        }

        string CustomLabel()
        {
            if (_timeMode != "custom") return "自定义日期";
            DateTime d = _customDate;
            if (d.Year == DateTime.Today.Year) return (d.Month) + " 月 " + d.Day + " 日";
            return d.Year + "-" + d.Month.ToString("00") + "-" + d.Day.ToString("00");
        }

        void SetTime(string mode, DateTime date)
        {
            _timeMode = mode;
            if (mode == "custom") _customDate = date.Date;

            // 选中态先跟手，列表稍后刷新
            BuildTimeBar();

            if (_dayPending)
            {
                // 还在等去抖：继续往后推，不重建
                _dayMerged++;
                RestartDayTimer();
                return;
            }

            if ((DateTime.UtcNow - _dayLastApply).TotalMilliseconds < 90)
            {
                // 刚刚才刷过一次，说明用户在连按：合并成一次
                _dayPending = true;
                _daySw = System.Diagnostics.Stopwatch.StartNew();
                _dayMerged = 1;
                RestartDayTimer();
                return;
            }

            // 单次按下：立即重建，不做延迟
            _dayPending = true;
            _daySw = System.Diagnostics.Stopwatch.StartNew();
            ApplyTimeChange();
        }

        void RestartDayTimer()
        {
            if (_dayTimer == null)
            {
                _dayTimer = new DispatcherTimer();
                _dayTimer.Interval = TimeSpan.FromMilliseconds(90);
                _dayTimer.Tick += delegate { _dayTimer.Stop(); ApplyTimeChange(); };
            }
            _dayTimer.Stop();
            _dayTimer.Start();
        }

        /// <summary>立即应用待处理的日期切换（连按去抖窗口内尚未刷新的那次）。</summary>
        public void FlushTimeChange()
        {
            if (_dayTimer != null) _dayTimer.Stop();
            if (_dayPending) ApplyTimeChange();
        }

        void ApplyTimeChange()
        {
            if (!_dayPending) return;
            _dayPending = false;
            if (_dayMerged > 0)
            {
                Perf.Add("date.merged", _dayMerged);
                _dayMerged = 0;
            }
            Rebuild();
            _scroll.ScrollToTop();
            if (_search != null) { _search.Focus(); Keyboard.Focus(_search); }
            _dayLastApply = DateTime.UtcNow;
            if (_daySw != null)
            {
                Perf.Add("date.switch", _daySw.Elapsed.TotalMilliseconds);
                _daySw = null;
            }
        }

        /// <summary>上一天 / 下一天：不限时间时以今天为锚点。</summary>
        void ShiftDay(int delta)
        {
            DateTime anchor = DateTime.Today;
            if (_timeMode == "custom") anchor = _customDate;
            else if (_timeMode == "yesterday") anchor = DateTime.Today.AddDays(-1);
            else if (_timeMode == "week") anchor = DateTime.Today;

            DateTime target = anchor.AddDays(delta);
            if (target > DateTime.Today) target = DateTime.Today;

            if (target == DateTime.Today) SetTime("today", target);
            else if (target == DateTime.Today.AddDays(-1)) SetTime("yesterday", target);
            else SetTime("custom", target);
        }

        // ------------------------------------------------------------------ 日期选择器
        void OpenDatePicker()
        {
            EnsureDatePopup();
            _calMonth = new DateTime(_customDate.Year, _customDate.Month, 1);
            _dateInput.Text = _customDate.ToString("yyyy-MM-dd");
            _dateInputBox.BorderBrush = Theme.FieldBorder;
            BuildCalendar();
            _datePopup.PlacementTarget = _timeBar;
            _datePopup.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(delegate { _dateInput.Focus(); _dateInput.SelectAll(); }), DispatcherPriority.Input);
        }

        void EnsureDatePopup()
        {
            if (_datePopup != null) return;

            Border panel = new Border();
            panel.Background = Theme.Menu;
            panel.BorderBrush = Theme.PanelBorder;
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(8);
            panel.Padding = new Thickness(10);
            panel.Effect = Theme.Shadow(20, 0.35);

            StackPanel sp = new StackPanel();
            sp.Width = 236;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ClickBox mprev = Ui.IconBtn(Ui.Icon("M9,2 L4,7 L9,12", 12, Theme.Txt2, 1.4), "上个月", delegate { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); });
            mprev.Width = 22; mprev.Height = 22;
            head.Children.Add(mprev);
            _calTitle = Ui.Text("", 12, Theme.Txt);
            _calTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _calTitle.VerticalAlignment = VerticalAlignment.Center;
            _calTitle.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(_calTitle, 1);
            head.Children.Add(_calTitle);
            ClickBox mnext = Ui.IconBtn(Ui.Icon("M5,2 L10,7 L5,12", 12, Theme.Txt2, 1.4), "下个月", delegate { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); });
            mnext.Width = 22; mnext.Height = 22;
            Grid.SetColumn(mnext, 2);
            head.Children.Add(mnext);
            sp.Children.Add(head);

            Grid week = new Grid();
            string[] wd = new string[] { "一", "二", "三", "四", "五", "六", "日" };
            for (int i = 0; i < 7; i++)
            {
                week.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                TextBlock t = Ui.Text(wd[i], 10, Theme.Txt3);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 8, 0, 4);
                Grid.SetColumn(t, i);
                week.Children.Add(t);
            }
            sp.Children.Add(week);

            _calDays = new Grid();
            for (int i = 0; i < 7; i++)
            {
                _calDays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            for (int r = 0; r < 6; r++)
            {
                _calDays.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            }
            sp.Children.Add(_calDays);

            Border line = new Border();
            line.Height = 1;
            line.Background = Theme.Line;
            line.Margin = new Thickness(0, 8, 0, 8);
            sp.Children.Add(line);

            Grid inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dateInputBox = new Border();
            _dateInputBox.Height = 30;
            _dateInputBox.CornerRadius = new CornerRadius(5);
            _dateInputBox.Background = Theme.Field;
            _dateInputBox.BorderBrush = Theme.FieldBorder;
            _dateInputBox.BorderThickness = new Thickness(1);
            _dateInputBox.Padding = new Thickness(8, 0, 6, 0);
            _dateInput = new TextBox();
            _dateInput.BorderThickness = new Thickness(0);
            _dateInput.Background = Brushes.Transparent;
            _dateInput.Foreground = Theme.Txt;
            _dateInput.CaretBrush = Theme.Acc;
            _dateInput.FontSize = 12;
            _dateInput.FontFamily = Theme.Font;
            _dateInput.VerticalContentAlignment = VerticalAlignment.Center;
            _dateInput.Padding = new Thickness(0);
            _dateInput.ToolTip = "可直接输入：2026-11-09 / 11-09 / 20261109";
            _dateInput.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { ApplyTypedDate(); e.Handled = true; }
                else if (e.Key == Key.Escape) { _datePopup.IsOpen = false; e.Handled = true; }
            };
            _dateInputBox.Child = _dateInput;
            Grid.SetColumn(_dateInputBox, 0);
            inputRow.Children.Add(_dateInputBox);

            ClickBox ok = Ui.Btn("查看", true, delegate { ApplyTypedDate(); });
            ok.Height = 30;
            ok.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(ok, 1);
            inputRow.Children.Add(ok);
            sp.Children.Add(inputRow);

            TextBlock tip = Ui.Text("回车确认 · Ctrl+D 打开此面板", 10, Theme.Txt3);
            tip.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(tip);

            panel.Child = sp;

            _datePopup = new Popup();
            _datePopup.AllowsTransparency = true;
            _datePopup.StaysOpen = false;
            _datePopup.Placement = PlacementMode.Bottom;
            _datePopup.VerticalOffset = 4;
            _datePopup.Child = panel;
        }

        void BuildCalendar()
        {
            if (_calDays == null) return;
            _calTitle.Text = _calMonth.Year + " 年 " + _calMonth.Month + " 月";
            _calDays.Children.Clear();

            int offset = ((int)_calMonth.DayOfWeek + 6) % 7;   // 周一为一周起始
            DateTime first = _calMonth.AddDays(-offset);
            DateTime today = DateTime.Today;
            DateTime earliest = EarliestDay();

            for (int r = 0; r < 6; r++)
            {
                for (int c = 0; c < 7; c++)
                {
                    DateTime d = first.AddDays(r * 7 + c);
                    bool outMonth = d.Month != _calMonth.Month;
                    bool future = d > today;
                    bool tooOld = earliest != DateTime.MinValue && d < earliest;

                    ClickBox cell = new ClickBox();
                    cell.CornerRadius = new CornerRadius(4);
                    cell.Margin = new Thickness(1);
                    cell.HoverBg = Theme.Hover;
                    bool isSel = _timeMode == "custom" && d == _customDate;
                    bool isToday = d == today;
                    if (isSel) cell.Background = Theme.Acc;
                    TextBlock t = Ui.Text(d.Day.ToString(), 11,
                        isSel ? Theme.AccFg : (future ? Theme.Txt3 : (outMonth ? Theme.Txt3 : Theme.Txt)));
                    if (isToday && !isSel) t.FontWeight = FontWeights.SemiBold;
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    t.VerticalAlignment = VerticalAlignment.Center;
                    cell.Child = t;
                    if (!future)
                    {
                        DateTime captured = d;
                        cell.Clicked = delegate
                        {
                            _datePopup.IsOpen = false;
                            SetTime("custom", captured);
                        };
                    }
                    if (tooOld) cell.ToolTip = "早于最早记录（" + earliest.ToString("M 月 d 日") + "）";
                    Grid.SetColumn(cell, c);
                    Grid.SetRow(cell, r);
                    _calDays.Children.Add(cell);
                }
            }
        }

        void ApplyTypedDate()
        {
            DateTime d;
            if (!TryParseDate(_dateInput.Text, out d))
            {
                _dateInputBox.BorderBrush = Theme.DangerBrush;
                return;
            }
            if (d > DateTime.Today) d = DateTime.Today;
            _datePopup.IsOpen = false;
            SetTime("custom", d);
        }

        static bool TryParseDate(string raw, out DateTime result)
        {
            result = DateTime.MinValue;
            if (raw == null) return false;
            string s = raw.Trim();
            if (s.Length == 0) return false;
            s = s.Replace("年", "-").Replace("月", "-").Replace("日", "").Replace("/", "-").Replace(".", "-");
            string[] parts = s.Split(new char[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            int y = DateTime.Today.Year, m, d;
            try
            {
                if (parts.Length == 1)
                {
                    if (parts[0].Length == 8)
                    {
                        y = int.Parse(parts[0].Substring(0, 4));
                        m = int.Parse(parts[0].Substring(4, 2));
                        d = int.Parse(parts[0].Substring(6, 2));
                    }
                    else return false;
                }
                else if (parts.Length == 2)
                {
                    m = int.Parse(parts[0]);
                    d = int.Parse(parts[1]);
                }
                else
                {
                    y = int.Parse(parts[0]);
                    m = int.Parse(parts[1]);
                    d = int.Parse(parts[2]);
                }
                result = new DateTime(y, m, d);
                return true;
            }
            catch { return false; }
        }

        /// <summary>历史里最早一条记录的日期（用于"最早可查"提示）。</summary>
        DateTime EarliestDay()
        {
            DateTime min = DateTime.MinValue;
            foreach (ClipItem it in _store.Items)
            {
                if (min == DateTime.MinValue || it.Created.Date < min) min = it.Created.Date;
            }
            return min;
        }

        // ------------------------------------------------------------------ 显示 / 隐藏
        public void Toggle()
        {
            if (IsVisible) HideOverlay();
            else ShowOverlay();
        }

        public void RefreshFromStore()
        {
            BuildChips();
            RefreshBanner();
            UpdateSyncDot();
            Rebuild();
        }

        void UpdateSyncDot()
        {
            if (_syncDot == null) return;
            SyncService sync = AppHost.Current == null ? null : AppHost.Current.Sync;
            if (sync == null || _cfg.SyncMode != "lan")
            {
                _syncDot.Visibility = Visibility.Collapsed;
                return;
            }
            int level = sync.StatusLevel;
            _syncDot.Visibility = Visibility.Visible;
            if (level == 1) _syncDot.Background = Theme.Hex(Theme.IsLight ? "#2FA866" : "#4CD18A");
            else if (level == 2) _syncDot.Background = Theme.Hex(Theme.IsLight ? "#C9760A" : "#E2AB34");
            else if (level == 3) _syncDot.Background = Theme.Hex(Theme.IsLight ? "#C0362C" : "#F2716B");
            else _syncDot.Background = Theme.Txt3;
            _syncDot.ToolTip = "同步：" + sync.StatusText;
        }

        public void ShowOverlay()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            new WindowInteropHelper(this).EnsureHandle();
            _target = Native.GetForegroundWindow();
            if (_target == IntPtr.Zero || IsOwnWindow(_target)) _target = IntPtr.Zero;

            _search.Text = "";
            _filter = "all";
            _timeMode = "all";
            _customDate = DateTime.Today;
            _delItem = null;
            HideToast();
            BuildChips();
            BuildTimeBar();
            RefreshBanner();
            UpdateSyncDot();
            Rebuild();
            PositionNearCursor();
            double prep = sw.Elapsed.TotalMilliseconds;
            Show();
            Activate();
            double shown = sw.Elapsed.TotalMilliseconds;
            UpdateLayout();
            _search.Focus();
            Keyboard.Focus(_search);
            Perf.Add("overlay.show.prep", prep);
            Perf.Add("overlay.show.show", shown - prep);
            Perf.Add("overlay.show.layout", sw.Elapsed.TotalMilliseconds - shown);
            Perf.Add("overlay.show", sw.Elapsed.TotalMilliseconds);
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Perf.Add("overlay.firstframe", sw.Elapsed.TotalMilliseconds);
            }), DispatcherPriority.Render);
        }

        static bool IsOwnWindow(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public void HideOverlay()
        {
            if (!IsVisible) return;
            _clickTimer.Stop();
            _pendingClick = null;
            Hide();
        }

        void RefreshBanner()
        {
            _banner.Visibility = _cfg.Paused ? Visibility.Visible : Visibility.Collapsed;
        }

        void PositionNearCursor()
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            double scale = 1.0;
            try
            {
                HwndSource src = HwndSource.FromHwnd(hwnd);
                if (src != null && src.CompositionTarget != null)
                {
                    scale = src.CompositionTarget.TransformFromDevice.M11;
                }
            }
            catch { }

            System.Drawing.Point cp = WinForms.Cursor.Position;
            System.Drawing.Rectangle wa = WinForms.Screen.FromPoint(cp).WorkingArea;

            double cx = cp.X * scale;
            double cy = cp.Y * scale;
            double left = wa.Left * scale;
            double top = wa.Top * scale;
            double right = wa.Right * scale;
            double bottom = wa.Bottom * scale;

            double x = cx - CardW / 2;
            double y = cy + 16;
            if (y + CardH > bottom - 8) y = cy - CardH - 16;
            if (y < top + 8) y = top + 8;
            if (x < left + 8) x = left + 8;
            if (x + CardW > right - 8) x = right - CardW - 8;

            Left = x;
            Top = y;
        }

        // ------------------------------------------------------------------ 列表
        void Rebuild()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            string keepId = null;
            if (_sel >= 0 && _sel < _rows.Count) keepId = _rows[_sel].Item.Id;

            _list.Children.Clear();
            _rows.Clear();
            _groupHeaders.Clear();
            _groupLabels.Clear();
            _plan.Clear();
            _planPos = 0;
            _renderTarget = PageSize;
            _moreHint = null;
            _view = Filtered();

            if (_view.Count == 0)
            {
                _empty.Visibility = Visibility.Visible;
                UpdateEmptyText();
                _footCount.Text = "0 条";
                _sel = -1;
                _sticky.Visibility = Visibility.Collapsed;
                Perf.Add("overlay.build", sw.Elapsed.TotalMilliseconds);
                return;
            }
            _empty.Visibility = Visibility.Collapsed;

            // 先把要显示的内容编成渲染计划（只有标题和记录两种元素），再按需分批实例化
            bool hasPinned = false;
            foreach (ClipItem it in _view)
            {
                if (it.Pinned) { hasPinned = true; break; }
            }
            if (hasPinned)
            {
                _plan.Add("已置顶");
                foreach (ClipItem it in _view)
                {
                    if (it.Pinned) _plan.Add(it);
                }
            }
            // 按天分组（本地时间），组内保持原有时间倒序
            DateTime currentDay = DateTime.MinValue;
            foreach (ClipItem it in _view)
            {
                if (it.Pinned) continue;
                DateTime day = it.Created.Date;
                if (day != currentDay)
                {
                    currentDay = day;
                    _plan.Add(DayLabel(day));
                }
                _plan.Add(it);
            }

            RenderMore();

            _footCount.Text = _view.Count == _store.Items.Count
                ? _view.Count + " 条"
                : _view.Count + " / " + _store.Items.Count + " 条";

            int target = 0;
            if (keepId != null)
            {
                int i = 0;
                foreach (Row r in _rows)
                {
                    if (r.Item.Id == keepId) { target = i; break; }
                    i++;
                }
            }
            Select(target, false);
            Perf.Add("rows", _rows.Count);
            Perf.Add("overlay.build", sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>按渲染计划追加元素，只为需要的行创建视觉树。</summary>
        void RenderMore()
        {
            if (_rendering) return;
            _rendering = true;
            try
            {
                while (_planPos < _plan.Count && _rows.Count < _renderTarget)
                {
                    object o = _plan[_planPos++];
                    string label = o as string;
                    if (label != null)
                    {
                        _list.Children.Add(GroupHeader(label));
                        continue;
                    }
                    AddRow((ClipItem)o, _rows.Count);
                }

                if (_moreHint == null)
                {
                    _moreHint = Ui.Text("", 10.5, Theme.Txt3);
                    _moreHint.HorizontalAlignment = HorizontalAlignment.Center;
                    _moreHint.Margin = new Thickness(0, 8, 0, 4);
                }
                else
                {
                    _list.Children.Remove(_moreHint);
                }

                int remaining = 0;
                for (int i = _planPos; i < _plan.Count; i++)
                {
                    if (!(_plan[i] is string)) remaining++;
                }
                if (remaining > 0)
                {
                    _moreHint.Text = "还有 " + remaining + " 条 · 继续向下滚动";
                    _list.Children.Add(_moreHint);
                }
            }
            finally { _rendering = false; }
        }

        List<ClipItem> Filtered()
        {
            using (Perf.Span("date.filter"))
            {
                return FilteredCore();
            }
        }

        List<ClipItem> FilteredCore()
        {
            IEnumerable<ClipItem> q = _store.Items;
            if (_filter == "text") q = q.Where(i => !i.IsImage);
            else if (_filter == "image") q = q.Where(i => i.IsImage);
            else if (_filter == "fav") q = q.Where(i => i.Fav || i.Pinned);

            q = ApplyTimeFilter(q);

            string kw = _search.Text == null ? "" : _search.Text.Trim();
            if (kw.Length > 0)
            {
                q = q.Where(i =>
                {
                    if (i.IsImage) return ("图片 " + i.Fmt).IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (i.Text == null) return false;
                    if (i.Sensitive && _cfg.MaskSensitive) return false;
                    return i.Text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }
            return q.ToList();
        }

        /// <summary>时间条件：按本地日期比较，跨零点以当前时间的"今天"为准。</summary>
        IEnumerable<ClipItem> ApplyTimeFilter(IEnumerable<ClipItem> q)
        {
            DateTime today = DateTime.Today;
            if (_timeMode == "today") return q.Where(i => i.Created.Date == today);
            if (_timeMode == "yesterday")
            {
                DateTime y = today.AddDays(-1);
                return q.Where(i => i.Created.Date == y);
            }
            if (_timeMode == "week")
            {
                DateTime from = today.AddDays(-6);
                return q.Where(i => i.Created.Date >= from && i.Created.Date <= today);
            }
            if (_timeMode == "custom")
            {
                DateTime d = _customDate.Date;
                return q.Where(i => i.Created.Date == d);
            }
            return q;
        }

        static string DayLabel(DateTime day)
        {
            DateTime today = DateTime.Today;
            if (day == today) return "今天";
            if (day == today.AddDays(-1)) return "昨天";
            if (day.Year == today.Year) return day.Month + " 月 " + day.Day + " 日";
            return day.Year + " 年 " + day.Month + " 月 " + day.Day + " 日";
        }

        /// <summary>日期分组标题（同时登记给吸顶条用）。</summary>
        Border GroupHeader(string label)
        {
            StackPanel g = new StackPanel();
            g.Margin = new Thickness(8, 8, 8, 2);
            TextBlock t = Ui.Text(label, 10.5, Theme.Txt2);
            t.FontWeight = FontWeights.SemiBold;
            g.Children.Add(t);
            Border line = new Border();
            line.Height = 1;
            line.Background = Theme.Line;
            line.Margin = new Thickness(0, 3, 0, 0);
            g.Children.Add(line);
            Border wrap = new Border();
            wrap.Child = g;
            _groupHeaders.Add(wrap);
            _groupLabels.Add(label);
            return wrap;
        }

        /// <summary>滚动时把当前分组标题吸到顶部。</summary>
        void UpdateSticky()
        {
            if (_sticky == null) return;
            if (_groupHeaders.Count == 0 || _empty.Visibility == Visibility.Visible)
            {
                _sticky.Visibility = Visibility.Collapsed;
                return;
            }
            int active = -1;
            for (int i = 0; i < _groupHeaders.Count; i++)
            {
                Border h = _groupHeaders[i];
                if (h == null) continue;
                double y;
                try
                {
                    y = h.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
                }
                catch { continue; }
                if (y < -2) active = i;
                else break;
            }
            if (active < 0)
            {
                _sticky.Visibility = Visibility.Collapsed;
                return;
            }
            _stickyText.Text = _groupLabels[active];
            _sticky.Visibility = Visibility.Visible;
        }

        void UpdateEmptyText()
        {
            StackPanel sp = _empty.Child as StackPanel;
            if (sp == null || sp.Children.Count < 3) return;
            TextBlock title = sp.Children[1] as TextBlock;
            TextBlock sub = sp.Children[2] as TextBlock;
            bool timeActive = _timeMode != "all";
            bool keyword = _search.Text != null && _search.Text.Trim().Length > 0;

            if (_emptyToday != null) _emptyToday.Visibility = timeActive ? Visibility.Visible : Visibility.Collapsed;

            if (timeActive)
            {
                DateTime earliest = EarliestDay();
                if (_timeMode == "today") title.Text = "今天还没有记录";
                else if (_timeMode == "yesterday") title.Text = "昨天没有记录";
                else if (_timeMode == "week") title.Text = "近 7 天没有记录";
                else title.Text = "这一天没有记录";

                if (_timeMode == "custom" && earliest != DateTime.MinValue && _customDate.Date < earliest)
                {
                    sub.Text = "超出保留天数，最早可查 " + earliest.ToString("M 月 d 日");
                }
                else if (keyword)
                {
                    sub.Text = "这段时间里没有包含「" + _search.Text.Trim() + "」的内容";
                }
                else
                {
                    sub.Text = "换一天看看，或者点下面的按钮回到今天";
                }
                return;
            }

            if (keyword)
            {
                title.Text = "没有找到包含「" + _search.Text.Trim() + "」的记录";
                sub.Text = "试试更短的关键词，或切换到「全部」";
            }
            else if (_filter != "all")
            {
                title.Text = "这个分类下还没有内容";
                sub.Text = "换个筛选，或者先复制点什么";
            }
            else
            {
                title.Text = "还没有记录";
                sub.Text = "复制一段文字或一张图片，它会出现在这里";
            }
        }

        Border Separator(string label)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(8, 8, 8, 4);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock t = Ui.Text(label, 10.5, Theme.Txt3);
            t.Margin = new Thickness(0, 0, 8, 0);
            g.Children.Add(t);
            Border line = new Border();
            line.Height = 1;
            line.Background = Theme.Line;
            line.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(line, 1);
            g.Children.Add(line);
            Border wrap = new Border();
            wrap.Child = g;
            return wrap;
        }

        void AddRow(ClipItem it, int index)
        {
            Row row = new Row();
            row.Item = it;

            Border root = new Border();
            root.CornerRadius = new CornerRadius(6);
            root.Padding = new Thickness(10, 8, 10, 8);
            root.Margin = new Thickness(0, 1, 0, 1);
            root.Background = Brushes.Transparent;
            row.Root = root;

            Grid outer = new Grid();
            Border bar = new Border();
            bar.Width = 2.5;
            bar.CornerRadius = new CornerRadius(2);
            bar.Background = Theme.Acc;
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Margin = new Thickness(-8, 6, 0, 6);
            bar.Visibility = Visibility.Collapsed;
            row.Bar = bar;
            outer.Children.Add(bar);

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Margin = new Thickness(0, 0, 40, 0);

            if (it.IsImage)
            {
                Border thumbBox = new Border();
                thumbBox.Width = 56;
                thumbBox.Height = 56;
                thumbBox.CornerRadius = new CornerRadius(6);
                thumbBox.Background = Theme.Card;
                thumbBox.BorderBrush = Theme.Line;
                thumbBox.BorderThickness = new Thickness(1);
                thumbBox.ClipToBounds = true;
                string tp = _store.ThumbPath(it);
                BitmapSource bmp;
                // 内存缓存复用：切日期/重开浮层时同一张缩略图不再重复解码
                using (Perf.Span("overlay.thumb")) { bmp = Store.LoadThumbCached(tp); }
                if (bmp != null)
                {
                    Image img = new Image();
                    img.Source = bmp;
                    img.Stretch = Stretch.UniformToFill;
                    thumbBox.Child = img;
                }
                else
                {
                    thumbBox.Child = Ui.Icon("M2.4,3.2 L13.6,3.2 L13.6,12.8 L2.4,12.8 Z M2.4,10.2 L6,6.8 L9,9.6 L11,7.8 L13.6,10.4", 22, Theme.Txt3, 1.2);
                }
                Grid.SetColumn(thumbBox, 0);
                content.Children.Add(thumbBox);
            }
            else
            {
                Border iconBox = new Border();
                iconBox.Width = 28;
                iconBox.Height = 28;
                iconBox.CornerRadius = new CornerRadius(6);
                iconBox.Background = Theme.Card;
                iconBox.BorderBrush = Theme.CardBorder;
                iconBox.BorderThickness = new Thickness(1);
                iconBox.VerticalAlignment = VerticalAlignment.Top;
                iconBox.Margin = new Thickness(0, 1, 0, 0);
                string kind = TextKind(it.Text);
                iconBox.Child = Ui.Icon(IconFor(kind), 15, IconBrush(kind), 1.25);
                Grid.SetColumn(iconBox, 0);
                content.Children.Add(iconBox);
            }

            StackPanel main = new StackPanel();
            main.Margin = new Thickness(10, 0, 0, 0);
            main.VerticalAlignment = VerticalAlignment.Center;

            if (it.IsImage)
            {
                string nameText = "图片 · " + it.W + " × " + it.H + " · " + TextUtil.SizeText(it.Bytes);
                if (!string.IsNullOrEmpty(it.SourceDevice)) nameText += " · 来自 " + it.SourceDevice;
                TextBlock name = Ui.Text(nameText, 12.5, Theme.Txt);
                name.Margin = new Thickness(0, 0, 0, 4);
                main.Children.Add(name);

                // 远端图片只同步了信息，没有原图：点一下才跨设备拉取并解密
                if (!_store.HasLocalImage(it))
                {
                    ClickBox get = new ClickBox();
                    get.Margin = new Thickness(0, 0, 0, 3);
                    get.HorizontalAlignment = HorizontalAlignment.Left;
                    StackPanel gs = new StackPanel();
                    gs.Orientation = Orientation.Horizontal;
                    FrameworkElement gi = Ui.Icon("M8,2.4 L8,9.2 M5.2,6.6 L8,9.4 L10.8,6.6 M2.6,11.4 L13.4,11.4", 12, Theme.Acc, 1.3);
                    gi.VerticalAlignment = VerticalAlignment.Center;
                    gi.Margin = new Thickness(0, 0, 4, 0);
                    gs.Children.Add(gi);
                    TextBlock gt = Ui.Text("图片未同步 · 点击获取", 11, Theme.Acc);
                    gt.VerticalAlignment = VerticalAlignment.Center;
                    gs.Children.Add(gt);
                    get.Child = gs;
                    get.Clicked = delegate { FetchRemoteImage(it); };
                    main.Children.Add(get);
                }
            }
            else
            {
                TextBlock preview = PreviewBlock(it);
                preview.MaxHeight = 38;
                preview.Margin = new Thickness(0, 0, 0, 4);
                main.Children.Add(preview);
            }

            StackPanel meta = new StackPanel();
            meta.Orientation = Orientation.Horizontal;
            Border dot = new Border();
            dot.Width = 6;
            dot.Height = 6;
            dot.CornerRadius = new CornerRadius(2);
            dot.Background = Theme.AppColorBrush(it.AppColor);
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Margin = new Thickness(0, 0, 4, 0);
            meta.Children.Add(dot);

            TextBlock app = Ui.Text(it.App, 10.5, Theme.Txt3);
            app.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(app);

            TextBlock time = Ui.Text("· " + TextUtil.RelativeTime(it.Created), 10.5, Theme.Txt3);
            time.VerticalAlignment = VerticalAlignment.Center;
            time.Margin = new Thickness(6, 0, 0, 0);
            meta.Children.Add(time);

            if (_cfg.ShowCount && it.Count > 1)
            {
                TextBlock cnt = Ui.Text("· 已复制 " + it.Count + " 次", 10.5, Theme.Txt3);
                cnt.VerticalAlignment = VerticalAlignment.Center;
                cnt.Margin = new Thickness(6, 0, 0, 0);
                meta.Children.Add(cnt);
            }

            if (it.Sensitive && !string.IsNullOrEmpty(it.SensitiveNote))
            {
                Border badge = new Border();
                badge.CornerRadius = new CornerRadius(3);
                badge.Background = Theme.Argba(0.14, "#F0B429");
                badge.Padding = new Thickness(5, 0, 5, 0);
                badge.Height = 15;
                badge.Margin = new Thickness(6, 0, 0, 0);
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Child = Ui.Text(it.SensitiveNote, 9.5, Theme.IsLight ? Theme.Hex("#9A6400") : Theme.Hex("#E2AB34"));
                meta.Children.Add(badge);
            }

            main.Children.Add(meta);
            Grid.SetColumn(main, 1);
            content.Children.Add(main);
            outer.Children.Add(content);

            // 悬停操作
            StackPanel acts = new StackPanel();
            acts.Orientation = Orientation.Horizontal;

            Border actBg = new Border();
            actBg.Background = Theme.PanelSolid;
            actBg.CornerRadius = new CornerRadius(5);
            actBg.Padding = new Thickness(2);
            // 阴影要单独渲染一层，第一次悬停时再加，避免每行都常驻一个特效
            actBg.HorizontalAlignment = HorizontalAlignment.Right;
            actBg.VerticalAlignment = VerticalAlignment.Top;
            actBg.Margin = new Thickness(0, 2, 2, 0);
            actBg.Opacity = 0;
            actBg.IsHitTestVisible = false;
            row.ActionBox = actBg;
            actBg.Child = acts;
            row.Actions = acts;

            outer.Children.Add(actBg);

            root.Child = outer;
            _list.Children.Add(root);
            _rows.Add(row);

            Row me = row;
            int myIndex = index;
            root.MouseEnter += delegate
            {
                // 悬停按钮只在第一次悬停时创建，减少每一行的视觉元素
                if (acts.Children.Count == 0) BuildRowActions(me);
                if (actBg.Effect == null) actBg.Effect = Theme.Shadow(5, 0.22);
                actBg.Opacity = 1;
                actBg.IsHitTestVisible = true;
                if (_sel != myIndex) root.Background = Theme.Hover;
            };
            root.MouseLeave += delegate
            {
                if (_sel != myIndex)
                {
                    actBg.Opacity = 0;
                    actBg.IsHitTestVisible = false;
                }
                if (_sel != myIndex) root.Background = Brushes.Transparent;
            };
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount >= 2)
                {
                    _clickTimer.Stop();
                    _pendingClick = null;
                    Activate(it, true);
                    e.Handled = true;
                    return;
                }
                Select(myIndex, false);
                _pendingClick = it;
                _clickTimer.Stop();
                _clickTimer.Start();
                e.Handled = true;
            };
            root.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowRowMenu(it, e.GetPosition(this));
            };
        }

        /// <summary>按需创建一行右侧的悬停按钮（置顶 / 收藏 / 看大图 / 删除）。</summary>
        void BuildRowActions(Row row)
        {
            ClipItem it = row.Item;
            StackPanel acts = row.Actions;

            ClickBox pin = MiniBtn("M9.6,2 L14,6.4 L12,6.9 L9.4,9.5 L9,12.1 L3.9,7 L7,7 L9.6,4.4 Z", it.Pinned, "置顶",
                delegate { _store.TouchPinned(it, !it.Pinned); Rebuild(); });
            acts.Children.Add(pin);

            ClickBox fav = MiniBtn("M8,2.1 L9.7,5.6 L13.5,6.1 L10.7,8.8 L11.4,12.6 L8,10.8 L4.6,12.6 L5.3,8.8 L2.5,6.1 L6.3,5.6 Z", it.Fav, "收藏",
                delegate { _store.TouchFav(it, !it.Fav); Rebuild(); });
            acts.Children.Add(fav);

            if (it.IsImage)
            {
                ClickBox eye = MiniBtn("M1.8,8 C4.4,4 8,4 8,4 C8,4 11.6,4 14.2,8 C11.6,12 8,12 8,12 C8,12 4.4,12 1.8,8 Z M9.7,8 A1.7,1.7 0 1 1 6.3,8 A1.7,1.7 0 1 1 9.7,8", false, "查看大图",
                    delegate { HideOverlay(); PreviewWindow.Open(it, _store, _cfg, OnPreviewAct); });
                acts.Children.Add(eye);
            }

            ClickBox trash = MiniBtn("M3.2,4.4 L12.8,4.4 M6.4,4.4 L6.4,2.8 L9.6,2.8 L9.6,4.4 M4.8,4.4 L5.4,13.4 L10.6,13.4 L11.2,4.4", false, "删除",
                delegate { RemoveItem(it); });
            acts.Children.Add(trash);
        }

        void ShowRowMenu(ClipItem it, Point pos)
        {
            Border panel = new Border();
            panel.Background = Theme.Menu;
            panel.BorderBrush = Theme.PanelBorder;
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(6);
            panel.Padding = new Thickness(4);
            panel.Effect = Theme.Shadow(18, 0.35);
            StackPanel list = new StackPanel();
            panel.Child = list;

            Popup popup = new Popup();
            popup.AllowsTransparency = true;
            popup.StaysOpen = false;
            popup.PlacementTarget = this;
            popup.Placement = PlacementMode.Relative;
            popup.HorizontalOffset = pos.X + 4;
            popup.VerticalOffset = pos.Y + 4;
            popup.Child = panel;

            Action<string, bool, Action> add = delegate(string label, bool danger, Action act)
            {
                ClickBox c = new ClickBox();
                c.Height = 30;
                c.CornerRadius = new CornerRadius(4);
                c.Padding = new Thickness(10, 0, 24, 0);
                c.HoverBg = Theme.Hover;
                c.Child = Ui.Text(label, 11.5, danger ? Theme.DangerBrush : Theme.Txt);
                c.Clicked = delegate
                {
                    popup.IsOpen = false;
                    act();
                };
                list.Children.Add(c);
            };

            add("直接粘贴", false, delegate { Activate(it, true); });
            add("仅复制", false, delegate { Activate(it, false); });
            add(it.Pinned ? "取消置顶" : "置顶", false, delegate { _store.TouchPinned(it, !it.Pinned); RefreshFromStore(); });
            add(it.Fav ? "取消收藏" : "收藏", false, delegate { _store.TouchFav(it, !it.Fav); RefreshFromStore(); });
            if (it.IsImage)
            {
                add("查看大图", false, delegate { HideOverlay(); PreviewWindow.Open(it, _store, _cfg, OnPreviewAct); });
            }
            add("不再记录此内容", true, delegate
            {
                _store.BlockHash(it);
                BuildChips();
                Rebuild();
                _toastText.Text = "已删除，之后不再记录相同内容";
                if (_toastUndo != null) _toastUndo.Visibility = Visibility.Collapsed;
                _toast.Visibility = Visibility.Visible;
                _toastTimer.Stop();
                _toastTimer.Start();
            });

            popup.IsOpen = true;
        }

        ClickBox MiniBtn(string data, bool on, string tip, Action click)
        {
            ClickBox b = new ClickBox();
            b.Width = 22;
            b.Height = 22;
            b.CornerRadius = new CornerRadius(4);
            b.HoverBg = Theme.Hover;
            b.PressBg = Theme.Active;
            b.ToolTip = tip;
            b.Child = Ui.Icon(data, 13, on ? Theme.Acc : Theme.Txt3, 1.25);
            b.Clicked = click;
            return b;
        }

        static string TextKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return "doc";
            string t = text.Trim();
            if (t.StartsWith("http://") || t.StartsWith("https://") || t.StartsWith("www.")) return "link";
            if (t.Length < 120 && System.Text.RegularExpressions.Regex.IsMatch(t, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return "mail";
            if (t.IndexOf("SELECT ", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("function ", StringComparison.OrdinalIgnoreCase) >= 0
                || (t.IndexOf(";") >= 0 && t.IndexOf("{") >= 0)
                || t.StartsWith("curl ") || t.StartsWith("git "))
                return "code";
            return "doc";
        }

        static string IconFor(string kind)
        {
            if (kind == "link") return "M6.6,9.4 A2.8,2.8 0 0 0 10.6,9.4 L12,8 A2.8,2.8 0 0 0 8,4 L7.2,4.8 M9.4,6.6 A2.8,2.8 0 0 0 5.4,6.6 L4,8 A2.8,2.8 0 0 0 8,12 L8.8,11.2";
            if (kind == "mail") return "M2.4,3.8 L13.6,3.8 L13.6,12.2 L2.4,12.2 Z M3,4.4 L8,8.4 L13,4.4";
            if (kind == "code") return "M6,5.2 L3.4,8 L6,10.8 M10,5.2 L12.6,8 L10,10.8";
            return "M4,1.8 L9.2,1.8 L12,4.6 L12,14.2 L4,14.2 Z M9,1.9 L9,4.8 L11.9,4.8 M5.8,8.2 L10,8.2 M5.8,10.4 L8.8,10.4";
        }

        static Brush IconBrush(string kind)
        {
            if (kind == "link") return Theme.Hex("#4CC2FF");
            if (kind == "code") return Theme.Hex("#B18CFF");
            if (kind == "mail") return Theme.Hex("#F2B25C");
            if (Theme.IsLight)
            {
                if (kind == "link") return Theme.Hex("#0F6CBD");
                if (kind == "code") return Theme.Hex("#6F42C1");
                if (kind == "mail") return Theme.Hex("#9A6400");
            }
            return Theme.Txt2;
        }

        TextBlock PreviewBlock(ClipItem it)
        {
            TextBlock tb = new TextBlock();
            tb.FontSize = 12.5;
            tb.Foreground = Theme.Txt;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.FontFamily = Theme.Font;
            tb.ToolTip = it.Text;

            string text = it.Text == null ? "" : it.Text;
            if (TextKind(text) == "code")
            {
                tb.FontFamily = Theme.Mono;
                tb.FontSize = 11.5;
            }
            if (it.Sensitive && _cfg.MaskSensitive)
            {
                tb.Text = TextUtil.Mask(text);
                tb.Foreground = Theme.Txt2;
                return tb;
            }
            // 预览只显示两行，超出的文字不用参与排版（越长排版越慢）
            if (text.Length > 110) text = text.Substring(0, 110);
            text = text.Replace("\r\n", " ").Replace("\n", " ");

            string kw = _search.Text == null ? "" : _search.Text.Trim();
            if (kw.Length == 0)
            {
                tb.Text = text;
                return tb;
            }
            int pos = 0;
            while (true)
            {
                int i = text.IndexOf(kw, pos, StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                {
                    tb.Inlines.Add(new System.Windows.Documents.Run(text.Substring(pos)));
                    break;
                }
                if (i > pos) tb.Inlines.Add(new System.Windows.Documents.Run(text.Substring(pos, i - pos)));
                System.Windows.Documents.Run hit = new System.Windows.Documents.Run(text.Substring(i, kw.Length));
                hit.Background = Theme.AccSoft;
                hit.Foreground = Theme.Acc;
                tb.Inlines.Add(hit);
                pos = i + kw.Length;
            }
            return tb;
        }

        void Select(int index, bool scroll)
        {
            // 键盘/鼠标要选到还没渲染的行时，先补渲染
            if (index >= _rows.Count && _planPos < _plan.Count)
            {
                _renderTarget = _rows.Count + PageSize;
                if (index >= _renderTarget) _renderTarget = index + 1;
                RenderMore();
            }
            if (_rows.Count == 0) { _sel = -1; return; }
            if (index < 0) index = 0;
            if (index >= _rows.Count) index = _rows.Count - 1;

            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (i == index)
                {
                    r.Root.Background = Theme.Active;
                    r.Bar.Visibility = Visibility.Visible;
                    r.ActionBox.Opacity = 1;
                    r.ActionBox.IsHitTestVisible = true;
                }
                else
                {
                    r.Root.Background = Brushes.Transparent;
                    r.Bar.Visibility = Visibility.Collapsed;
                    r.ActionBox.Opacity = 0;
                    r.ActionBox.IsHitTestVisible = false;
                }
            }
            _sel = index;
            if (scroll)
            {
                Border el = _rows[index].Root;
                Dispatcher.BeginInvoke(new Action(delegate { el.BringIntoView(); }), DispatcherPriority.Background);
            }
        }

        void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (_datePopup != null && _datePopup.IsOpen)
            {
                if (e.Key == Key.Escape) { _datePopup.IsOpen = false; e.Handled = true; }
                return;
            }
            bool ctrlKey = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrlKey && e.Key == Key.D) { OpenDatePicker(); e.Handled = true; return; }
            if (e.Key == Key.PageUp) { ShiftDay(-1); e.Handled = true; return; }
            if (e.Key == Key.PageDown) { ShiftDay(1); e.Handled = true; return; }
            if (e.Key == Key.Left && !_search.IsKeyboardFocusWithin) { ShiftDay(-1); e.Handled = true; return; }
            if (e.Key == Key.Right && !_search.IsKeyboardFocusWithin) { ShiftDay(1); e.Handled = true; return; }
            if (e.Key == Key.Escape) { HideOverlay(); e.Handled = true; return; }
            if (e.Key == Key.Down) { Select(_sel + 1, true); e.Handled = true; return; }
            if (e.Key == Key.Up) { Select(_sel - 1, true); e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                if (_sel >= 0 && _sel < _rows.Count) Activate(_rows[_sel].Item, true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete)
            {
                if (_search.Text.Length == 0 && _sel >= 0 && _sel < _rows.Count)
                {
                    RemoveItem(_rows[_sel].Item);
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (_sel >= 0 && _sel < _rows.Count)
                {
                    Activate(_rows[_sel].Item, false);
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Key.Tab)
            {
                string[] order = new string[] { "all", "text", "image", "fav" };
                int i = Array.IndexOf(order, _filter);
                SetFilter(order[(i + 1) % order.Length]);
                e.Handled = true;
            }
        }

        void SearchKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.D) { OpenDatePicker(); e.Handled = true; return; }
            if (e.Key == Key.PageUp) { ShiftDay(-1); e.Handled = true; return; }
            if (e.Key == Key.PageDown) { ShiftDay(1); e.Handled = true; return; }
            if (e.Key == Key.Down) { Select(_sel + 1, true); e.Handled = true; }
            else if (e.Key == Key.Up) { Select(_sel - 1, true); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                if (_sel >= 0 && _sel < _rows.Count) Activate(_rows[_sel].Item, true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { HideOverlay(); e.Handled = true; }
        }

        // ------------------------------------------------------------------ 动作
        void Activate(ClipItem it, bool paste)
        {
            if (it == null) return;
            if (it.IsImage && !_store.HasLocalImage(it))
            {
                FetchRemoteImage(it);
                return;
            }
            _clickTimer.Stop();
            _pendingClick = null;
            _store.SetClipboard(it);
            HideOverlay();
            if (!paste || _target == IntPtr.Zero) return;

            IntPtr target = _target;
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(140);
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    Native.SetForegroundWindow(target);
                    System.Threading.Thread.Sleep(30);
                    Native.SendPaste();
                }
                catch { }
            };
            t.Start();
        }

        void OnPreviewAct(ClipItem it, bool paste)
        {
            if (it == null) return;
            if (it.IsImage && !_store.HasLocalImage(it))
            {
                FetchRemoteImage(it);
                return;
            }
            _store.SetClipboard(it);
            if (paste && _target != IntPtr.Zero)
            {
                Native.SetForegroundWindow(_target);
                System.Threading.Thread.Sleep(40);
                Native.SendPaste();
            }
        }

        void FetchRemoteImage(ClipItem it)
        {
            if (it == null) return;
            SyncService sync = AppHost.Current == null ? null : AppHost.Current.Sync;
            if (sync == null)
            {
                ShowHint("同步没有启用，无法获取这张图片");
                return;
            }
            string msg = sync.RequestImage(it);
            if (string.IsNullOrEmpty(msg)) msg = "正在获取图片…";
            ShowHint(msg);
        }

        void RemoveItem(ClipItem it)
        {
            int idx = _store.Items.IndexOf(it);
            _delIndex = idx < 0 ? 0 : idx;
            _delItem = it;
            _store.DeleteItem(it);
            _toastText.Text = "已删除";
            if (_toastUndo != null) _toastUndo.Visibility = Visibility.Visible;
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
            BuildChips();
            Rebuild();
        }

        void UndoDelete()
        {
            _toastTimer.Stop();
            HideToast();
            if (_delItem != null)
            {
                _store.RestoreItem(_delItem, _delIndex);
                _delItem = null;
                BuildChips();
                Rebuild();
            }
        }

        void HideToast()
        {
            _toast.Visibility = Visibility.Collapsed;
        }

        /// <summary>轻提示：不提供撤销，仅告知结果（如占位文字没取到图片）。</summary>
        public void ShowHint(string text)
        {
            if (_toast == null) return;
            _toastText.Text = text;
            if (_toastUndo != null) _toastUndo.Visibility = Visibility.Collapsed;
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }
}
