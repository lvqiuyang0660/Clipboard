using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClipHelper
{
    public enum UpdateDecision { Later, Apply, Skip }

    /// <summary>更新说明弹窗：列出本次更新内容，由用户决定是否下载；结果也在这里展示。</summary>
    public class UpdateWindow : Window
    {
        public UpdateDecision Decision = UpdateDecision.Later;

        AppSettings _cfg;
        UpdateInfo _info;
        StackPanel _status;
        StackPanel _buttons;
        bool _busy;

        public UpdateWindow(AppSettings cfg, UpdateInfo info)
        {
            _cfg = cfg;
            _info = info;

            Ui.SetupWindow(this, true);
            Width = 470;
            SizeToContent = SizeToContent.Height;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Border card = new Border();
            card.Background = Theme.PanelSolid;
            card.BorderBrush = Theme.PanelBorder;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(20, 18, 20, 16);

            StackPanel sp = new StackPanel();

            TextBlock head = Ui.Text("发现新版本  v" + info.Version, 14, Theme.Txt);
            head.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(head);

            TextBlock sub = Ui.Text("当前 v" + BuildInfo.AppVersion + "   ·   " + info.Date
                + "   ·   " + SizeText(info.Size), 11, Theme.Txt2);
            sub.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(sub);

            Border div = Ui.Divider();
            div.Margin = new Thickness(0, 14, 0, 12);
            sp.Children.Add(div);

            sp.Children.Add(Ui.Text("更新内容", 12, Theme.Txt2));
            if (info.Notes.Count == 0)
            {
                TextBlock none = Ui.Wrap("·  本次未附带更新说明", 12, Theme.Txt);
                none.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(none);
            }
            else
            {
                for (int i = 0; i < info.Notes.Count; i++)
                {
                    TextBlock n = Ui.Wrap("·  " + info.Notes[i], 12, Theme.Txt);
                    n.LineHeight = 19;
                    n.Margin = new Thickness(0, 8, 0, 0);
                    sp.Children.Add(n);
                }
            }

            _status = new StackPanel();
            _status.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(_status);

            _buttons = new StackPanel();
            _buttons.Orientation = Orientation.Horizontal;
            _buttons.HorizontalAlignment = HorizontalAlignment.Right;
            _buttons.Margin = new Thickness(0, 18, 0, 0);

            ClickBox skip = Ui.Btn("跳过此版本", false, delegate
            {
                Decision = UpdateDecision.Skip;
                try
                {
                    if (_cfg != null) { _cfg.SkippedVersion = _info.Version; _cfg.Save(); }
                }
                catch { }
                Close();
            });
            skip.Margin = new Thickness(0, 0, 8, 0);
            _buttons.Children.Add(skip);

            ClickBox later = Ui.Btn("稍后提醒", false, delegate
            {
                Decision = UpdateDecision.Later;
                Close();
            });
            later.Margin = new Thickness(0, 0, 8, 0);
            _buttons.Children.Add(later);

            ClickBox apply = Ui.Btn("立即更新", true, delegate { BeginApply(); });
            _buttons.Children.Add(apply);
            sp.Children.Add(_buttons);

            card.Child = sp;
            Content = card;
            Ui.EnableDrag(this, card, 56);

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && !_busy) { Decision = UpdateDecision.Later; Close(); }
            };
        }

        void BeginApply()
        {
            if (_busy) return;
            _busy = true;
            for (int i = 0; i < _buttons.Children.Count; i++)
            {
                ClickBox b = _buttons.Children[i] as ClickBox;
                if (b != null) b.IsEnabled = false;
            }
            _status.Children.Clear();
            _status.Children.Add(Ui.Wrap("正在下载并校验…", 12, Theme.Txt2));

            UpdateService.ApplyAsync(_info, delegate(UpdateApplyResult r)
            {
                Decision = UpdateDecision.Apply;
                _status.Children.Clear();
                TextBlock msg = Ui.Wrap(r.Message, 12, r.Ok ? Theme.Txt : Theme.DangerBrush);
                msg.LineHeight = 19;
                _status.Children.Add(msg);

                if (r.Ok)
                {
                    ClickBox open = Ui.Btn("打开所在文件夹", false, delegate
                    {
                        try { Process.Start("explorer.exe", "/select,\"" + r.SavedPath + "\""); }
                        catch { }
                    });
                    open.HorizontalAlignment = HorizontalAlignment.Left;
                    open.Margin = new Thickness(0, 10, 0, 0);
                    _status.Children.Add(open);
                }

                _buttons.Children.Clear();
                ClickBox done = Ui.Btn("知道了", true, delegate { Close(); });
                _buttons.Children.Add(done);
            });
        }

        static string SizeText(long n)
        {
            if (n >= 1024 * 1024)
                return (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (n / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }
    }
}
