using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipHelper
{
    public class SettingsWindow : Window
    {
        AppSettings _cfg;
        Store _store;
        AppHost _host;

        StackPanel _nav;
        ScrollViewer _contentScroll;
        StackPanel _content;
        string _section = "general";
        DispatcherTimer _syncTimer;
        string _syncSig = "";

        public SettingsWindow(AppSettings cfg, Store store, AppHost host)
        {
            _cfg = cfg;
            _store = store;
            _host = host;

            Ui.SetupWindow(this, true);
            Ui.SlimScroll(this);
            // 高 DPI / 小屏幕下按工作区收缩，避免右侧内容被屏幕边缘裁掉
            Rect wa = SystemParameters.WorkArea;
            Width = Math.Max(680, Math.Min(880, wa.Width - 32));
            Height = Math.Max(420, Math.Min(600, wa.Height - 32));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Title = "剪贴板助手 · 设置";

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);

            Grid root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---------------- 左侧
            Border side = new Border();
            side.Background = Theme.IsLight ? Theme.Hex("#F3F5F9") : Theme.Hex("#191B1F");
            side.BorderBrush = Theme.Line;
            side.BorderThickness = new Thickness(0, 0, 1, 0);

            Grid sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(74) });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 品牌
            Grid brand = new Grid();
            brand.Margin = new Thickness(18, 18, 14, 0);
            brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border logo = new Border();
            logo.Width = 32;
            logo.Height = 32;
            logo.CornerRadius = new CornerRadius(8);
            logo.Background = Theme.AccSoft;
            logo.Child = Ui.Icon("M4.4,3.4 L11.6,3.4 L11.6,4.6 L4.4,4.6 Z M3.4,5.2 L12.6,5.2 L12.6,13.4 L3.4,13.4 Z M6,8 L10,8 M6,10.4 L9,10.4", 17, Theme.Acc, 1.2);
            brand.Children.Add(logo);

            StackPanel brandText = new StackPanel();
            brandText.Margin = new Thickness(10, 0, 0, 0);
            brandText.VerticalAlignment = VerticalAlignment.Center;
            TextBlock brandName = Ui.Text("剪贴板助手", 13, Theme.Txt);
            brandName.FontWeight = FontWeights.SemiBold;
            brandText.Children.Add(brandName);
            TextBlock brandSub = Ui.Text("v" + BuildInfo.AppVersion, 10.5, Theme.Txt3);
            brandSub.Margin = new Thickness(0, 2, 0, 0);
            brandText.Children.Add(brandSub);
            Grid.SetColumn(brandText, 1);
            brand.Children.Add(brandText);
            Grid.SetRow(brand, 0);
            sideGrid.Children.Add(brand);

            _nav = new StackPanel();
            _nav.Margin = new Thickness(10, 12, 10, 0);
            Grid.SetRow(_nav, 1);
            sideGrid.Children.Add(_nav);

            Border tip = new Border();
            tip.Margin = new Thickness(14, 0, 14, 16);
            tip.Padding = new Thickness(10, 8, 10, 8);
            tip.CornerRadius = new CornerRadius(6);
            tip.Background = Theme.Card;
            tip.BorderBrush = Theme.CardBorder;
            tip.BorderThickness = new Thickness(1);
            TextBlock tipText = Ui.Wrap("所有记录只保存在这台电脑上，不联网、不上报任何数据。", 10.5, Theme.Txt3);
            tipText.LineHeight = 16;
            tip.Child = tipText;
            Grid.SetRow(tip, 2);
            sideGrid.Children.Add(tip);

            side.Child = sideGrid;
            Grid.SetColumn(side, 0);
            root.Children.Add(side);

            // ---------------- 右侧
            Grid main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid top = new Grid();
            top.Margin = new Thickness(24, 0, 12, 0);
            top.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition topRightCol = new ColumnDefinition();
            topRightCol.Width = GridLength.Auto;
            top.ColumnDefinitions.Add(topRightCol);
            TextBlock pageTitle = Ui.Text("设置", 15, Theme.Txt);
            pageTitle.FontWeight = FontWeights.SemiBold;
            pageTitle.VerticalAlignment = VerticalAlignment.Center;
            pageTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(pageTitle, 0);
            top.Children.Add(pageTitle);

            StackPanel topRight = new StackPanel();
            topRight.Orientation = Orientation.Horizontal;
            topRight.VerticalAlignment = VerticalAlignment.Center;
            ClickBox pauseAll = Ui.IconBtn(Ui.Icon("M4,4 L12,12 M12,4 L4,12", 13, Theme.Txt2, 1.5), "关闭设置", Close);
            topRight.Children.Add(pauseAll);
            Grid.SetColumn(topRight, 1);
            top.Children.Add(topRight);
            Grid.SetRow(top, 0);
            main.Children.Add(top);

            _content = new StackPanel();
            _content.Margin = new Thickness(24, 0, 24, 24);
            _contentScroll = new ScrollViewer();
            _contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _contentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _contentScroll.Content = _content;
            Grid.SetRow(_contentScroll, 1);
            main.Children.Add(_contentScroll);

            Border topLine = new Border();
            topLine.Height = 1;
            topLine.Background = Theme.Line;
            topLine.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(topLine, 0);
            main.Children.Add(topLine);

            Grid.SetColumn(main, 1);
            root.Children.Add(main);

            card.Child = root;
            Content = card;
            Ui.EnableDrag(this, card, 56);

            BuildNav();
            BuildContent();

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Close();
            };
            Loaded += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Native.RoundCorners(hwnd);
            };
            Closed += delegate
            {
                if (_syncTimer != null) _syncTimer.Stop();
            };
        }

        // ------------------------------------------------------------------ 导航
        void BuildNav()
        {
            _nav.Children.Clear();
            AddNav("general", "通用", "M8,1.6 L8,3.2 M8,12.8 L8,14.4 M1.6,8 L3.2,8 M12.8,8 L14.4,8 M3.5,3.5 L4.6,4.6 M11.4,11.4 L12.5,12.5 M12.5,3.5 L11.4,4.6 M4.6,11.4 L3.5,12.5 M10.2,8 A2.2,2.2 0 1 1 5.8,8 A2.2,2.2 0 1 1 10.2,8");
            AddNav("hotkey", "快捷键", "M2.4,4.6 L13.6,4.6 L13.6,11.4 L2.4,11.4 Z M4.6,7 L4.6,7 M7,7 L7,7 M9.4,7 L9.4,7 M4.6,9 L10,9");
            AddNav("storage", "存储与清理", "M2.6,5.6 L13.4,5.6 L13.4,13 L2.6,13 Z M2.6,5.6 L2.6,4.4 L13.4,4.4 L13.4,5.6 M6,8.2 L10,8.2");
            AddNav("sync", "同步", "M2.4,4.6 L7,4.6 L7,10 L2.4,10 Z M9,6.2 L13.6,6.2 L13.6,11.6 L9,11.6 Z M7,7.3 L9,7.3");
            AddNav("perf", "性能", "M8,2.6 L8,8 L10.8,10 M14,8 A6,6 0 1 1 2,8 A6,6 0 1 1 14,8 M6.6,1.4 L9.4,1.4");
            AddNav("privacy", "隐私", "M8,1.8 L12.8,3.6 L12.8,7.4 C12.8,10.3 10.7,12.4 8,13.3 C5.3,12.4 3.2,10.3 3.2,7.4 L3.2,3.6 Z M6.2,7.6 L7.5,8.9 L10,6.4");
            AddNav("about", "关于", "M7.9,7.2 L7.9,11.4 M8,4.8 L8,4.9 M14.2,8 A6.2,6.2 0 1 1 1.8,8 A6.2,6.2 0 1 1 14.2,8");
        }

        void AddNav(string key, string label, string icon)
        {
            ClickBox item = new ClickBox();
            item.Height = 34;
            item.CornerRadius = new CornerRadius(6);
            item.Padding = new Thickness(10, 0, 10, 0);
            item.Margin = new Thickness(0, 1, 0, 1);
            item.HoverBg = Theme.Hover;

            bool active = _section == key;
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;
            FrameworkElement ico = Ui.Icon(icon, 15, active ? Theme.Acc : Theme.Txt3, 1.25);
            ico.Margin = new Thickness(0, 0, 9, 0);
            sp.Children.Add(ico);
            TextBlock t = Ui.Text(label, 12, active ? Theme.Acc : Theme.Txt2);
            if (active) t.FontWeight = FontWeights.SemiBold;
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            item.Child = sp;
            if (active)
            {
                item.Background = Theme.AccSoft;
                item.HoverBg = Theme.AccSoft;
            }
            item.Clicked = delegate
            {
                _section = key;
                BuildNav();
                BuildContent();
                _contentScroll.ScrollToTop();
            };
            _nav.Children.Add(item);
        }

        // ------------------------------------------------------------------ 内容
        void BuildContent()
        {
            _content.Children.Clear();
            if (_section == "general") BuildGeneral();
            else if (_section == "hotkey") BuildHotkey();
            else if (_section == "storage") BuildStorage();
            else if (_section == "sync") BuildSync();
            else if (_section == "perf") BuildPerformance();
            else if (_section == "privacy") BuildPrivacy();
            else BuildAbout();
        }

        void BuildGeneral()
        {
            StackPanel rows = new StackPanel();

            SwitchBox auto = new SwitchBox(_cfg.AutoStart);
            auto.Toggled = delegate(bool on)
            {
                _cfg.AutoStart = on;
                _cfg.Save();
                AppHost.SetAutoStart(on);
            };
            rows.Children.Add(ItemRow("开机自启", "登录 Windows 后自动在后台运行，不弹窗", auto));

            SwitchBox paused = new SwitchBox(_cfg.Paused);
            paused.Toggled = delegate(bool on)
            {
                _cfg.Paused = on;
                _cfg.Save();
                _host.NotifyPauseChanged();
            };
            rows.Children.Add(ItemRow("暂停记录", "暂停后新的复制内容不会被记录，已有记录不受影响", paused));

            rows.Children.Add(ItemRow("外观", "跟随系统会自动读取 Windows 的深浅色设置", BuildThemeSeg()));

            rows.Children.Add(ItemRow("呼出方式", "按 Alt+V 打开历史；再按一次或 Esc 收起", Ui.Text("Alt + V", 11.5, Theme.Txt2)));

            rows.Children.Add(ItemRow("回填方式", "回车或双击直接把内容粘贴到刚才的窗口", Ui.Text("直接粘贴", 11.5, Theme.Txt2)));

            _content.Children.Add(Section("通用", "启动、暂停与外观", rows));
        }

        FrameworkElement BuildThemeSeg()
        {
            Border wrap = new Border();
            wrap.Background = Theme.SegBrush;
            wrap.CornerRadius = new CornerRadius(7);
            wrap.Padding = new Thickness(2);

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            string[] labels = new string[] { "跟随系统", "浅色", "深色" };
            string[] values = new string[] { "auto", "light", "dark" };
            for (int i = 0; i < labels.Length; i++)
            {
                string val = values[i];
                bool active = _cfg.Theme == val;
                ClickBox b = new ClickBox();
                b.Height = 26;
                b.CornerRadius = new CornerRadius(5);
                b.Padding = new Thickness(12, 0, 12, 0);
                TextBlock t = Ui.Text(labels[i], 11.5, active ? Theme.Txt : Theme.Txt2);
                b.Child = t;
                if (active)
                {
                    b.Background = Theme.SegActive;
                    b.HoverBg = Theme.SegActive;
                    t.FontWeight = FontWeights.SemiBold;
                }
                b.Clicked = delegate
                {
                    if (_cfg.Theme == val) return;
                    _cfg.Theme = val;
                    _cfg.Save();
                    Dispatcher.BeginInvoke(new Action(delegate { _host.ReloadTheme(); }));
                };
                sp.Children.Add(b);
            }
            wrap.Child = sp;
            return wrap;
        }

        void BuildHotkey()
        {
            StackPanel rows = new StackPanel();

            rows.Children.Add(ItemRow("呼出剪贴板历史", "在任意应用里都能使用", KeyCaps(new string[] { "Alt", "V" })));
            rows.Children.Add(ItemRow("关闭浮层", "列表打开时按一次即可收起", KeyCaps(new string[] { "Esc" })));
            rows.Children.Add(ItemRow("浏览与粘贴", "上下键选择，回车直接粘贴到当前窗口", KeyCaps(new string[] { "↑", "↓", "Enter" })));
            rows.Children.Add(ItemRow("仅复制", "不粘贴，只把内容放回剪贴板", KeyCaps(new string[] { "Ctrl", "C" })));
            rows.Children.Add(ItemRow("删除记录", "删除后 3 秒内可以撤销", KeyCaps(new string[] { "Delete" })));
            rows.Children.Add(ItemRow("切换分类", "全部 → 文本 → 图片 → 收藏", KeyCaps(new string[] { "Tab" })));

            _content.Children.Add(Section("快捷键", "第一版固定 Alt+V，避免与系统或其他软件冲突", rows));

            TextBlock note = Ui.Wrap("说明：如果 Alt+V 被其他软件占用，托盘图标会提示；你仍然可以从托盘菜单打开列表。", 11, Theme.Txt3);
            note.LineHeight = 18;
            note.Margin = new Thickness(2, 10, 0, 0);
            _content.Children.Add(note);
        }

        FrameworkElement KeyCaps(string[] keys)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            foreach (string k in keys)
            {
                Border b = Ui.Kbd(k);
                b.Padding = new Thickness(7, 2, 7, 2);
                b.Margin = new Thickness(4, 0, 0, 0);
                sp.Children.Add(b);
            }
            return sp;
        }

        void BuildStorage()
        {
            StackPanel rows = new StackPanel();

            // 存储位置
            Grid pathBox = new Grid();
            pathBox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathBox.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathBox.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border field = new Border();
            field.Height = 30;
            field.CornerRadius = new CornerRadius(5);
            field.Background = Theme.Field;
            field.BorderBrush = Theme.FieldBorder;
            field.BorderThickness = new Thickness(1);
            field.Padding = new Thickness(9, 0, 9, 0);
            TextBlock pathText = Ui.Text(_store.Dir, 11, Theme.Txt2);
            pathText.VerticalAlignment = VerticalAlignment.Center;
            pathText.TextTrimming = TextTrimming.CharacterEllipsis;
            field.Child = pathText;
            field.ToolTip = _store.Dir;
            Grid.SetColumn(field, 0);
            pathBox.Children.Add(field);

            ClickBox open = Ui.Btn("打开", false, delegate
            {
                try { Process.Start("explorer.exe", "\"" + _store.Dir + "\""); } catch { }
            });
            open.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(open, 1);
            pathBox.Children.Add(open);

            StackPanel pathRight = new StackPanel();
            pathRight.Orientation = Orientation.Horizontal;
            ClickBox change = Ui.Btn("修改目录", false, ChangeDir);
            change.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(change, 2);
            pathBox.Children.Add(change);

            rows.Children.Add(BlockRow("存储位置", "记录与图片都保存在这个文件夹，可以迁移到其他磁盘；数据以加密文件保存", pathBox));

            // 占用
            string usage = _store.Items.Count + " 条记录 · 约 " + TextUtil.SizeText(_store.TotalBytes());
            rows.Children.Add(ItemRow("当前占用", usage, Ui.Text("", 11, Theme.Txt3)));

            // 保留条数
            rows.Children.Add(ItemRow("保留条数", "超出后自动清理最旧的记录；置顶和收藏不会被清理",
                new SelectBox(_cfg.MaxItems > 0 ? _cfg.MaxItems + " 条" : "不限",
                    new string[] { "200 条", "500 条", "1000 条", "3000 条", "不限" },
                    delegate(string v)
                    {
                        _cfg.MaxItems = ParseLimit(v);
                        _cfg.Save();
                        _store.Enforce();
                    })));

            // 保留天数
            rows.Children.Add(ItemRow("保留天数", "超过天数的记录会被自动清理",
                new SelectBox(_cfg.MaxDays > 0 ? _cfg.MaxDays + " 天" : "不限",
                    new string[] { "30 天", "90 天", "180 天", "365 天", "不限" },
                    delegate(string v)
                    {
                        _cfg.MaxDays = ParseLimit(v);
                        _cfg.Save();
                        _store.Enforce();
                    })));

            SwitchBox dedup = new SwitchBox(_cfg.Dedup);
            dedup.Toggled = delegate(bool on) { _cfg.Dedup = on; _cfg.Save(); };
            rows.Children.Add(ItemRow("重复内容去重", "同样的内容再次复制时，只保留一条并更新时间与次数", dedup));

            SwitchBox showCount = new SwitchBox(_cfg.ShowCount);
            showCount.Toggled = delegate(bool on) { _cfg.ShowCount = on; _cfg.Save(); };
            rows.Children.Add(ItemRow("显示复制次数", "在列表里显示这条内容被复制过几次", showCount));

            _content.Children.Add(Section("存储与清理", "默认保留最近 1000 条 / 90 天", rows));

            // 危险操作
            StackPanel danger = new StackPanel();
            Grid dangerRow = new Grid();
            dangerRow.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition dangerRight = new ColumnDefinition();
            dangerRight.Width = GridLength.Auto;
            dangerRow.ColumnDefinitions.Add(dangerRight);
            StackPanel dl = new StackPanel();
            TextBlock dt = Ui.Text("清空全部历史", 12, Theme.Txt);
            dl.Children.Add(dt);
            TextBlock dd = Ui.Text("删除所有文本与图片记录，操作无法撤销", 10.5, Theme.Txt3);
            dd.Margin = new Thickness(0, 3, 0, 0);
            dl.Children.Add(dd);
            Grid.SetColumn(dl, 0);
            dangerRow.Children.Add(dl);

            StackPanel dr = new StackPanel();
            dr.Orientation = Orientation.Horizontal;
            dr.VerticalAlignment = VerticalAlignment.Center;
            dr.Margin = new Thickness(12, 0, 0, 0);
            ClickBox clearImg = Ui.Btn("仅清空图片", false, true, delegate
            {
                bool ok = Ui.Confirm(this, "清空所有图片记录？", "只会删除图片记录和对应的图片文件，文本记录会保留。", "清空图片", "取消");
                if (!ok) return;
                _store.ClearImages();
                BuildContent();
            });
            clearImg.Margin = new Thickness(0, 0, 8, 0);
            dr.Children.Add(clearImg);
            ClickBox clear = Ui.Btn("清空全部", false, true, delegate
            {
                bool ok = Ui.Confirm(this, "清空全部历史？", "所有文本与图片记录都会从本机删除，无法恢复。", "清空", "取消");
                if (!ok) return;
                _store.ClearAll();
                BuildContent();
            });
            dr.Children.Add(clear);
            Grid.SetColumn(dr, 1);
            dangerRow.Children.Add(dr);

            danger.Children.Add(dangerRow);

            Border card = MakeCard();
            card.Margin = new Thickness(0, 14, 0, 0);
            card.Child = danger;
            _content.Children.Add(card);

            TextBlock note = Ui.Wrap("清理会在启动时和每次复制后自动进行；置顶和收藏的记录不会被自动清理。", 11, Theme.Txt3);
            note.Margin = new Thickness(2, 10, 0, 0);
            note.LineHeight = 18;
            _content.Children.Add(note);
        }

        static int ParseLimit(string v)
        {
            if (v.StartsWith("不限")) return 0;
            string digits = new string(v.TakeWhile(Char.IsDigit).ToArray());
            int n;
            if (int.TryParse(digits, out n)) return n;
            return 0;
        }

        // ------------------------------------------------------------------ 性能
        void BuildPerformance()
        {
            StackPanel rows = new StackPanel();

            rows.Children.Add(PerfRow("呼出列表（按 Alt+V 到能用）", "overlay.show",
                "从按下热键到列表真正出现在屏幕上", 300));
            rows.Children.Add(PerfRow("呼出列表：首帧显示", "overlay.firstframe",
                "窗口画到屏幕上的实际时间", 350));
            rows.Children.Add(PerfRow("切换日期（← → / 点击日期）", "date.switch",
                "换一天到列表刷新完成", 100));
            rows.Children.Add(PerfRow("列表刷新（重建可见条目）", "overlay.build",
                "筛选或换日期后重新生成列表", 80));
            rows.Children.Add(PerfRow("保存历史到磁盘", "store.save",
                "复制后加密写盘，已在后台完成，不应该影响手感", 80));
            rows.Children.Add(PerfRow("复制图片：压缩编码", "capture.image.encode",
                "大图转 PNG，已在后台线程做", 200));

            TextBlock size = Ui.Text(_store.Items.Count + " 条 · " + TextUtil.SizeText(_store.TotalBytes())
                + (_store.LastSaveMs > 0 ? " · 上次落盘 " + _store.LastSaveMs.ToString("0") + " ms" : ""), 11.5, Theme.Txt2);
            size.TextAlignment = TextAlignment.Right;
            size.VerticalAlignment = VerticalAlignment.Center;
            rows.Children.Add(ItemRow("记录条数与数据体积", "数据目录：" + _store.Dir, size));

            _content.Children.Add(Section("性能", "最近 300 次操作的本机耗时；绿色表示正常，橙色表示这一项偏慢", rows));

            // ---- 判据说明
            StackPanel tips = new StackPanel();
            tips.Children.Add(FontBlock("看起来像「程序的问题」", new string[] {
                "每次都慢，而且数字很稳定：例如每次呼出都要 500ms 以上、每次换日期都超过 150ms。",
                "记录越多越慢：攒到几百条以后明显变卡，这是列表或存储方式需要优化。",
                "同样的操作、同样的数据，隔一次再试还是那么慢。"
            }));
            tips.Children.Add(FontBlock("看起来像「电脑本身忙」", new string[] {
                "有时快有时慢，P95 是 P50 的好几倍（比如 P50 60ms、P95 900ms），说明是被别的东西挤了一下。",
                "同时开着浏览器、视频会议、编译或游戏时明显更慢，空闲时恢复正常。",
                "第一次复制大图特别慢、后面同样大小就快了：磁盘慢，或杀毒软件正在扫描新写入的文件。",
                "呼出列表第一次慢、之后快：Windows 第一次创建窗口和加载字体的固定开销，属于冷启动。",
                "保存历史（落盘）经常超过 200ms：多半是磁盘慢或杀毒实时扫描，可以把这个数据目录加进杀毒软件白名单。"
            }));
            _content.Children.Add(Section("怎么看这些数字", "不用懂技术，对照下面几条就能大致判断", tips));

            // ---- 诊断开关
            StackPanel diag = new StackPanel();

            SwitchBox logSwitch = new SwitchBox(_cfg.PerfLog);
            logSwitch.Toggled = delegate(bool on)
            {
                _cfg.PerfLog = on;
                _cfg.Save();
                Perf.Verbose = on;
            };
            diag.Children.Add(ItemRow("记录详细耗时日志",
                "打开后每次操作的时间都会写进本机的 perf.log，方便排查；平时建议关闭", logSwitch));

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            ClickBox refresh = Ui.Btn("刷新数据", false, delegate { BuildContent(); });
            refresh.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(refresh);
            ClickBox reset = Ui.Btn("清空统计", false, delegate { Perf.Reset(); BuildContent(); });
            reset.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(reset);
            btns.Children.Add(Ui.Btn("打开数据目录", false, delegate
            {
                try { Process.Start("explorer.exe", "\"" + _store.Dir + "\""); } catch { }
            }));
            diag.Children.Add(BlockRow("统计与日志", "日志位置：%LOCALAPPDATA%\\ClipboardHelper\\perf.log", btns));

            _content.Children.Add(Section("诊断", "需要进一步排查时再打开；这些数据只留在这台电脑上", diag));
        }

        /// <summary>一行「P50 / P95」耗时，按阈值上色。</summary>
        Border PerfRow(string title, string key, string desc, double goodP95)
        {
            Perf.Stat s = Perf.Get(key);
            string txt;
            Brush color = Theme.Txt3;
            if (s.N == 0)
            {
                txt = "还没有数据";
            }
            else
            {
                txt = "P50 " + s.P50.ToString("0") + " / P95 " + s.P95.ToString("0") + " ms";
                bool ok = s.P95 <= goodP95;
                color = Theme.Hex(ok
                    ? (Theme.IsLight ? "#1F8A4C" : "#4CD18A")
                    : (Theme.IsLight ? "#B46000" : "#E2AB34"));
                desc = desc + " · 样本 " + s.N + " 次";
            }
            TextBlock v = Ui.Text(txt, 11.5, color);
            v.TextAlignment = TextAlignment.Right;
            v.VerticalAlignment = VerticalAlignment.Center;
            return ItemRow(title, desc, v);
        }

        /// <summary>一段小标题 + 若干条说明。</summary>
        FrameworkElement FontBlock(string title, string[] items)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(0, 8, 0, 10);
            TextBlock t = Ui.Text(title, 12, Theme.Txt);
            t.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(t);
            foreach (string line in items)
            {
                TextBlock d = Ui.Wrap("· " + line, 11, Theme.Txt3);
                d.Margin = new Thickness(0, 5, 0, 0);
                d.LineHeight = 17;
                sp.Children.Add(d);
            }
            return sp;
        }

        void BuildPrivacy()
        {
            StackPanel rows = new StackPanel();

            SwitchBox pwd = new SwitchBox(_cfg.PasswordNoDisk);
            pwd.Toggled = delegate(bool on) { _cfg.PasswordNoDisk = on; _cfg.Save(); };
            rows.Children.Add(ItemRow("疑似密码不落盘", "形似密码的短随机串直接丢弃，不写入历史文件", pwd));

            SwitchBox mask = new SwitchBox(_cfg.MaskSensitive);
            mask.Toggled = delegate(bool on) { _cfg.MaskSensitive = on; _cfg.Save(); };
            rows.Children.Add(ItemRow("敏感内容打码显示", "身份证、银行卡号等只显示首尾字符，鼠标悬停可看全文", mask));

            SwitchBox sys = new SwitchBox(true);
            sys.IsEnabled = false;
            sys.Opacity = 0.6;
            rows.Children.Add(ItemRow("尊重系统隐私标记（始终开启）", "标记为「不进入剪贴板历史」的内容不会被记录", sys));

            _content.Children.Add(Section("隐私", "默认全部在本机完成，不联网", rows));

            TextBlock note = Ui.Wrap("历史数据保存在本机，并做了用户级加密（同一台电脑的其他 Windows 账户无法读取）。第一版没有账号、没有云同步，也不做任何匿名统计上报。", 11, Theme.Txt3);
            note.Margin = new Thickness(2, 10, 0, 0);
            note.LineHeight = 18;
            _content.Children.Add(note);

            ClickBox block = Ui.Btn("查看不再记录的内容", false, ShowBlocked);
            block.Margin = new Thickness(2, 14, 0, 0);
            block.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(block);
        }

        void ShowBlocked()
        {
            int n = _cfg.BlockedHashes == null ? 0 : _cfg.BlockedHashes.Count;
            if (n == 0)
            {
                Ui.Info(this, "没有已排除的内容", "在列表里右键任意一条记录，选择「不再记录此内容」，之后相同的复制不会再出现。");
                return;
            }
            bool ok = Ui.Confirm(this, "已排除 " + n + " 条内容", "这些内容不会再被记录。需要恢复记录吗？", "全部恢复", "取消");
            if (ok)
            {
                _cfg.BlockedHashes.Clear();
                _cfg.Save();
            }
        }

        void BuildAbout()
        {
            StackPanel rows = new StackPanel();
            rows.Children.Add(ItemRow("版本", "当前安装版本", Ui.Text("v" + BuildInfo.AppVersion, 11.5, Theme.Txt2)));
            ClickBox checkUpdate = Ui.Btn("检查更新", false, delegate { UpdateService.CheckNow(_cfg, this); });
            rows.Children.Add(ItemRow("更新", "查看新版本与更新内容，确认后再下载", checkUpdate));
            rows.Children.Add(ItemRow("呼出快捷键", "全局生效", KeyCaps(new string[] { "Alt", "V" })));
            rows.Children.Add(ItemRow("数据目录", _store.Dir, new TextBlock()));
            rows.Children.Add(ItemRow("记录上限", "可随时在「存储与清理」里调整", Ui.Text(_cfg.MaxItems > 0 ? _cfg.MaxItems + " 条 / " + (_cfg.MaxDays > 0 ? _cfg.MaxDays + " 天" : "不限") : "不限", 11.5, Theme.Txt2)));

            _content.Children.Add(Section("关于", "一个只在本地运行的 Windows 剪贴板历史工具", rows));

            TextBlock note = Ui.Wrap("跨设备同步、账号与多语言会在后续版本提供；第一版专注把本机的复制粘贴做得又快又稳。", 11, Theme.Txt3);
            note.Margin = new Thickness(2, 10, 0, 0);
            note.LineHeight = 18;
            _content.Children.Add(note);
        }

        // ------------------------------------------------------------------ 目录迁移
        void ChangeDir()
        {
            using (System.Windows.Forms.FolderBrowserDialog d = new System.Windows.Forms.FolderBrowserDialog())
            {
                d.Description = "选择一个文件夹来保存剪贴板历史（建议放在非系统盘）";
                d.ShowNewFolderButton = true;
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string target = d.SelectedPath;
                if (string.IsNullOrEmpty(target)) return;
                string newDir = Path.Combine(target, "ClipboardHelper");
                if (string.Equals(Path.GetFullPath(newDir).TrimEnd('\\'), Path.GetFullPath(_store.Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    Ui.Info(this, "目录没有变化", "新位置和当前目录是一样的。");
                    return;
                }
                DoChangeDir(newDir);
            }
        }

        void DoChangeDir(string newDir)
        {
            Directory.CreateDirectory(newDir);
            long bytes = _store.TotalBytes();
            int count = _store.Items.Count;

            ChoiceDialog choice = new ChoiceDialog(
                "切换存储位置",
                "新位置：" + newDir + "\n\n当前有 " + count + " 条记录，约 " + TextUtil.SizeText(bytes) + "。\n" +
                "· 迁移并切换：把现有记录复制到新位置后切换，原位置的文件会保留。\n" +
                "· 仅切换：不复制旧记录，新位置从空白开始。",
                new string[] { "迁移并切换", "仅切换", "取消" });
            choice.Owner = this;
            choice.ShowDialog();
            if (choice.Result < 0 || choice.Result == 2) return;

            if (choice.Result == 1)
            {
                SwitchDir(newDir, null);
                return;
            }

            string oldDir = _store.Dir;
            ProgressDialog prog = new ProgressDialog("正在迁移数据…", "准备中");
            prog.Owner = this;
            List<string> copied = new List<string>();

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    _store.Flush();
                    CopyDirWithProgress(oldDir, newDir, copied, delegate(string file, int percent)
                    {
                        prog.Update(percent, file);
                    });
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        prog.Close();
                        SwitchDir(newDir, oldDir);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        prog.Close();
                        foreach (string f in copied)
                        {
                            try { File.Delete(f); } catch { }
                        }
                        Ui.Info(this, "迁移失败", "没有切换存储位置，原来的记录不受影响。\n\n" + ex.Message);
                    }));
                }
            });
            prog.ShowDialog();
        }

        void SwitchDir(string newDir, string oldDir)
        {
            _cfg.DataDir = newDir;
            _cfg.Save();
            _store.Reopen(newDir);
            BuildContent();

            if (!string.IsNullOrEmpty(oldDir))
            {
                bool del = Ui.Confirm(this, "迁移完成", "记录已迁移到新位置。原目录里的旧记录还占着空间，要删除吗？", "删除旧文件", "先保留");
                if (del)
                {
                    DeleteOurFiles(oldDir);
                }
            }
        }

        void DeleteOurFiles(string dir)
        {
            try
            {
                string hist = Path.Combine(dir, "history.dat");
                if (File.Exists(hist)) File.Delete(hist);
                string img = Path.Combine(dir, "images");
                if (Directory.Exists(img)) Directory.Delete(img, true);
                string th = Path.Combine(dir, "thumbs");
                if (Directory.Exists(th)) Directory.Delete(th, true);
            }
            catch { }
        }

        static void CopyDirWithProgress(string oldDir, string newDir, List<string> copied, Action<string, int> progress)
        {
            List<string> files = new List<string>();
            if (Directory.Exists(oldDir))
            {
                files.AddRange(Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories));
            }
            // 只复制我们自己的文件
            files = files.FindAll(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            int i = 0;
            foreach (string f in files)
            {
                string rel = f.Substring(oldDir.Length).TrimStart('\\');
                string dest = Path.Combine(newDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(f, dest, true);
                copied.Add(dest);
                i++;
                int pct = files.Count == 0 ? 100 : (int)(i * 100.0 / files.Count);
                progress(rel, pct);
            }
            if (files.Count == 0) progress("完成", 100);
        }

        // ------------------------------------------------------------------ 同步
        void EnsureSyncTimer()
        {
            if (_syncTimer == null)
            {
                _syncTimer = new DispatcherTimer();
                _syncTimer.Interval = TimeSpan.FromMilliseconds(1000);
                _syncTimer.Tick += delegate
                {
                    if (_section != "sync") { _syncTimer.Stop(); return; }
                    string sig = SyncSignature();
                    if (sig != _syncSig)
                    {
                        _syncSig = sig;
                        BuildContent();
                    }
                };
            }
            if (!_syncTimer.IsEnabled) _syncTimer.Start();
        }

        string SyncSignature()
        {
            SyncService s = _host.Sync;
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(_cfg.SyncMode).Append('|').Append(s.StatusText).Append('|').Append(s.PeerOnline ? "1" : "0");
            sb.Append('|').Append(s.OutboxCount).Append('|').Append(s.CurrentCode).Append('|').Append(s.CodeSecondsLeft / 10);
            sb.Append('|').Append(s.FolderPairPending ? "1" : "0").Append('|').Append(s.FolderPairCode);
            sb.Append('|').Append(s.JoinWaiting ? "1" : "0");
            sb.Append('|').Append(s.RelayDir);
            sb.Append('|').Append(s.ServerAddressText).Append(s.ServerCode).Append(s.InviteCode).Append(s.ServerReachable ? "1" : "0").Append(s.ServerLatencyText);
            foreach (ServerDeviceInfo sd in s.SnapshotServerDevices()) sb.Append('|').Append("D").Append(sd.Id).Append(sd.Online ? "1" : "0");
            foreach (Discovered d in s.SnapshotDiscovered()) sb.Append('|').Append(d.Id).Append(':').Append(d.Address);
            foreach (PendingPair p in s.SnapshotPending()) sb.Append('|').Append("P").Append(p.Id);
            foreach (PairInvite i in s.SnapshotInvites()) sb.Append('|').Append("I").Append(i.Id);
            SyncPeer pp = s.Peer;
            if (pp != null)
            {
                sb.Append('|').Append(pp.Alias).Append(pp.Name).Append(pp.Paused ? "1" : "0");
                sb.Append('|').Append(s.LastSyncLocal.ToString("yyyyMMddHHmm"));
            }
            return sb.ToString();
        }

        void BuildSync()
        {
            SyncService sync = _host.Sync;
            if (sync == null) return;
            EnsureSyncTimer();

            StackPanel mode = new StackPanel();
            mode.Children.Add(ItemRow("当前方式", "三种方式共用同一套加密；「仅本机」不联网，另外两种需要两台电脑确认一次", ModeSeg(sync)));
            string sub = _cfg.SyncMode == "server"
                ? "内容在这台电脑上加密，只经由你自己的服务器中转，服务端看不到明文"
                : "两台电脑点对点连接，不需要账号，也不经过任何服务器";
            _content.Children.Add(Section("同步", sub, mode));

            if (_cfg.SyncMode != "lan" && _cfg.SyncMode != "server")
            {
                StackPanel empty = new StackPanel();
                StackPanel right = new StackPanel();
                right.Orientation = Orientation.Horizontal;
                ClickBox start = Ui.Btn("开始配对", true, delegate
                {
                    sync.SetMode("lan");
                    sync.RefreshCode();
                    BuildNav();
                    BuildContent();
                });
                right.Children.Add(start);
                ClickBox byServer = Ui.Btn("用自建服务器", false, delegate
                {
                    sync.SetMode("server");
                    BuildNav();
                    BuildContent();
                });
                byServer.Margin = new Thickness(8, 0, 0, 0);
                right.Children.Add(byServer);
                empty.Children.Add(BlockRow("把这台电脑和另一台连起来",
                    "配过对之后，两台电脑的文字记录自动同步；图片只同步「有这么一张图」，点开时才获取。两台不在同一个网络时，用「自建服务器」最稳。", right));
                _content.Children.Add(Section("开始配对", "同一网络可以直接发现对方；不在同一网络时，用自建服务器中转，或用共享文件夹交换配对凭据", empty));

                TextBlock note = Ui.Wrap("现在是「仅本机」：所有记录只保存在这台电脑上，不联网。切到「局域网配对」或「自建服务器」后才会开始同步。",
                    11, Theme.Txt3);
                note.Margin = new Thickness(2, 10, 0, 0);
                note.LineHeight = 18;
                _content.Children.Add(note);
                return;
            }

            if (_cfg.SyncMode == "server")
            {
                BuildSyncStatus(sync);
                BuildSyncServer(sync);
                if (sync.Peer != null) BuildSyncDevices(sync);
                BuildSyncScope(sync);
                return;
            }

            BuildSyncStatus(sync);
            if (sync.Peer == null) BuildSyncPairing(sync);
            else BuildSyncDevices(sync);
            BuildSyncScope(sync);
        }

        FrameworkElement ModeSeg(SyncService sync)
        {
            Border wrap = new Border();
            wrap.Background = Theme.SegBrush;
            wrap.CornerRadius = new CornerRadius(7);
            wrap.Padding = new Thickness(2);

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            string[] labels = new string[] { "仅本机", "局域网配对", "自建服务器" };
            string[] values = new string[] { "local", "lan", "server" };
            for (int i = 0; i < labels.Length; i++)
            {
                string val = values[i];
                bool active = _cfg.SyncMode == val;
                ClickBox b = new ClickBox();
                b.Height = 26;
                b.CornerRadius = new CornerRadius(5);
                b.Padding = new Thickness(12, 0, 12, 0);
                TextBlock t = Ui.Text(labels[i], 11.5, active ? Theme.Txt : Theme.Txt2);
                b.Child = t;
                if (active)
                {
                    b.Background = Theme.SegActive;
                    b.HoverBg = Theme.SegActive;
                    t.FontWeight = FontWeights.SemiBold;
                }
                b.Clicked = delegate
                {
                    if (_cfg.SyncMode == val) return;
                    if (val == "lan")
                    {
                        sync.SetMode("lan");
                        sync.RefreshCode();
                    }
                    else if (val == "server")
                    {
                        sync.SetMode("server");
                    }
                    else
                    {
                        bool ok = Ui.Confirm(this, "切回「仅本机」？", "会断开与另一台电脑的同步连接，已配对的设备保留在列表里，本机历史不受影响。要用时再切回来即可，不需要重新配对。", "切回仅本机", "取消");
                        if (!ok) return;
                        sync.SetMode("local");
                    }
                    BuildNav();
                    BuildContent();
                };
                sp.Children.Add(b);
            }
            wrap.Child = sp;
            return wrap;
        }

        Brush StatusBrush(int level)
        {
            if (level == 1) return Theme.Hex(Theme.IsLight ? "#1F8A4C" : "#4CD18A");
            if (level == 2) return Theme.Hex(Theme.IsLight ? "#B46000" : "#E2AB34");
            if (level == 3) return Theme.Hex(Theme.IsLight ? "#B3261E" : "#F2716B");
            return Theme.Txt3;
        }

        static string Ago(DateTime local)
        {
            if (local == DateTime.MinValue) return "还没有同步过";
            double m = (DateTime.Now - local).TotalMinutes;
            if (m < 1) return "刚刚";
            if (m < 60) return ((int)m) + " 分钟前";
            double h = m / 60;
            if (h < 24) return ((int)h) + " 小时前";
            return local.ToString("M月d日 HH:mm");
        }

        void BuildSyncStatus(SyncService sync)
        {
            StackPanel rows = new StackPanel();
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;

            TextBlock st = Ui.Text(sync.StatusText, 11.5, StatusBrush(sync.StatusLevel));
            st.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(st);

            if (sync.LastError != null)
            {
                ClickBox retry = Ui.Btn("重试", false, delegate { sync.RetryNow(); BuildContent(); });
                retry.Margin = new Thickness(8, 0, 0, 0);
                right.Children.Add(retry);
            }

            string desc;
            SyncPeer peer = sync.Peer;
            if (peer == null) desc = "还没有配对设备，按下面任意一种方式加入";
            else
            {
                desc = "最近同步：" + Ago(sync.LastSyncLocal);
                if (sync.OutboxCount > 0) desc += " · 待补传 " + sync.OutboxCount + " 条";
            }
            if (_cfg.SyncMode == "server" && sync.ServerConfigured)
            {
                desc += " · " + sync.ServerAddressText + " · " + sync.ServerLatencyText;
            }
            rows.Children.Add(ItemRow("同步状态", desc, right));
            _content.Children.Add(Section("状态", "同一个状态点也会显示在列表窗口右上角", rows));
        }

        void BuildSyncPairing(SyncService sync)
        {
            // ---- 把配对码给对方
            StackPanel give = new StackPanel();
            StackPanel codeRight = new StackPanel();
            codeRight.Orientation = Orientation.Horizontal;
            codeRight.VerticalAlignment = VerticalAlignment.Center;

            string code = sync.CurrentCode;
            if (string.IsNullOrEmpty(code))
            {
                code = sync.RefreshCode();
            }
            string shown = code.Length == 6 ? code.Substring(0, 3) + " " + code.Substring(3) : "——— ———";
            TextBlock codeText = Ui.Text(shown, 22, Theme.Txt);
            codeText.FontFamily = Theme.Mono;
            codeText.FontWeight = FontWeights.SemiBold;
            codeText.VerticalAlignment = VerticalAlignment.Center;
            codeRight.Children.Add(codeText);

            ClickBox refresh = Ui.Btn("换一个", false, delegate { sync.RefreshCode(); BuildContent(); });
            refresh.Margin = new Thickness(12, 0, 0, 0);
            codeRight.Children.Add(refresh);

            int left = sync.CodeSecondsLeft;
            string expire = left > 0 ? ("剩余 " + (left / 60) + " 分 " + (left % 60) + " 秒有效") : "已过期，点「换一个」重新生成";
            give.Children.Add(BlockRow("配对码", expire, codeRight));

            List<PendingPair> pending = sync.SnapshotPending();
            foreach (PendingPair p in pending)
            {
                PendingPair req = p;
                StackPanel btns = new StackPanel();
                btns.Orientation = Orientation.Horizontal;
                ClickBox deny = Ui.Btn("拒绝", false, delegate { sync.DenyPending(req.Id); BuildContent(); });
                btns.Children.Add(deny);
                ClickBox allow = Ui.Btn("允许加入", true, delegate
                {
                    sync.AllowPending(req.Id);
                    BuildContent();
                    Ui.Info(this, "已允许加入", req.Name + " 现在和这台电脑建立了加密连接。文字记录会自动同步；图片要点开时才获取。");
                });
                allow.Margin = new Thickness(8, 0, 0, 0);
                btns.Children.Add(allow);
                give.Children.Add(ItemRow("请求加入：" + req.Name,
                    req.Os + " · " + SyncService.MaskAddress(req.Address) + " · 配对码 " + req.Code, btns));
            }
            if (pending.Count > 0)
            {
                TextBlock warn = Ui.Wrap("如果不是你本人操作，请点「拒绝」，并重新生成配对码。", 10.5,
                    Theme.Hex(Theme.IsLight ? "#B46000" : "#E2AB34"));
                warn.Margin = new Thickness(0, 8, 0, 10);
                warn.LineHeight = 17;
                give.Children.Add(warn);
            }
            _content.Children.Add(Section("把配对码给对方", "", give));

            // ---- 加入另一台电脑
            StackPanel join = new StackPanel();
            List<Discovered> found = sync.SnapshotDiscovered();
            if (found.Count == 0)
            {
                StackPanel joinBtns = new StackPanel();
                joinBtns.Orientation = Orientation.Horizontal;
                ClickBox enter = Ui.Btn("输入配对码", true, delegate { PromptPairCodeAny(sync); });
                joinBtns.Children.Add(enter);
                join.Children.Add(ItemRow("输入配对码", "另一台也要切到「局域网配对」", joinBtns));
            }
            else
            {
                foreach (Discovered d in found)
                {
                    Discovered dev = d;
                    StackPanel btns = new StackPanel();
                    btns.Orientation = Orientation.Horizontal;
                    ClickBox byCode = Ui.Btn("输入配对码", false, delegate { PromptPairCode(dev); });
                    btns.Children.Add(byCode);
                    ClickBox req = Ui.Btn("请求加入", false, delegate { PromptJoin(dev); });
                    req.Margin = new Thickness(8, 0, 0, 0);
                    btns.Children.Add(req);
                    join.Children.Add(ItemRow(dev.Name, dev.Os + " · " + SyncService.MaskAddress(dev.Address), btns));
                }
            }
            _content.Children.Add(Section("加入另一台电脑", "", join));

            BuildSyncFolderPairing(sync);
        }

        // ---- 自建同步服务器（密文中转）
        void BuildSyncServer(SyncService sync)
        {
            // ---- 地址与端口
            StackPanel addrLine = new StackPanel();
            addrLine.Orientation = Orientation.Horizontal;
            TextBox hostBox = MakeField(sync.ServerHost, "例如 47.100.12.34 或 sync.example.com", 250);
            addrLine.Children.Add(hostBox);
            TextBlock colon = Ui.Text(":", 12, Theme.Txt3);
            colon.Margin = new Thickness(7, 0, 7, 0);
            colon.VerticalAlignment = VerticalAlignment.Center;
            addrLine.Children.Add(colon);
            TextBox portBox = MakeField(sync.ServerPort.ToString(), "47820", 68);
            addrLine.Children.Add(portBox);

            TextBlock result = Ui.Wrap(sync.ServerConfigured ? "当前：" + sync.ServerAddressText : "填好地址后点「测试连通」，能连上再往下配对。",
                10.5, Theme.Txt3);
            result.LineHeight = 17;
            result.Margin = new Thickness(0, 8, 0, 2);

            StackPanel acts = new StackPanel();
            acts.Orientation = Orientation.Horizontal;
            ClickBox save = Ui.Btn("保存并生效", true, delegate
            {
                SaveServerFields(sync, hostBox, portBox);
                BuildContent();
            });
            acts.Children.Add(save);
            ClickBox test = Ui.Btn("测试连通", false, delegate { TestServer(sync, hostBox, portBox, result); });
            test.Margin = new Thickness(8, 0, 0, 0);
            acts.Children.Add(test);

            StackPanel rows = new StackPanel();
            rows.Children.Add(BlockRow("服务器地址", "填 IP 或域名都行（可以整段粘贴 http://1.2.3.4:47820）。端口要和服务器启动时一致，默认 47820。", addrLine));
            rows.Children.Add(BlockRow("先做一次连通测试", "失败会直接说是哪一步：地址解析、端口不通、配对码不对、还是证书不受信。", acts));
            rows.Children.Add(result);

            StackPanel flags = new StackPanel();
            flags.Orientation = Orientation.Horizontal;
            ClickBox httpsChip = ServerChip("使用 HTTPS", sync.ServerHttps, delegate
            {
                SaveServerFields(sync, hostBox, portBox, !sync.ServerHttps, sync.ServerAllowSelfSigned);
                BuildContent();
            });
            flags.Children.Add(httpsChip);
            ClickBox selfChip = ServerChip("信任自签名证书", sync.ServerAllowSelfSigned, delegate
            {
                SaveServerFields(sync, hostBox, portBox, sync.ServerHttps, !sync.ServerAllowSelfSigned);
                BuildContent();
            });
            selfChip.Margin = new Thickness(8, 0, 0, 0);
            flags.Children.Add(selfChip);
            rows.Children.Add(BlockRow("HTTPS（可选）", "内容本身已经端到端加密，TLS 只是额外一层。自己签的证书要勾上「信任自签名证书」，否则会提示证书不受信。", flags));

            _content.Children.Add(Section("服务器", "服务端只做密文中转：看不到你的内容，也没有解密密钥", rows));

            // ---- 配对
            if (sync.Peer == null) BuildSyncServerPairing(sync);
            else BuildSyncServerDevices(sync);
        }

        void BuildSyncServerPairing(SyncService sync)
        {
            StackPanel pair = new StackPanel();

            // 第一台：生成配对码
            StackPanel firstBtns = new StackPanel();
            firstBtns.Orientation = Orientation.Horizontal;
            if (sync.ServerCodeActive)
            {
                string c = sync.ServerCode;
                string shown = c.Length == 6 ? c.Substring(0, 3) + " " + c.Substring(3) : c;
                TextBlock codeText = Ui.Text(shown, 20, Theme.Acc);
                codeText.FontFamily = Theme.Mono;
                codeText.FontWeight = FontWeights.SemiBold;
                codeText.VerticalAlignment = VerticalAlignment.Center;
                codeText.Margin = new Thickness(0, 0, 12, 0);
                firstBtns.Children.Add(codeText);

                ClickBox again = Ui.Btn("换一个", false, delegate
                {
                    string err = sync.RefreshServerCode();
                    if (err != null) Ui.Info(this, "没能生成配对码", err);
                    BuildContent();
                });
                firstBtns.Children.Add(again);
                ClickBox cancel = Ui.Btn("取消", false, delegate { sync.CancelServerPairing(); BuildContent(); });
                cancel.Margin = new Thickness(8, 0, 0, 0);
                firstBtns.Children.Add(cancel);
            }
            else
            {
                ClickBox make = Ui.Btn("生成配对码", true, delegate
                {
                    string err = sync.CreateServerRoom();
                    if (err != null) Ui.Info(this, "没能生成配对码", err);
                    BuildContent();
                });
                firstBtns.Children.Add(make);
            }
            pair.Children.Add(BlockRow("第一台：生成配对码", "生成后把这 6 位数字告诉另一台电脑；5 分钟内有效，配对成功立即作废。", firstBtns));

            if (sync.ServerCodeActive)
            {
                TextBlock tip = Ui.Wrap("在另一台电脑上：设置 → 同步 → 自建服务器，填同一个服务器地址，再输入上面这 6 位配对码，然后在这台点「允许加入」。",
                    10.5, Theme.Txt3);
                tip.LineHeight = 17;
                tip.Margin = new Thickness(0, 0, 0, 8);
                pair.Children.Add(tip);
            }

            // 第二台：输入配对码
            StackPanel secondBtns = new StackPanel();
            secondBtns.Orientation = Orientation.Horizontal;
            ClickBox join = Ui.Btn("输入配对码并加入", false, delegate { PromptServerJoin(sync); });
            secondBtns.Children.Add(join);
            pair.Children.Add(BlockRow("第二台：输入配对码", "在另一台电脑上填同一个服务器地址，再填第一台显示的 6 位配对码。", secondBtns));

            // 待确认
            List<PendingPair> pending = sync.SnapshotPending();
            if (pending.Count > 0)
            {
                StackPanel wait = new StackPanel();
                foreach (PendingPair p in pending)
                {
                    PendingPair req = p;
                    StackPanel btns = new StackPanel();
                    btns.Orientation = Orientation.Horizontal;
                    ClickBox deny = Ui.Btn("拒绝", false, delegate { sync.DenyPending(req.Id); BuildContent(); });
                    btns.Children.Add(deny);
                    ClickBox allow = Ui.Btn("允许加入", true, delegate
                    {
                        sync.AllowPending(req.Id);
                        BuildContent();
                        Ui.Info(this, "已允许加入", "配对密钥会用配对码加密后送到对方，服务器只见到密文。\n\n之后文字记录自动同步；图片只同步信息，点开时才获取。");
                    });
                    allow.Margin = new Thickness(8, 0, 0, 0);
                    btns.Children.Add(allow);
                    wait.Children.Add(ItemRow("请求加入：" + req.Name, req.Os + " · " + req.Address, btns));
                }
                TextBlock warn = Ui.Wrap("如果不是你本人操作，请点「拒绝」，再点「换一个」重新生成配对码。", 10.5,
                    Theme.Hex(Theme.IsLight ? "#B46000" : "#E2AB34"));
                warn.Margin = new Thickness(0, 8, 0, 10);
                warn.LineHeight = 17;
                wait.Children.Add(warn);
                pair.Children.Add(BlockRow("待确认", "安全关键路径：不允许静音，每次新设备加入都要你点一次", wait));
            }

            _content.Children.Add(Section("配对（经服务器中转）", "首次配对只做一次；之后每台设备各自和服务器保持一条长连接，房间最多 4 台", pair));
        }

        void BuildSyncServerDevices(SyncService sync)
        {
            StackPanel rows = new StackPanel();
            List<ServerDeviceInfo> list = sync.SnapshotServerDevices();
            if (list.Count == 0)
            {
                TextBlock none = Ui.Wrap("正在从服务器读取设备列表，稍等几秒会自动刷新。", 11, Theme.Txt3);
                none.LineHeight = 18;
                none.Margin = new Thickness(0, 6, 0, 10);
                rows.Children.Add(none);
            }
            foreach (ServerDeviceInfo d in list)
            {
                ServerDeviceInfo dev = d;
                StackPanel right = new StackPanel();
                right.Orientation = Orientation.Horizontal;
                string stateText = dev.Self ? "本机" : (dev.Online ? "在线" : "离线");
                TextBlock state = Ui.Text(stateText, 11,
                    dev.Online || dev.Self ? Theme.Hex(Theme.IsLight ? "#1F8A4C" : "#4CD18A") : Theme.Txt3);
                state.VerticalAlignment = VerticalAlignment.Center;
                state.Margin = new Thickness(0, 0, 10, 0);
                right.Children.Add(state);

                if (!dev.Self)
                {
                    ClickBox revoke = Ui.Btn("吊销", false, true, delegate
                    {
                        bool ok = Ui.Confirm(this, "吊销「" + dev.Name + "」？",
                            "会立即把它从服务器白名单里删除并销毁它的收件箱，两台需要重新配对才能同步。两台电脑上的历史记录都完整保留。", "吊销", "取消");
                        if (!ok) return;
                        string err = sync.RevokeServerDevice(dev.Id);
                        if (err != null) Ui.Info(this, "没能吊销", err);
                        BuildNav();
                        BuildContent();
                    });
                    right.Children.Add(revoke);
                }

                string desc = string.IsNullOrEmpty(dev.Os) ? "系统版本未知" : dev.Os;
                if (!dev.Self && dev.LastSeenSec >= 0)
                {
                    desc += dev.Online ? " · 刚刚还连着" : " · 最近在线 " + Ago(DateTime.Now.AddSeconds(-dev.LastSeenSec));
                }
                rows.Children.Add(ItemRow(dev.Self ? sync.DeviceName : dev.Name, desc, right));
            }
            _content.Children.Add(Section("服务器上的已配对设备", "最多 4 台；服务器只知道「谁跟谁配对、谁在线」，不知道同步了什么", rows));

            // ---- 邀请第 3 / 4 台：复用现有房间与密钥，只重新取一个 6 位码
            StackPanel invite = new StackPanel();
            StackPanel inviteBtns = new StackPanel();
            inviteBtns.Orientation = Orientation.Horizontal;
            if (sync.InviteCodeActive)
            {
                string ic = sync.InviteCode;
                string shown = ic.Length == 6 ? ic.Substring(0, 3) + " " + ic.Substring(3) : ic;
                TextBlock codeText = Ui.Text(shown, 20, Theme.Acc);
                codeText.FontFamily = Theme.Mono;
                codeText.FontWeight = FontWeights.SemiBold;
                codeText.VerticalAlignment = VerticalAlignment.Center;
                codeText.Margin = new Thickness(0, 0, 12, 0);
                inviteBtns.Children.Add(codeText);
            }
            ClickBox makeInvite = Ui.Btn(sync.InviteCodeActive ? "换一个" : "邀请新设备", true, delegate
            {
                string err = sync.InviteServerDevice();
                if (err != null) Ui.Info(this, "没能生成邀请码", err);
                BuildContent();
            });
            inviteBtns.Children.Add(makeInvite);
            invite.Children.Add(BlockRow(sync.InviteCodeActive ? "邀请码" : "添加设备",
                "把 6 位数字告诉新设备；5 分钟内有效，对方被允许后立即作废。", inviteBtns));

            List<PendingPair> pending = sync.SnapshotPending();
            if (pending.Count > 0)
            {
                foreach (PendingPair p in pending)
                {
                    PendingPair req = p;
                    StackPanel btns = new StackPanel();
                    btns.Orientation = Orientation.Horizontal;
                    ClickBox deny = Ui.Btn("拒绝", false, delegate { sync.DenyPending(req.Id); BuildContent(); });
                    btns.Children.Add(deny);
                    ClickBox allow = Ui.Btn("允许加入", true, delegate
                    {
                        sync.AllowPending(req.Id);
                        BuildContent();
                    });
                    allow.Margin = new Thickness(8, 0, 0, 0);
                    btns.Children.Add(allow);
                    invite.Children.Add(ItemRow("请求加入：" + req.Name, req.Os + " · " + req.Address, btns));
                }
                TextBlock warn = Ui.Wrap("只允许你认识的设备加入。新设备拿到的是房间现有的密钥，加入后所有设备共用同一把。",
                    10.5, Theme.Hex(Theme.IsLight ? "#B46000" : "#E2AB34"));
                warn.Margin = new Thickness(0, 8, 0, 10);
                warn.LineHeight = 17;
                invite.Children.Add(warn);
            }
            _content.Children.Add(Section("邀请新设备", "房间最多 4 台；新设备从加入时刻起同步，之前的历史不会倒灌", invite));
        }

        TextBox MakeField(string text, string tip, double width)
        {
            TextBox box = new TextBox();
            box.Text = text == null ? "" : text;
            box.Width = width;
            box.Height = 30;
            box.FontSize = 11.5;
            box.FontFamily = Theme.Font;
            box.Foreground = Theme.Txt;
            box.Background = Theme.Field;
            box.BorderBrush = Theme.FieldBorder;
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(8, 0, 8, 0);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.ToolTip = tip;
            return box;
        }

        ClickBox ServerChip(string text, bool active, Action click)
        {
            ClickBox b = new ClickBox();
            b.Height = 26;
            b.CornerRadius = new CornerRadius(5);
            b.Padding = new Thickness(10, 0, 10, 0);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = Theme.FieldBorder;
            b.Background = active ? Theme.SegActive : Theme.Field;
            b.HoverBg = active ? Theme.SegActive : Theme.SegBrush;
            TextBlock t = Ui.Text(text, 11, active ? Theme.Txt : Theme.Txt2);
            t.VerticalAlignment = VerticalAlignment.Center;
            if (active) t.FontWeight = FontWeights.SemiBold;
            b.Child = t;
            b.Clicked = delegate { click(); };
            return b;
        }

        void SaveServerFields(SyncService sync, TextBox hostBox, TextBox portBox)
        {
            SaveServerFields(sync, hostBox, portBox, sync.ServerHttps, sync.ServerAllowSelfSigned);
        }

        void SaveServerFields(SyncService sync, TextBox hostBox, TextBox portBox, bool https, bool selfSigned)
        {
            int port = 0;
            if (!int.TryParse(portBox.Text.Trim(), out port) || port <= 0 || port > 65535) port = sync.ServerPort;
            sync.ConfigureServer(hostBox.Text, port, https, selfSigned);
        }

        void TestServer(SyncService sync, TextBox hostBox, TextBox portBox, TextBlock result)
        {
            SaveServerFields(sync, hostBox, portBox);
            result.Foreground = Theme.Txt2;
            result.Text = "正在测试 " + sync.ServerAddressText + " …";
            string code = sync.ServerCode;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg = sync.TestServerConnection(code);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    result.Text = msg;
                    bool ok = msg.StartsWith("连接成功");
                    result.Foreground = ok
                        ? Theme.Hex(Theme.IsLight ? "#1F8A4C" : "#4CD18A")
                        : Theme.Hex(Theme.IsLight ? "#B3261E" : "#F2716B");
                }));
            });
        }

        void PromptServerJoin(SyncService sync)
        {
            if (!sync.ServerConfigured)
            {
                Ui.Info(this, "先填服务器地址", "把服务器地址和端口填在上面，点「保存并生效」，再输入配对码。");
                return;
            }
            TextInputDialog dlg = new TextInputDialog("输入配对码",
                "在另一台电脑上打开 设置 → 同步 → 自建服务器，把显示的 6 位配对码填在这里。", "6 位数字", 6);
            dlg.Owner = this;
            dlg.ShowDialog();
            if (!dlg.OK) return;
            string code = dlg.Value;

            ProgressDialog prog = new ProgressDialog("正在加入…", "申请已发给对方，请在另一台电脑上点「允许加入」（最多等 2 分钟）");
            prog.Owner = this;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = sync.JoinServerRoom(code);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    prog.Close();
                    if (err != null)
                    {
                        Ui.Info(this, "没有加入成功", err);
                        BuildContent();
                        return;
                    }
                    BuildNav();
                    BuildContent();
                    Ui.Info(this, "配对成功", "两台电脑已经通过你自己的服务器建立了加密连接。\n\n文字记录会自动同步；图片只同步信息，点开时才获取。");
                }));
            });
            prog.ShowDialog();
        }

        // ---- 经由共享文件夹交换配对凭据（两台不在同一网络时用）
        void BuildSyncFolderPairing(SyncService sync)
        {
            StackPanel rows = new StackPanel();

            Grid pathBox = new Grid();
            pathBox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathBox.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Border field = new Border();
            field.Height = 30;
            field.CornerRadius = new CornerRadius(5);
            field.Background = Theme.Field;
            field.BorderBrush = Theme.FieldBorder;
            field.BorderThickness = new Thickness(1);
            field.Padding = new Thickness(9, 0, 9, 0);
            TextBlock pathText = Ui.Text(string.IsNullOrEmpty(sync.RelayDir) ? "还没有选择共享文件夹" : sync.RelayDir, 11,
                string.IsNullOrEmpty(sync.RelayDir) ? Theme.Txt3 : Theme.Txt2);
            pathText.VerticalAlignment = VerticalAlignment.Center;
            pathText.TextTrimming = TextTrimming.CharacterEllipsis;
            field.Child = pathText;
            if (!string.IsNullOrEmpty(sync.RelayDir)) field.ToolTip = sync.RelayDir;
            Grid.SetColumn(field, 0);
            pathBox.Children.Add(field);

            ClickBox choose = Ui.Btn("选择文件夹", false, delegate { ChooseRelayDir(sync); });
            choose.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(choose, 1);
            pathBox.Children.Add(choose);

            rows.Children.Add(BlockRow("共享文件夹", "选一个已经在两台电脑之间自动同步的文件夹（OneDrive、坚果云、百度网盘、iCloud 等都可以，不接各家接口，只当普通文件夹读写）。两台电脑要选同一个。", pathBox));

            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            ClickBox make = Ui.Btn("生成配对邀请", true, delegate
            {
                string err = sync.CreateFolderInvite();
                if (err != null) Ui.Info(this, "没能生成邀请", err);
                BuildContent();
            });
            actions.Children.Add(make);

            if (sync.FolderPairPending)
            {
                ClickBox cancel = Ui.Btn("取消邀请", false, delegate { sync.CancelFolderInvite(); BuildContent(); });
                cancel.Margin = new Thickness(8, 0, 0, 0);
                actions.Children.Add(cancel);
            }
            rows.Children.Add(BlockRow("第一台：生成邀请", "生成后把 6 位配对码给对方；对方确认后，这里会在几秒内自动变成「已配对」。", actions));

            if (sync.FolderPairPending)
            {
                string c = sync.FolderPairCode;
                string shown = c.Length == 6 ? c.Substring(0, 3) + " " + c.Substring(3) : c;
                TextBlock code = Ui.Text(shown, 20, Theme.Acc);
                code.FontFamily = Theme.Mono;
                code.FontWeight = FontWeights.SemiBold;
                TextBlock waiting = Ui.Text("等待另一台电脑确认…（最多 10 分钟）", 11, Theme.Txt3);
                waiting.Margin = new Thickness(12, 0, 0, 0);
                waiting.VerticalAlignment = VerticalAlignment.Center;
                StackPanel line = new StackPanel();
                line.Orientation = Orientation.Horizontal;
                line.Children.Add(code);
                line.Children.Add(waiting);
                rows.Children.Add(BlockRow("这台电脑的配对码", "对方在他的电脑上选择同一个共享文件夹，点「读取邀请」，输入这 6 位数字。", line));
            }

            List<PairInvite> invites = sync.SnapshotInvites();
            StackPanel list = new StackPanel();
            if (invites.Count == 0)
            {
                TextBlock none = Ui.Wrap(string.IsNullOrEmpty(sync.RelayDir)
                    ? "先选择共享文件夹，然后点「刷新邀请」查看对方写的邀请。"
                    : "共享文件夹里还没有别人的邀请。让对方在他的电脑上点「生成配对邀请」，等网盘同步过来后点「刷新邀请」。", 11, Theme.Txt3);
                none.LineHeight = 18;
                none.Margin = new Thickness(0, 6, 0, 10);
                list.Children.Add(none);
                ClickBox refresh = Ui.Btn("刷新邀请", false, delegate { BuildContent(); });
                refresh.Margin = new Thickness(0, 0, 0, 10);
                refresh.HorizontalAlignment = HorizontalAlignment.Left;
                list.Children.Add(refresh);
            }
            else
            {
                foreach (PairInvite inv in invites)
                {
                    PairInvite item = inv;
                    StackPanel btns = new StackPanel();
                    btns.Orientation = Orientation.Horizontal;
                    ClickBox go = Ui.Btn("输入配对码并加入", true, delegate { PromptAcceptInvite(item); });
                    btns.Children.Add(go);
                    list.Children.Add(ItemRow(item.Name, item.Os + " · 邀请有效期至 " + item.ExpireUtc.ToLocalTime().ToString("HH:mm"), btns));
                }
            }
            rows.Children.Add(BlockRow("第二台：读取邀请", "在另一台电脑上重复同样的步骤：选择同一个文件夹 → 读取邀请 → 输入对方显示的 6 位配对码。", list));

            TextBlock note = Ui.Wrap("本轮先完成配对与「同一网络下的文字同步」；通过共享文件夹自动同步记录（跨网络）会在下一版提供。",
                10.5, Theme.Txt3);
            note.Margin = new Thickness(0, 0, 0, 10);
            note.LineHeight = 17;
            rows.Children.Insert(0, note);

            _content.Children.Add(Section("用共享文件夹完成配对", "", rows));
        }

        void ChooseRelayDir(SyncService sync)
        {
            using (System.Windows.Forms.FolderBrowserDialog d = new System.Windows.Forms.FolderBrowserDialog())
            {
                d.Description = "选择两台电脑都能读写的共享/网盘同步文件夹";
                d.ShowNewFolderButton = true;
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (string.IsNullOrEmpty(d.SelectedPath)) return;
                sync.RelayDir = d.SelectedPath;
                BuildContent();
            }
        }

        void PromptAcceptInvite(PairInvite inv)
        {
            TextInputDialog dlg = new TextInputDialog("输入配对码",
                "在「" + inv.Name + "」上打开 设置 → 同步，把显示的 6 位配对码填在这里。", "6 位数字", 6);
            dlg.Owner = this;
            dlg.ShowDialog();
            if (!dlg.OK) return;
            string err = _host.Sync.AcceptInvite(inv, dlg.Value);
            if (err != null)
            {
                Ui.Info(this, "没有加入成功", err);
                return;
            }
            BuildNav();
            BuildContent();
            Ui.Info(this, "配对成功", "两台电脑已经建立加密连接。\n\n本轮先支持「在同一网络下」自动同步文字记录；图片只同步信息，点开时才获取。\n\n若两台不在同一网络，下一版会通过共享文件夹自动同步，不用重新配对。");
        }

        void PromptPairCodeAny(SyncService sync)
        {
            TextInputDialog dlg = new TextInputDialog("输入配对码",
                "在另一台电脑上打开 设置 → 同步，把显示的 6 位配对码填在这里。", "6 位数字", 6);
            dlg.Owner = this;
            dlg.ShowDialog();
            if (!dlg.OK) return;
            string code = dlg.Value;

            List<Discovered> found = sync.SnapshotDiscovered();
            if (found.Count == 0)
            {
                Ui.Info(this, "还没发现对方电脑",
                    "另一台也要打开 设置 → 同步 → 切到「局域网配对」，并连到同一个 Wi-Fi / 网段。");
                return;
            }
            Discovered target = found[0];
            RunPairAction("正在配对…", "正在和对方电脑交换密钥",
                delegate { return _host.Sync.PairWith(target.Id, code); });
        }

        void PromptPairCode(Discovered d)
        {
            TextInputDialog dlg = new TextInputDialog("输入配对码",
                "在「" + d.Name + "」上打开 设置 → 同步，把显示的 6 位配对码填在这里。", "6 位数字", 6);
            dlg.Owner = this;
            dlg.ShowDialog();
            if (!dlg.OK) return;
            string code = dlg.Value;
            RunPairAction("正在配对…", "正在和对方电脑交换密钥", delegate { return _host.Sync.PairWith(d.Id, code); });
        }

        void PromptJoin(Discovered d)
        {
            bool ok = Ui.Confirm(this, "向「" + d.Name + "」请求加入？",
                "对方屏幕上会弹出确认提示。对方点「允许加入」后自动完成配对，不需要配对码。", "发送请求", "取消");
            if (!ok) return;
            RunPairAction("等待对方确认…", "已发送加入请求，请在对方电脑上点「允许加入」（最多等 2 分钟）",
                delegate { return _host.Sync.RequestJoin(d.Id); });
        }

        void RunPairAction(string title, string message, Func<string> action)
        {
            ProgressDialog prog = new ProgressDialog(title, message);
            prog.Owner = this;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = action();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    prog.Close();
                    if (err != null)
                    {
                        Ui.Info(this, "配对没有完成", err);
                        return;
                    }
                    BuildNav();
                    BuildContent();
                    Ui.Info(this, "配对成功", "两台电脑已经建立加密连接。文字记录会自动同步（同一网络下）；图片只同步信息，点开时才获取。");
                }));
            });
            prog.ShowDialog();
        }

        void BuildSyncDevices(SyncService sync)
        {
            SyncPeer peer = sync.Peer;
            StackPanel rows = new StackPanel();

            StackPanel selfBtns = new StackPanel();
            selfBtns.Orientation = Orientation.Horizontal;
            ClickBox renameSelf = Ui.Btn("改名", false, delegate
            {
                TextInputDialog dlg = new TextInputDialog("给这台电脑起个名字", "只影响两台电脑上的显示，不影响同步。", "例如：公司台式机", 24);
                dlg.Value = sync.DeviceName;
                dlg.Owner = this;
                dlg.ShowDialog();
                if (!dlg.OK) return;
                sync.RenameLocal(dlg.Value);
                BuildContent();
            });
            selfBtns.Children.Add(renameSelf);
            TextBlock selfTag = Ui.Text("本机", 10.5, Theme.Txt3);
            selfTag.Margin = new Thickness(10, 0, 0, 0);
            selfTag.VerticalAlignment = VerticalAlignment.Center;
            selfBtns.Children.Add(selfTag);
            rows.Children.Add(ItemRow(sync.DeviceName, SyncService.OsText + " · 这台电脑（不能被移除）", selfBtns));

            StackPanel peerBtns = new StackPanel();
            peerBtns.Orientation = Orientation.Horizontal;
            TextBlock state = Ui.Text(sync.PeerOnline ? "在线" : "离线", 11,
                sync.PeerOnline ? Theme.Hex(Theme.IsLight ? "#1F8A4C" : "#4CD18A") : Theme.Txt3);
            state.VerticalAlignment = VerticalAlignment.Center;
            state.Margin = new Thickness(0, 0, 10, 0);
            peerBtns.Children.Add(state);

            ClickBox renamePeer = Ui.Btn("改名", false, delegate
            {
                TextInputDialog dlg = new TextInputDialog("给这台设备起个名字", "只影响这台电脑上的显示，例如「家里笔记本」。", "例如：家里笔记本", 24);
                dlg.Value = peer.Alias;
                dlg.Owner = this;
                dlg.ShowDialog();
                if (!dlg.OK) return;
                sync.RenamePeer(dlg.Value);
                BuildContent();
            });
            renamePeer.Margin = new Thickness(0, 0, 10, 0);
            peerBtns.Children.Add(renamePeer);

            SwitchBox sw = new SwitchBox(!peer.Paused);
            sw.Toggled = delegate(bool on)
            {
                sync.SetPeerPaused(peer.Id, !on);
                BuildContent();
            };
            TextBlock swLabel = Ui.Text("同步", 10.5, Theme.Txt3);
            swLabel.VerticalAlignment = VerticalAlignment.Center;
            swLabel.Margin = new Thickness(0, 0, 6, 0);
            peerBtns.Children.Add(swLabel);
            peerBtns.Children.Add(sw);

            ClickBox remove = Ui.Btn("移除", false, true, delegate
            {
                bool ok = Ui.Confirm(this, "移除「" + sync.PeerName + "」？",
                    "移除后两台不再同步。两台电脑上的历史记录都会完整保留，不会删除。要重新加回来，需要再确认一次配对码。", "移除设备", "取消");
                if (!ok) return;
                sync.RemovePeer(peer.Id);
                BuildNav();
                BuildContent();
            });
            remove.Margin = new Thickness(10, 0, 0, 0);
            peerBtns.Children.Add(remove);

            string peerDesc = (string.IsNullOrEmpty(peer.Os) ? "系统版本未知" : peer.Os)
                + (string.IsNullOrEmpty(peer.Address) ? "" : " · " + SyncService.MaskAddress(peer.Address))
                + " · 最近同步 " + Ago(sync.LastSyncLocal);
            rows.Children.Add(ItemRow(sync.PeerName, peerDesc, peerBtns));

            _content.Children.Add(Section("设备", "局域网直连一次只连 1 台（本机 + 1 台）；要多台互同步，请切到「自建服务器」，房间最多 4 台。关掉开关只是停止收发，不等于解绑",
                rows));

            StackPanel danger = new StackPanel();
            danger.Children.Add(ItemRow("同步总开关", "关掉后暂时不收发任何内容（配对关系保留），打开就继续",
                MakePauseAll(sync)));
            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            ClickBox key = Ui.Btn("更换配对密钥", false, delegate
            {
                bool ok = Ui.Confirm(this, "更换配对密钥？",
                    "会立即作废当前密钥，另一台电脑需要重新配对。两台电脑上的历史记录都会完整保留。", "更换密钥", "取消");
                if (!ok) return;
                sync.DisconnectAll();
                BuildNav();
                BuildContent();
                Ui.Info(this, "已更换配对密钥", "旧的连接已经作废。在另一台电脑上重新走一次配对流程即可。");
            });
            btns.Children.Add(key);
            ClickBox all = Ui.Btn("断开全部设备", false, true, delegate
            {
                bool ok = Ui.Confirm(this, "断开全部设备？",
                    "会立即断开并清除配对关系，需要重新配对才能同步。两台电脑上的历史记录完整保留，只会停止同步。", "全部断开", "取消");
                if (!ok) return;
                sync.DisconnectAll();
                BuildNav();
                BuildContent();
                Ui.Info(this, "已断开全部设备", "同步已停止，历史记录没有被删除。需要时重新配对即可。");
            });
            all.Margin = new Thickness(8, 0, 0, 0);
            btns.Children.Add(all);
            danger.Children.Add(BlockRow("紧急处置", "怀疑设备被他人掌握时使用；断开不会删除你电脑上的历史记录。", btns));

            _content.Children.Add(Section("断开与重置", "没有账号可改密，紧急处置的对象是设备与密钥", danger));
        }

        FrameworkElement MakePauseAll(SyncService sync)
        {
            SwitchBox sw = new SwitchBox(!_cfg.SyncPaused);
            sw.Toggled = delegate(bool on)
            {
                sync.SetAllPaused(!on);
                BuildContent();
            };
            return sw;
        }

        void BuildSyncScope(SyncService sync)
        {
            StackPanel rows = new StackPanel();
            rows.Children.Add(ItemRow("文字内容", "包含收藏、置顶、复制次数、删除动作", Ui.Text("始终同步", 11.5, Theme.Txt2)));
            rows.Children.Add(ItemRow("图片", "只同步「有这么一张图」和尺寸、格式；原图永不自动下载", Ui.Text("按需获取", 11.5, Theme.Txt2)));
            rows.Children.Add(ItemRow("敏感 / 疑似密码", "和本机「密码类不落盘」同一原则，不参与同步", Ui.Text("不同步", 11.5, Theme.Txt2)));
            rows.Children.Add(ItemRow("设备名与状态", "设备名、系统版本、最近同步时间", Ui.Text("随同步更新", 11.5, Theme.Txt2)));
            _content.Children.Add(Section("同步范围", "下面是固定规则，不需要你调", rows));

            TextBlock note = Ui.Wrap(_cfg.SyncMode == "server"
                ? "数据在传输前就加密好了，服务器只负责转手密文：它看不到内容，也没有解密密钥。TLS/HTTPS 是可选的额外一层，主要用于挡住「这一路连接里能看出你在同步」这件事。"
                : "数据在传输前加密，只有两台已配对的电脑能解开。本轮是「同一网络下」的点对点同步；跨网络的共享文件夹同步在下一版补齐，已配对关系继续有效。",
                11, Theme.Txt3);
            note.Margin = new Thickness(2, 10, 0, 0);
            note.LineHeight = 18;
            _content.Children.Add(note);
        }

        // ------------------------------------------------------------------ 小组件
        Border MakeCard()
        {
            Border card = new Border();
            card.Background = Theme.Card;
            card.BorderBrush = Theme.CardBorder;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(10);
            card.Padding = new Thickness(16, 6, 16, 6);
            return card;
        }

        Border ItemRow(string title, string desc, FrameworkElement right)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 12, 0, 12);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel();
            TextBlock t = Ui.Text(title, 12.5, Theme.Txt);
            left.Children.Add(t);
            if (!string.IsNullOrEmpty(desc))
            {
                TextBlock d = Ui.Wrap(desc, 10.5, Theme.Txt3);
                d.Margin = new Thickness(0, 3, 0, 0);
                d.LineHeight = 16;
                left.Children.Add(d);
            }
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            if (right != null)
            {
                right.VerticalAlignment = VerticalAlignment.Center;
                right.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(right, 1);
                g.Children.Add(right);
            }

            Border wrap = new Border();
            wrap.Child = g;
            wrap.BorderBrush = Theme.Line;
            wrap.BorderThickness = new Thickness(0, 0, 0, 1);
            return wrap;
        }

        Border BlockRow(string title, string desc, FrameworkElement content)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(0, 12, 0, 12);
            TextBlock t = Ui.Text(title, 12.5, Theme.Txt);
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(desc))
            {
                TextBlock d = Ui.Wrap(desc, 10.5, Theme.Txt3);
                d.Margin = new Thickness(0, 3, 0, 0);
                d.LineHeight = 16;
                sp.Children.Add(d);
            }
            content.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(content);
            Border wrap = new Border();
            wrap.Child = sp;
            wrap.BorderBrush = Theme.Line;
            wrap.BorderThickness = new Thickness(0, 0, 0, 1);
            return wrap;
        }

        Border Section(string title, string desc, Panel rows)
        {
            StackPanel sp = new StackPanel();
            TextBlock t = Ui.Text(title, 14, Theme.Txt);
            t.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(desc))
            {
                TextBlock d = Ui.Text(desc, 11, Theme.Txt3);
                d.Margin = new Thickness(0, 4, 0, 6);
                sp.Children.Add(d);
            }
            sp.Children.Add(rows);

            Border card = MakeCard();
            card.Child = sp;
            card.Padding = new Thickness(18, 14, 18, 6);
            return card;
        }
    }

    /// <summary>三选一确认框（迁移目录时用）。</summary>
    public class ChoiceDialog : Window
    {
        public int Result = -1;

        public ChoiceDialog(string title, string message, string[] buttons)
        {
            Ui.SetupWindow(this, true);
            SizeToContent = SizeToContent.Height;
            Width = 460;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(22, 20, 22, 16);

            StackPanel sp = new StackPanel();
            TextBlock h = Ui.Text(title, 14.5, Theme.Txt);
            h.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(h);
            TextBlock body = Ui.Wrap(message, 11.5, Theme.Txt2);
            body.LineHeight = 19;
            body.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(body);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 20, 0, 0);
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                ClickBox b = Ui.Btn(buttons[i], i == 0, delegate
                {
                    Result = idx;
                    Close();
                });
                b.Margin = new Thickness(8, 0, 0, 0);
                btns.Children.Add(b);
            }
            sp.Children.Add(btns);
            card.Child = sp;
            Content = card;

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Result = -1; Close(); }
            };
        }
    }

    /// <summary>单行输入框（配对码、设备改名）。</summary>
    public class TextInputDialog : Window
    {
        public bool OK;
        public string Value = "";
        TextBox _box;

        public TextInputDialog(string title, string message, string hint, int maxLen)
        {
            Ui.SetupWindow(this, true);
            SizeToContent = SizeToContent.Height;
            Width = 440;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(22, 20, 22, 16);

            StackPanel sp = new StackPanel();
            TextBlock h = Ui.Text(title, 14.5, Theme.Txt);
            h.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(h);

            TextBlock body = Ui.Wrap(message, 11.5, Theme.Txt2);
            body.LineHeight = 19;
            body.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(body);

            Border field = new Border();
            field.Height = 36;
            field.Margin = new Thickness(0, 14, 0, 0);
            field.CornerRadius = new CornerRadius(6);
            field.Background = Theme.Field;
            field.BorderBrush = Theme.FieldBorder;
            field.BorderThickness = new Thickness(1);
            field.Padding = new Thickness(10, 0, 10, 0);

            _box = new TextBox();
            _box.Background = System.Windows.Media.Brushes.Transparent;
            _box.BorderThickness = new Thickness(0);
            _box.Foreground = Theme.Txt;
            _box.CaretBrush = Theme.Acc;
            _box.FontSize = 13;
            _box.FontFamily = Theme.Mono;
            _box.VerticalContentAlignment = VerticalAlignment.Center;
            _box.MaxLength = maxLen;
            field.Child = _box;
            sp.Children.Add(field);

            TextBlock tip = Ui.Text(hint, 10.5, Theme.Txt3);
            tip.Margin = new Thickness(1, 6, 0, 0);
            sp.Children.Add(tip);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 18, 0, 0);
            ClickBox cancel = Ui.Btn("取消", false, delegate { Close(); });
            btns.Children.Add(cancel);
            ClickBox ok = Ui.Btn("确定", true, delegate { Accept(); });
            ok.Margin = new Thickness(8, 0, 0, 0);
            btns.Children.Add(ok);
            sp.Children.Add(btns);

            card.Child = sp;
            Content = card;

            _box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Close();
            };
            Loaded += delegate
            {
                _box.Text = Value;
                _box.Focus();
                _box.SelectAll();
            };
        }

        void Accept()
        {
            Value = (_box.Text == null ? "" : _box.Text.Trim());
            OK = true;
            Close();
        }
    }

    /// <summary>迁移进度框。</summary>
    public class ProgressDialog : Window
    {
        ProgressBar _bar;
        TextBlock _file;

        public ProgressDialog(string title, string message)
        {
            Ui.SetupWindow(this, true);
            Width = 420;
            Height = 150;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(22, 18, 22, 18);

            StackPanel sp = new StackPanel();
            TextBlock h = Ui.Text(title, 13.5, Theme.Txt);
            h.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(h);

            _file = Ui.Text(message, 10.5, Theme.Txt3);
            _file.Margin = new Thickness(0, 8, 0, 8);
            _file.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(_file);

            _bar = new ProgressBar();
            _bar.Height = 6;
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Value = 0;
            _bar.Foreground = Theme.Acc;
            _bar.Background = Theme.Field;
            _bar.BorderThickness = new Thickness(0);
            sp.Children.Add(_bar);

            card.Child = sp;
            Content = card;
        }

        public void Update(int percent, string file)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { Update(percent, file); }));
                return;
            }
            _bar.Value = percent;
            _file.Text = file;
        }
    }
}
