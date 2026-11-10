using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipHelper
{
    /// <summary>统一的轻量控件（不用 ControlTemplate，避免默认系统样式串味）。</summary>
    public class ClickBox : Border
    {
        public Action Clicked;
        public Brush HoverBg;
        public Brush PressBg;
        public bool Silent;
        Brush _baseBg = Brushes.Transparent;

        public ClickBox()
        {
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            _baseBg = Background;          // 记住常态底色，离开时还原，避免主色按钮变透明
            if (HoverBg != null) Background = HoverBg;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Background = _baseBg;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (PressBg != null) Background = PressBg;
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Background = HoverBg != null ? HoverBg : _baseBg;
            if (!Silent && Clicked != null) Clicked();
            e.Handled = true;
        }
    }

    public class ChipBox : ClickBox
    {
        TextBlock _label;
        TextBlock _count;
        bool _active;

        public ChipBox(string text)
        {
            Height = 26;
            CornerRadius = new CornerRadius(13);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(11, 0, 11, 0);
            HoverBg = null;
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;
            _label = Ui.Text(text, 11.5, Theme.Txt2);
            _label.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(_label);
            _count = Ui.Text("", 10, Theme.Txt3);
            _count.Margin = new Thickness(6, 0, 0, 0);
            _count.VerticalAlignment = VerticalAlignment.Center;
            _count.Visibility = Visibility.Collapsed;
            sp.Children.Add(_count);
            Child = sp;
            Apply();
        }

        public void SetLabel(string text)
        {
            _label.Text = text;
        }

        public void SetCount(int n)
        {
            if (n > 0)
            {
                _count.Text = n.ToString();
                _count.Visibility = Visibility.Visible;
            }
            else _count.Visibility = Visibility.Collapsed;
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; Apply(); }
        }

        void Apply()
        {
            if (_active)
            {
                Background = Theme.AccSoft;
                BorderBrush = Brushes.Transparent;
                _label.Foreground = Theme.Acc;
                _label.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                Background = Theme.Chip;
                BorderBrush = Theme.ChipBorder;
                _label.Foreground = Theme.Txt2;
                _label.FontWeight = FontWeights.Normal;
            }
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            if (!_active)
            {
                Background = Theme.Hover;
                _label.Foreground = Theme.Txt;
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            Apply();
        }
    }

    public class SwitchBox : Border
    {
        Ellipse _knob;
        bool _on;
        public Action<bool> Toggled;

        public SwitchBox(bool on)
        {
            Width = 40;
            Height = 20;
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            _knob = new Ellipse();
            _knob.Width = 12;
            _knob.Height = 12;
            _knob.Margin = new Thickness(3, 0, 3, 0);
            _knob.VerticalAlignment = VerticalAlignment.Center;
            Child = _knob;
            _on = on;
            Apply();
        }

        void Apply()
        {
            Background = _on ? Theme.Acc : Theme.SwitchOff;
            BorderBrush = _on ? Brushes.Transparent : Theme.PanelBorder;
            _knob.Fill = _on ? Theme.OnAccentKnob : Brushes.White;
            _knob.HorizontalAlignment = _on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public bool On
        {
            get { return _on; }
            set { _on = value; Apply(); }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _on = !_on;
            Apply();
            if (Toggled != null) Toggled(_on);
            e.Handled = true;
        }
    }

    public class SelectBox : ClickBox
    {
        TextBlock _value;
        Popup _popup;
        public string Value { get; private set; }

        public SelectBox(string value, string[] options, Action<string> onPick)
        {
            Height = 30;
            CornerRadius = new CornerRadius(5);
            BorderThickness = new Thickness(1);
            Background = Theme.Field;
            BorderBrush = Theme.FieldBorder;
            Padding = new Thickness(10, 0, 8, 0);
            HoverBg = null;

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;
            _value = Ui.Text(value, 11.5, Theme.Txt);
            _value.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(_value);
            FrameworkElement chev = Ui.Icon("M1,1 L5,5 L9,1", 9, Theme.Txt3, 1.3);
            chev.Margin = new Thickness(10, 1, 0, 0);
            chev.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(chev);
            Child = sp;
            Value = value;

            Border panel = new Border();
            panel.Background = Theme.Menu;
            panel.BorderBrush = Theme.PanelBorder;
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(6);
            panel.Padding = new Thickness(4);
            panel.Effect = Theme.Shadow(18, 0.35);
            StackPanel list = new StackPanel();
            foreach (string opt in options)
            {
                string captured = opt;
                ClickBox item = new ClickBox();
                item.Height = 30;
                item.CornerRadius = new CornerRadius(4);
                item.Padding = new Thickness(10, 0, 16, 0);
                item.HoverBg = Theme.Hover;
                item.Child = Ui.Text(opt, 11.5, Theme.Txt);
                item.Clicked = delegate
                {
                    Value = captured;
                    _value.Text = captured;
                    _popup.IsOpen = false;
                    if (onPick != null) onPick(captured);
                };
                list.Children.Add(item);
            }
            panel.Child = list;

            _popup = new Popup();
            _popup.AllowsTransparency = true;
            _popup.StaysOpen = false;
            _popup.PlacementTarget = this;
            _popup.Placement = PlacementMode.Bottom;
            _popup.VerticalOffset = 4;
            _popup.Child = panel;

            Clicked = delegate { _popup.IsOpen = !_popup.IsOpen; };
        }
    }

    public static class Ui
    {
        public static TextBlock Text(string s, double size, Brush fg)
        {
            TextBlock t = new TextBlock();
            t.Text = s;
            t.FontSize = size;
            t.Foreground = fg;
            t.TextWrapping = TextWrapping.NoWrap;
            t.FontFamily = Theme.Font;
            return t;
        }

        public static TextBlock Wrap(string s, double size, Brush fg)
        {
            TextBlock t = Text(s, size, fg);
            t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        /// <summary>把 UI 稿里的 SVG path 直接转成 WPF 图标（stroke 风格）。</summary>
        public static FrameworkElement Icon(string data, double size, Brush stroke, double thickness)
        {
            Path p = new Path();
            p.Data = Geometry.Parse(data);
            p.Stroke = stroke;
            p.StrokeThickness = thickness;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.Fill = null;
            Viewbox vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Stretch = Stretch.Uniform;
            vb.Child = p;
            return vb;
        }

        public static FrameworkElement FilledIcon(string data, double size, Brush fill)
        {
            Path p = new Path();
            p.Data = Geometry.Parse(data);
            p.Fill = fill;
            p.Stroke = null;
            Viewbox vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Stretch = Stretch.Uniform;
            vb.Child = p;
            return vb;
        }

        public static ClickBox IconBtn(FrameworkElement icon, string tip, Action click)
        {
            ClickBox b = new ClickBox();
            b.Width = 32;
            b.Height = 32;
            b.CornerRadius = new CornerRadius(6);
            b.HoverBg = Theme.Hover;
            b.PressBg = Theme.Active;
            b.ToolTip = tip;
            icon.Opacity = 0.85;
            b.Child = icon;
            b.Clicked = click;
            return b;
        }

        public static ClickBox Btn(string text, bool primary, Action click)
        {
            return Btn(text, primary, false, click);
        }

        public static ClickBox Btn(string text, bool primary, bool danger, Action click)
        {
            ClickBox b = new ClickBox();
            b.Height = 30;
            b.CornerRadius = new CornerRadius(5);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(12, 0, 12, 0);
            if (primary)
            {
                b.Background = Theme.Acc;
                b.BorderBrush = Brushes.Transparent;
                b.HoverBg = Theme.Acc;
                b.PressBg = Theme.Acc;
                TextBlock t = Text(text, 11.5, Theme.AccFg);
                t.FontWeight = FontWeights.SemiBold;
                b.Child = t;
                b.MouseEnter += delegate { b.Opacity = 0.92; };
                b.MouseLeave += delegate { b.Opacity = 1.0; };
            }
            else
            {
                b.Background = Theme.Btn;
                b.BorderBrush = Theme.Line;
                b.HoverBg = Theme.Hover;
                b.PressBg = Theme.Active;
                TextBlock t = Text(text, 11.5, danger ? Theme.DangerBrush : Theme.Txt);
                b.Child = t;
            }
            b.Clicked = click;
            return b;
        }

        public static Border Divider()
        {
            Border b = new Border();
            b.Height = 1;
            b.Background = Theme.Line;
            b.Margin = new Thickness(0, 8, 0, 8);
            return b;
        }

        public static Border Kbd(string text)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(3);
            b.Background = Theme.Kbd;
            b.BorderBrush = Theme.Line;
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(4, 1, 4, 1);
            b.Margin = new Thickness(0, 0, 2, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            TextBlock t = Text(text, 9.5, Theme.Txt2);
            t.FontFamily = Theme.Mono;
            b.Child = t;
            return b;
        }

        /// <summary>细圆角滚动条（与 UI 稿一致）：注册到窗口资源后作用于窗口内所有滚动条。</summary>
        public static void SlimScroll(Window w)
        {
            string thumb = Theme.IsLight ? "#4A808080" : "#487F7F7F";
            string hover = Theme.IsLight ? "#73808080" : "#737F7F7F";
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
                "<Setter Property='Width' Value='8'/>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ScrollBar'>" +
                "<Grid Background='Transparent'>" +
                "<Track x:Name='PART_Track' IsDirectionReversed='True' Focusable='False'" +
                " Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'" +
                " Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>" +
                "<Track.Thumb><Thumb><Thumb.Template>" +
                "<ControlTemplate TargetType='Thumb'>" +
                "<Border x:Name='bar' CornerRadius='4' Margin='1' Background='" + thumb + "'/>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='bar' Property='Background' Value='" + hover + "'/>" +
                "</Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate></Setter.Value></Setter></Style>";
            try
            {
                Style s = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                w.Resources[typeof(ScrollBar)] = s;
            }
            catch { }
        }

        public static void SetupWindow(Window w, bool shadow)
        {
            w.WindowStyle = WindowStyle.None;
            w.ResizeMode = ResizeMode.NoResize;
            w.Background = Theme.PanelSolid;
            w.FontFamily = Theme.Font;
            w.UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
            if (shadow)
            {
                System.Windows.Shell.WindowChrome chrome = new System.Windows.Shell.WindowChrome();
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
                chrome.GlassFrameThickness = new Thickness(0);
                chrome.CornerRadius = new CornerRadius(0);
                chrome.UseAeroCaptionButtons = false;
                System.Windows.Shell.WindowChrome.SetWindowChrome(w, chrome);
            }
        }

        public static bool Confirm(Window owner, string title, string message, string okText, string cancelText)
        {
            DialogWindow d = new DialogWindow(title, message, okText, cancelText, null);
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            d.ShowDialog();
            return d.Result;
        }

        public static void Info(Window owner, string title, string message)
        {
            DialogWindow d = new DialogWindow(title, message, "知道了", null, null);
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            d.ShowDialog();
        }
    }

    public class DialogWindow : Window
    {
        public bool Result;

        public DialogWindow(string title, string message, string okText, string cancelText, Action<StackPanel> extra)
        {
            Ui.SetupWindow(this, true);
            SizeToContent = SizeToContent.Height;
            Width = 400;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(20, 18, 20, 16);
            StackPanel sp = new StackPanel();

            TextBlock h = Ui.Text(title, 14, Theme.Txt);
            h.FontWeight = FontWeights.SemiBold;
            h.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(h);

            TextBlock body = Ui.Wrap(message, 12, Theme.Txt2);
            body.LineHeight = 19;
            body.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(body);

            if (extra != null) extra(sp);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 18, 0, 0);

            if (!string.IsNullOrEmpty(cancelText))
            {
                ClickBox cancel = Ui.Btn(cancelText, false, delegate { Result = false; Close(); });
                cancel.Margin = new Thickness(0, 0, 8, 0);
                btns.Children.Add(cancel);
            }
            ClickBox ok = Ui.Btn(okText, true, delegate { Result = true; Close(); });
            btns.Children.Add(ok);
            sp.Children.Add(btns);

            card.Child = sp;
            Content = card;

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Result = false; Close(); }
                else if (e.Key == Key.Enter) { Result = true; Close(); }
            };

            Loaded += delegate
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Native.RoundCorners(hwnd);
            };
        }
    }
}
