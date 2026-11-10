using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClipHelper
{
    public static class Program
    {
        static Mutex _mutex;
        static EventWaitHandle _showEvent;

        [STAThread]
        public static void Main()
        {
            bool isNew;
            _mutex = new Mutex(true, "ClipboardHelper.SingleInstance.v1", out isNew);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ClipboardHelper.Show.v1");
            if (!isNew)
            {
                _showEvent.Set();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHelper");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.ExceptionObject + Environment.NewLine);
                }
                catch { }
            };

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppHost host = new AppHost();
            AppHost.Current = host;
            host.Start();

            Thread listener = new Thread(delegate()
            {
                while (true)
                {
                    _showEvent.WaitOne();
                    if (app.Dispatcher.HasShutdownStarted) break;
                    app.Dispatcher.BeginInvoke(new Action(delegate { host.OpenOverlay(); }));
                }
            });
            listener.IsBackground = true;
            listener.Start();

            app.Run();
        }
    }

    public class AppHost
    {
        public static AppHost Current;

        public AppSettings Cfg;
        public Store Store;
        public SyncService Sync;
        OverlayWindow _overlay;
        SettingsWindow _settings;
        TrayIcon _tray;
        HwndSource _msgSource;
        IntPtr _msgHwnd;
        int _hotkeyId = 0x4E01;
        int _captureRetry;
        bool _hotkeyOk;
        DateTime _lastPlaceholderTip;
        DispatcherTimer _clipTimer;

        public void Start()
        {
            System.Diagnostics.Stopwatch startup = System.Diagnostics.Stopwatch.StartNew();
            Cfg = AppSettings.Load();
            Perf.Verbose = Cfg.PerfLog;
            Theme.Apply(IsLightTheme());
            ClipboardApi.Log = WriteDiagLog;

            Store = new Store(Cfg);
            using (Perf.Span("startup.load"))
            {
                Store.Load();
                Store.Enforce();
            }
            Perf.Add("startup.loaded", Store.Items.Count);

            // 局域网同步：只在设置里选了「局域网配对」后才会真正开启监听
            Sync = new SyncService(Cfg, Store);
            Sync.Changed += delegate
            {
                if (_overlay != null && _overlay.IsVisible) _overlay.RefreshFromStore();
            };
            Sync.Start();

            CreateMessageWindow();
            AddClipboardListener();
            RegisterHotkey();

            using (Perf.Span("startup.overlay"))
            {
                _overlay = new OverlayWindow(Store, Cfg);
            }

            _tray = new TrayIcon(Cfg.Paused);
            _tray.OpenRequested += OpenOverlay;
            _tray.SettingsRequested += ShowSettings;
            _tray.GetAutoStart = delegate { return Cfg.AutoStart; };
            _tray.GetTheme = delegate { return Cfg.Theme; };
            _tray.GetCanClear = delegate { return Store != null && Store.Items.Count > 0; };
            _tray.AutoStartChanged += delegate(bool on)
            {
                Cfg.AutoStart = on;
                Cfg.Save();
                SetAutoStart(on);
            };
            _tray.ThemeChanged += delegate(string v)
            {
                if (Cfg.Theme == v) return;
                Cfg.Theme = v;
                Cfg.Save();
                ReloadTheme();
            };
            _tray.PauseRequested += delegate
            {
                Cfg.Paused = !Cfg.Paused;
                Cfg.Save();
                _tray.SetPaused(Cfg.Paused);
                if (_overlay != null) _overlay.RefreshFromStore();
            };
            _tray.ClearRequested += delegate
            {
                bool ok = Ui.Confirm(null, "清空全部历史？", "所有文本与图片记录都会从本机删除，无法恢复。置顶和收藏的内容也会一起清空。", "清空", "取消");
                if (!ok) return;
                Store.ClearAll();
            };
            _tray.ExitRequested += Exit;

            if (Sync != null)
            {
                Sync.PairRequested += delegate(string name)
                {
                    if (_tray != null)
                    {
                        _tray.Balloon("有 1 台新设备请求加入同步", name
                            + " 正在请求加入。打开「设置 → 同步」确认；如果不是你本人操作，请点拒绝并断开所有设备。");
                    }
                };
            }

            if (Cfg.AutoStart) SetAutoStart(true);
            _tray.SetPaused(Cfg.Paused);
            _tray.Balloon("剪贴板助手已开始运行", "按 Alt+V 呼出历史列表；托盘图标里可以暂停记录或打开设置。");

            // 跟随系统模式下，Windows 切换深/浅色时同步刷新
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += delegate(object s, Microsoft.Win32.UserPreferenceChangedEventArgs e)
            {
                if (Cfg.Theme != "auto") return;
                if (e.Category != Microsoft.Win32.UserPreferenceCategory.General
                    && e.Category != Microsoft.Win32.UserPreferenceCategory.Color) return;
                if (Application.Current == null) return;
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate { ReloadTheme(); }));
            };

            Store.Changed += delegate
            {
                if (_overlay != null && _overlay.IsVisible) _overlay.RefreshFromStore();
            };

            Perf.Add("startup.ready", startup.Elapsed.TotalMilliseconds);

            // 启动完成后趁空闲把浮层「预演」一次：Windows 第一次 Show 窗口的固定开销（本机约 1 秒）
            // 挪到这里付掉，用户第一次按 Alt+V 就不用等
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(PrewarmOverlay), DispatcherPriority.ApplicationIdle);
        }

        void PrewarmOverlay()
        {
            if (_overlay == null || _overlay.IsVisible) return;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _overlay.ShowActivated = false;
                _overlay.WindowStartupLocation = WindowStartupLocation.Manual;
                _overlay.Left = -20000;
                _overlay.Top = -20000;
                _overlay.Show();
                _overlay.UpdateLayout();
                _overlay.Hide();
                _overlay.ShowActivated = true;
            }
            catch { }
            Perf.Add("overlay.prewarm", sw.Elapsed.TotalMilliseconds);
        }

        void Exit()
        {
            try { if (Sync != null) Sync.Stop(); } catch { }
            try { Store.Flush(); Cfg.Save(); } catch { }
            try { Native.UnregisterHotKey(_msgHwnd, _hotkeyId); } catch { }
            try { if (_msgHwnd != IntPtr.Zero) Native.RemoveClipboardFormatListener(_msgHwnd); } catch { }
            if (_tray != null) _tray.Dispose();
            Application.Current.Shutdown();
        }

        // ------------------------------------------------------------- 全局窗口消息
        void CreateMessageWindow()
        {
            HwndSourceParameters p = new HwndSourceParameters("ClipboardHelper.Msg");
            p.Width = 0;
            p.Height = 0;
            p.WindowStyle = 0;
            p.ExtendedWindowStyle = 0x00000080;
            p.ParentWindow = new IntPtr(-3); // HWND_MESSAGE
            _msgSource = new HwndSource(p);
            _msgSource.AddHook(WndProc);
            _msgHwnd = _msgSource.Handle;
        }

        void AddClipboardListener()
        {
            try { Native.AddClipboardFormatListener(_msgHwnd); } catch { }
        }

        void RegisterHotkey()
        {
            _hotkeyOk = Native.RegisterHotKey(_msgHwnd, _hotkeyId, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_V);
            if (!_hotkeyOk)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(delegate
                {
                    if (_tray != null) _tray.Balloon("Alt+V 被占用了", "可能被其他软件注册了。可以先通过托盘图标的「打开剪贴板历史」使用。");
                }));
            }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
            {
                OpenOverlay();
                handled = true;
            }
            else if (msg == Native.WM_CLIPBOARDUPDATE)
            {
                // Windows 一次复制可能连发多条 WM_CLIPBOARDUPDATE，先合并再读，避免重复解析同一份内容
                ScheduleClipboard();
                handled = true;
            }
            return IntPtr.Zero;
        }

        static void LogErr(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHelper");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine);
            }
            catch { }
        }

        public void OpenOverlay()
        {
            if (_overlay == null) return;
            if (_settings != null && _settings.IsVisible) _settings.Hide();
            if (!_overlay.IsVisible) _overlay.ShowOverlay();
            else _overlay.HideOverlay();
        }

        public void ShowSettings()
        {
            if (_settings == null)
            {
                _settings = new SettingsWindow(Cfg, Store, this);
                _settings.Closed += delegate { _settings = null; };
            }
            if (!_settings.IsVisible)
            {
                _settings.Show();
            }
            _settings.Activate();
        }

        public void NotifyPauseChanged()
        {
            if (_tray != null) _tray.SetPaused(Cfg.Paused);
        }

        public void ReloadTheme()
        {
            Theme.Apply(IsLightTheme());
            if (_tray != null) _tray.ApplyTheme();
            OverlayWindow old = _overlay;
            _overlay = new OverlayWindow(Store, Cfg);
            if (old != null) old.Close();
            if (_settings != null)
            {
                _settings.Close();
                _settings = null;
            }
            ShowSettings();
        }

        public static bool IsLightTheme()
        {
            // 用户在设置里的选择优先；只有「跟随系统」才读 Windows 设置
            AppSettings cfg = Current != null ? Current.Cfg : null;
            if (cfg != null)
            {
                if (cfg.Theme == "light") return true;
                if (cfg.Theme == "dark") return false;
            }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v != 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("ClipboardHelper", "\"" + System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue("ClipboardHelper", false);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------- 剪贴板采集
        /// <summary>把 90ms 内的多次剪贴板通知合并成一次读取。</summary>
        void ScheduleClipboard()
        {
            if (Cfg.Paused) return;
            if (_clipTimer == null)
            {
                _clipTimer = new DispatcherTimer();
                _clipTimer.Interval = TimeSpan.FromMilliseconds(90);
                _clipTimer.Tick += delegate
                {
                    _clipTimer.Stop();
                    try { HandleClipboard(); }
                    catch (Exception ex) { LogErr(ex); }
                };
            }
            _clipTimer.Stop();
            _clipTimer.Start();
        }

        void HandleClipboard()
        {
            if (Cfg.Paused) return;
            if (DateTime.UtcNow < Store.SuppressUntil) return;
            try
            {
                CaptureClipboard();
                _captureRetry = 0;
            }
            catch (Exception ex)
            {
                LogErr(ex);
                if (ex is COMException && _captureRetry < 3)
                {
                    _captureRetry++;
                    DispatcherTimer t = new DispatcherTimer();
                    t.Interval = TimeSpan.FromMilliseconds(180);
                    t.Tick += delegate
                    {
                        t.Stop();
                        try { HandleClipboard(); } catch { }
                    };
                    t.Start();
                }
            }
        }

        void CaptureClipboard()
        {
            using (Perf.Span("capture.total"))
            {
                ClipboardApi.ReadResult data;
                using (Perf.Span("capture.read")) { data = ClipboardApi.Read(); }
                if (data == null) return;
                LogFormats(data);
                if (data.Excluded) return;
                string app = AppResolver.NameOf(Native.GetForegroundWindow());

                if (data.Image != null)
                {
                    SaveImage(data.Image, app);
                    return;
                }

                if (data.HasText)
                {
                    string text = data.Text;
                    if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;

                    // 只有「[图片]」这类占位文本：先试 OLE 延迟渲染，再取不到就不落盘，避免堆一列「[图片]」
                    if (ClipboardApi.IsPlaceholder(text))
                    {
                        BitmapSource ole = ClipboardApi.TryOleImage();
                        if (ole != null)
                        {
                            LogLine("placeholder 通过 OLE 取到图片 " + ole.PixelWidth + "x" + ole.PixelHeight);
                            SaveImage(ole, app);
                            return;
                        }
                        LogLine("placeholder 未取到图片，已跳过记录");
                        NotifyPlaceholder(text.Trim());
                        return;
                    }

                    if (text.Length > 200000) text = text.Substring(0, 200000);
                    if (Cfg.PasswordNoDisk && TextUtil.LooksLikeSecret(text)) return;
                    using (Perf.Span("capture.text")) { Store.AddText(text, app); }
                }
            }
        }

        /// <summary>占位文字没取到图片时的轻提示：浮层可见就显示在底部，否则每 60 秒最多提示一次托盘气泡。</summary>
        void NotifyPlaceholder(string placeholder)
        {
            string msg = "这条只拿到了「" + placeholder + "」占位文字，没有图片数据，未记录。";
            if (_overlay != null && _overlay.IsVisible)
            {
                _overlay.ShowHint(msg);
                return;
            }
            if ((DateTime.UtcNow - _lastPlaceholderTip).TotalSeconds < 60) return;
            _lastPlaceholderTip = DateTime.UtcNow;
            if (_tray != null) _tray.Balloon("这张图片没能取到图像数据", "剪贴板里只有「" + placeholder + "」占位文字。可以在源程序里右键图片选择「复制图片」，或截图后再复制。");
        }

        void SaveImage(BitmapSource src, string app)
        {
            if (src == null) return;
            // 位图冻结后可安全交给后台线程编码：大图压缩不再卡住界面
            bool frozen = src.IsFrozen;
            if (!frozen)
            {
                try { src.Freeze(); frozen = true; }
                catch { }
            }
            if (!frozen)
            {
                StoreEncodedImage(EncodePng(src), src, app);
                return;
            }

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] png;
                using (Perf.Span("capture.image.encode"))
                {
                    try { png = EncodePng(src); }
                    catch { return; }
                }
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    Perf.Add("capture.image.wait", sw.Elapsed.TotalMilliseconds);
                    StoreEncodedImage(png, src, app);
                }), DispatcherPriority.Background);
            });
        }

        static byte[] EncodePng(BitmapSource src)
        {
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (MemoryStream ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        void StoreEncodedImage(byte[] png, BitmapSource src, string app)
        {
            if (png == null) return;
            if (png.Length > 20 * 1024 * 1024)
            {
                if (_tray != null) _tray.Balloon("图片太大，未记录", "单张图片上限 20 MB。");
                return;
            }
            using (Perf.Span("capture.image.store")) { Store.AddImage(src, png, app, "PNG"); }
        }

        // ------------------------------------------------------------- 诊断日志
        static void LogFormats(ClipboardApi.ReadResult data)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("formats:");
            for (int i = 0; i < data.Formats.Count; i++)
            {
                ClipboardApi.FmtInfo f = data.Formats[i];
                sb.Append(" [" + f.Id);
                if (f.Name.Length > 0) sb.Append(" " + f.Name);
                if (f.Size >= 0) sb.Append(" " + f.Size + "B");
                if (!string.IsNullOrEmpty(f.Magic)) sb.Append(" " + f.Magic);
                sb.Append("]");
            }
            sb.Append(" => image=" + (data.Image == null ? "no" : "yes(" + data.Source + ")"));
            sb.Append(" text=" + (data.HasText ? Trim(data.Text, 40) : "no"));
            if (data.Placeholder) sb.Append(" placeholder=yes");
            LogLine(sb.ToString());
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        static void LogLine(string msg)
        {
            if (ClipboardApi.Log != null) ClipboardApi.Log(msg);
        }

        /// <summary>剪贴板格式诊断日志：记录每次变更时拿到了哪些格式，超过 512KB 自动重开。</summary>
        static void WriteDiagLog(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHelper");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "clipboard-formats.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                File.AppendAllText(file, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
