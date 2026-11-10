using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipHelper
{
    /// <summary>图片大图预览：适应窗口显示、仅复制、直接粘贴。</summary>
    public class PreviewWindow : Window
    {
        Store _store;
        ClipItem _item;
        Action<ClipItem, bool> _act;

        internal PreviewWindow(ClipItem it, Store store, AppSettings cfg, Action<ClipItem, bool> act)
        {
            _store = store;
            _item = it;
            _act = act;

            Ui.SetupWindow(this, true);
            // 高 DPI / 小屏幕下按工作区收缩，避免底部按钮行被屏幕边缘裁掉
            Rect wa = SystemParameters.WorkArea;
            Width = Math.Max(560, Math.Min(880, wa.Width - 32));
            Height = Math.Max(380, Math.Min(620, wa.Height - 32));
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });

            // 头部（两列：标题占满可伸缩区，关闭按钮固定右侧，避免长标题压到按钮上）
            Grid head = new Grid();
            head.Margin = new Thickness(16, 0, 10, 0);
            head.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition headRight = new ColumnDefinition();
            headRight.Width = GridLength.Auto;
            head.ColumnDefinitions.Add(headRight);

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            Border badge = new Border();
            badge.Background = Theme.AccSoft;
            badge.CornerRadius = new CornerRadius(3);
            badge.Padding = new Thickness(6, 1, 6, 1);
            badge.Child = Ui.Text("图片", 10.5, Theme.Acc);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(0, 0, 8, 0);
            left.Children.Add(badge);
            TextBlock title = Ui.Text("图片 " + it.Created.ToString("yyyy-MM-dd HHmmss") + ".png", 12, Theme.Txt2);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(title);
            Grid.SetColumn(left, 0);
            head.Children.Add(left);

            ClickBox close = Ui.IconBtn(Ui.Icon("M4,4 L12,12 M12,4 L4,12", 13, Theme.Txt2, 1.5), "关闭", Close);
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            Border lineTop = new Border();
            lineTop.Height = 1;
            lineTop.Background = Theme.Line;
            lineTop.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(lineTop, 0);
            root.Children.Add(lineTop);

            // 图片
            Border stage = new Border();
            stage.Background = Theme.IsLight ? Theme.Hex("#EEF1F5") : Theme.Hex("#0C0E12");
            stage.Margin = new Thickness(0);
            BitmapImage bmp = Store.LoadBitmap(store.ImagePath(it));
            if (bmp != null)
            {
                Image img = new Image();
                img.Source = bmp;
                img.Stretch = Stretch.Uniform;
                img.Margin = new Thickness(18);
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                stage.Child = img;
            }
            Grid.SetRow(stage, 1);
            root.Children.Add(stage);

            // 底部
            Grid foot = new Grid();
            foot.Margin = new Thickness(16, 0, 16, 0);
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition footRight = new ColumnDefinition();
            footRight.Width = GridLength.Auto;
            foot.ColumnDefinitions.Add(footRight);
            Border lineBottom = new Border();
            lineBottom.Height = 1;
            lineBottom.Background = Theme.Line;
            lineBottom.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(lineBottom, 2);
            root.Children.Add(lineBottom);

            TextBlock meta = Ui.Text(it.W + " × " + it.H + " · " + TextUtil.SizeText(it.Bytes) + " · " + it.Fmt + " · 来自 " + it.App, 11, Theme.Txt3);
            meta.VerticalAlignment = VerticalAlignment.Center;
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            meta.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(meta, 0);
            foot.Children.Add(meta);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.VerticalAlignment = VerticalAlignment.Center;

            ClickBox del = Ui.Btn("删除", false, true, delegate
            {
                bool ok = Ui.Confirm(this, "删除这张图片？", "删除后可以点「撤销」在 3 秒内找回；超过时间就会从历史和磁盘里移除。", "删除", "取消");
                if (!ok) return;
                _store.DeleteItem(it);
                _store.DeleteFilesOnly(it);
                Close();
            });
            del.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(del);

            ClickBox copy = Ui.Btn("仅复制", false, delegate
            {
                _store.SetClipboard(it);
                if (_act != null) _act(it, false);
                Close();
            });
            copy.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(copy);

            ClickBox paste = Ui.Btn("粘贴到当前窗口", true, delegate
            {
                if (_act != null) _act(it, true);
                Close();
            });
            btns.Children.Add(paste);

            foot.Children.Add(btns);
            Grid.SetColumn(btns, 1);
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);

            card.Child = root;
            Content = card;

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Close();
            };
            Loaded += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Native.RoundCorners(hwnd);
                Activate();
            };
        }

        public static void Open(ClipItem it, Store store, AppSettings cfg, Action<ClipItem, bool> act)
        {
            PreviewWindow w = new PreviewWindow(it, store, cfg, act);
            w.Show();
        }
    }
}
