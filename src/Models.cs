using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace ClipHelper
{
    public class ClipItem
    {
        public string Id;
        public string Kind = "text";      // text | image
        public string Text;               // 文本内容；图片为空
        public string Hash;               // 去重指纹
        public string Image;              // 原图文件名
        public string Thumb;              // 缩略图文件名
        public int W;
        public int H;
        public long Bytes;
        public string Fmt;
        public string App;                // 来源应用
        public string AppColor;
        public DateTime Created;
        public DateTime Modified;         // 最近一次内容/标记变化，用于增量同步
        public int Count = 1;
        public bool Pinned;
        public bool Fav;
        public bool Sensitive;
        public string SensitiveNote;
        public string SourceDevice;       // 来自哪台已配对电脑（本地记录为空）
        public string RemoteId;           // 对方设备上的条目 Id（按需取图时用）

        [XmlIgnore]
        public bool IsImage { get { return Kind == "image"; } }
    }

    public class HistoryData
    {
        public List<ClipItem> Items = new List<ClipItem>();
    }

    /// <summary>已配对的另一台电脑（首发最多 1 台）。Key 是本机与它共享的对称密钥，DPAPI 保护。</summary>
    public class SyncPeer
    {
        public string Id;
        public string Name;
        public string Alias;          // 用户起的名字，只影响显示
        public string Os;
        public string Address;        // 最近一次发现到的 IPv4
        public int Port;
        public string Key;            // DPAPI 保护的 Base64（32 字节）
        public bool Paused;           // 单台暂停收发
        public DateTime LastSyncUtc;
    }

    /// <summary>删除标记：一台删除后另一台 30 天内不复活。</summary>
    public class SyncTombstone
    {
        public string Hash;
        public DateTime DeletedUtc;
    }

    /// <summary>已经应用过的对方复制次数，避免每次同步都重复累加。</summary>
    public class SyncCountMark
    {
        public string Hash;
        public int Count;
    }

    public class AppSettings
    {
        public string Theme = "auto";        // auto | light | dark
        public bool PerfLog;                 // 诊断：把每次耗时写进 perf.log
        public bool AutoStart = true;
        public string DataDir = "";
        public int MaxItems = 1000;
        public int MaxDays = 90;
        public bool Dedup = true;
        public bool ShowCount = true;
        public bool IgnoreSensitive = true;
        public bool PasswordNoDisk = true;
        public bool MaskSensitive = true;
        public bool Paused;
        public List<string> BlockedHashes = new List<string>();

        // ---- 同步（仅本机 / 局域网配对 / 自建服务器 三种方式，共用同一套加密与增量协议）
        public string SyncMode = "local";    // local | lan | server
        public string SyncDeviceId = "";     // 本机设备标识（首次运行生成）
        public string SyncDeviceName = "";   // 默认取 Windows 计算机名
        public bool SyncPaused;              // 整体暂停同步（不等于解绑）
        public string SyncKey = "";          // 与对方共享的配对密钥（DPAPI 保护）
        public string SyncRelayDir = "";     // 共享文件夹（网盘同步目录），用于跨网络配对与后续中转同步
        public List<SyncPeer> SyncPeers = new List<SyncPeer>();
        public List<SyncTombstone> SyncDeleted = new List<SyncTombstone>();
        public List<SyncCountMark> SyncCounts = new List<SyncCountMark>();

        // ---- 自建同步服务器（密文中转）
        public string SyncServerHost = "";           // 服务器地址：IP 或域名
        public int SyncServerPort = 47820;           // 服务器端口
        public bool SyncServerHttps;                 // 用 HTTPS（可选，内容本身已端到端加密）
        public bool SyncServerAllowSelfSigned;       // 允许自签名证书
        public string SyncServerRoomId = "";         // 服务器上的配对房间（随机串，不含密钥）
        public string SyncServerToken = "";          // 本机在服务器上的访问令牌（不是解密密钥）
        public string SyncServerCode = "";           // 本机生成的 6 位配对码（第一台用）
        public DateTime SyncServerCodeExpireUtc;
        public string SyncServerPeerId = "";         // 服务器另一台设备的标识
        public bool SyncServerOwner;                 // 本机是这间配对房间的发起方（负责看"待确认"）
        public long SyncServerSeq;                   // 已确认收到的最大序号（长轮询游标）

        [XmlIgnore]
        public string SettingsPathOverride;   // 仅测试工具使用：把设置写到别处，避免影响真实安装

        [XmlIgnore]
        public static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardHelper");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.xml");
            }
        }

        public static string DefaultDataDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHelper");
            }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    using (FileStream fs = File.OpenRead(SettingsPath))
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(AppSettings));
                        s = (AppSettings)ser.Deserialize(fs);
                    }
                }
            }
            catch { s = new AppSettings(); }

            if (s == null) s = new AppSettings();
            if (s.BlockedHashes == null) s.BlockedHashes = new List<string>();
            if (string.IsNullOrEmpty(s.DataDir)) s.DataDir = DefaultDataDir;
            if (s.SyncPeers == null) s.SyncPeers = new List<SyncPeer>();
            if (s.SyncDeleted == null) s.SyncDeleted = new List<SyncTombstone>();
            if (s.SyncCounts == null) s.SyncCounts = new List<SyncCountMark>();
            if (string.IsNullOrEmpty(s.SyncMode)) s.SyncMode = "local";
            if (s.SyncServerPort <= 0) s.SyncServerPort = 47820;
            if (s.SyncServerHost == null) s.SyncServerHost = "";
            if (s.SyncServerRoomId == null) s.SyncServerRoomId = "";
            if (s.SyncServerToken == null) s.SyncServerToken = "";
            if (s.SyncServerCode == null) s.SyncServerCode = "";
            if (s.SyncServerPeerId == null) s.SyncServerPeerId = "";
            return s;
        }

        public void Save()
        {
            lock (SaveLock)
            {
                try
                {
                    string path = SettingsPath;
                    if (!string.IsNullOrEmpty(SettingsPathOverride))
                    {
                        path = SettingsPathOverride;
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    }
                    using (FileStream fs = File.Create(path))
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(AppSettings));
                        ser.Serialize(fs, this);
                    }
                }
                catch { }
            }
        }

        static readonly object SaveLock = new object();
    }

    public static class TextUtil
    {
        public static string Normalize(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char raw in text)
            {
                char c = raw;
                if (c == '\u3000') c = ' ';
                else if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0);
                if (char.IsWhiteSpace(c)) c = ' ';
                sb.Append(c);
            }
            string s = Regex.Replace(sb.ToString(), " +", " ").Trim();
            return s.ToLowerInvariant();
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>形似密码的短随机串：不落盘、不显示。</summary>
        public static bool LooksLikeSecret(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.Length < 8 || s.Length > 48) return false;
            if (Regex.IsMatch(s, @"\s")) return false;
            if (Regex.IsMatch(s, @"[\u4e00-\u9fa5]")) return false;
            if (Regex.IsMatch(s, @"^(https?://|www\.)", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return false;
            if (s.StartsWith("/") || s.StartsWith("\\") || s.Contains(":\\")) return false;

            int kinds = 0;
            if (Regex.IsMatch(s, "[a-z]")) kinds++;
            if (Regex.IsMatch(s, "[A-Z]")) kinds++;
            if (Regex.IsMatch(s, "[0-9]")) kinds++;
            if (Regex.IsMatch(s, @"[^a-zA-Z0-9]")) kinds++;
            if (kinds < 3) return false;
            if (s.Length < 10) return false;
            return true;
        }

        public static string DetectSensitive(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string s = text.Trim();
            if (Regex.IsMatch(s, @"^\d{17}[\dXx]$")) return "疑似身份证号";
            if (Regex.IsMatch(s, @"^[\d\s\-]{13,25}$") && Regex.Replace(s, @"\D", "").Length >= 13) return "疑似银行卡号";
            if (Regex.IsMatch(s, @"-----BEGIN [A-Z ]*PRIVATE KEY-----")) return "疑似密钥";
            if (Regex.IsMatch(s, @"^(sk-|AKIA)[A-Za-z0-9]{16,}")) return "疑似密钥";
            return null;
        }

        public static string Mask(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim();
            string digits = Regex.Replace(s, @"\D", "");
            if (digits.Length >= 13 && Regex.IsMatch(s, @"^[\d\s\-]{13,25}$"))
            {
                return s.Substring(0, Math.Min(4, s.Length)) + " •••• •••• " + s.Substring(Math.Max(0, s.Length - 4));
            }
            if (s.Length > 12)
            {
                return s.Substring(0, 4) + " •••••• " + s.Substring(s.Length - 4);
            }
            return "••••••";
        }

        public static string RelativeTime(DateTime t)
        {
            DateTime now = DateTime.Now;
            TimeSpan d = now - t;
            if (d.TotalSeconds < 0) return "刚刚";
            if (d.TotalSeconds < 60) return "刚刚";
            if (d.TotalMinutes < 60) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
            if (t.Date == now.Date) return t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.ToString("M月d日");
        }

        public static string SizeText(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##") + " GB";
        }
    }

    public static class AppResolver
    {
        static Dictionary<uint, string> _names = new Dictionary<uint, string>();

        static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "msedge", "Microsoft Edge" },
            { "chrome", "Google Chrome" },
            { "firefox", "Firefox" },
            { "Code", "Visual Studio Code" },
            { "WINWORD", "Word" },
            { "EXCEL", "Excel" },
            { "POWERPNT", "PowerPoint" },
            { "OUTLOOK", "Outlook" },
            { "WeChat", "微信" },
            { "Weixin", "微信" },
            { "Feishu", "飞书" },
            { "Lark", "飞书" },
            { "WXWork", "企业微信" },
            { "notepad", "记事本" },
            { "explorer", "文件资源管理器" },
            { "SnippingTool", "截图工具" },
            { "ScreenClippingHost", "截图工具" },
            { "ApplicationFrameHost", "Windows 应用" },
            { "WindowsTerminal", "终端" },
            { "powershell", "PowerShell" },
            { "pwsh", "PowerShell" },
            { "notepad++", "Notepad++" },
            { "Figma", "Figma" },
            { "Notion", "Notion" },
            { "OneNote", "OneNote" },
            { "Telegram", "Telegram" },
            { "QQ", "QQ" },
            { "DingTalk", "钉钉" },
        };

        public static string NameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "未知应用";
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return "未知应用";
            if (_names.ContainsKey(pid)) return _names[pid];

            string name = null;
            try
            {
                Process p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
                if (string.Equals(name, "ClipboardHelper", StringComparison.OrdinalIgnoreCase)) name = "剪贴板助手";
                else if (Known.ContainsKey(name)) name = Known[name];
                else
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrEmpty(fvi.FileDescription)) name = fvi.FileDescription.Trim();
                    }
                    catch { }
                }
            }
            catch { name = "未知应用"; }

            if (string.IsNullOrEmpty(name)) name = "未知应用";
            if (_names.Count > 300) _names.Clear();
            _names[pid] = name;
            return name;
        }

        public static string ColorOf(string app)
        {
            if (string.IsNullOrEmpty(app)) return Theme.AppPalette[9];
            int h = 0;
            foreach (char c in app) h = (h * 31 + c) & 0x7fffffff;
            return Theme.AppPalette[h % Theme.AppPalette.Length];
        }
    }

    public class Store
    {
        readonly AppSettings _cfg;
        DispatcherTimer _saveTimer;

        public List<ClipItem> Items = new List<ClipItem>();
        public string Dir;
        public DateTime SuppressUntil = DateTime.MinValue;
        public event Action Changed;
        public event Action<ClipItem> Deleted;    // 用户主动删除（用于把删除动作同步给对方）
        public event Action<ClipItem> Restored;   // 撤销删除

        public Store(AppSettings cfg)
        {
            _cfg = cfg;
            Dir = cfg.DataDir;
        }

        public string ImagesDir { get { return Path.Combine(Dir, "images"); } }
        public string ThumbsDir { get { return Path.Combine(Dir, "thumbs"); } }

        void EnsureDirs()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Directory.CreateDirectory(ImagesDir);
                Directory.CreateDirectory(ThumbsDir);
            }
            catch { }
        }

        public void Load()
        {
            Items.Clear();
            EnsureDirs();
            string file = Path.Combine(Dir, "history.dat");
            if (File.Exists(file))
            {
                try
                {
                    using (Perf.Span("store.load.read"))
                    {
                        byte[] enc = File.ReadAllBytes(file);
                        byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                        using (MemoryStream ms = new MemoryStream(raw))
                        {
                            XmlSerializer ser = new XmlSerializer(typeof(HistoryData));
                            HistoryData data = (HistoryData)ser.Deserialize(ms);
                            if (data != null && data.Items != null) Items.AddRange(data.Items);
                        }
                    }
                }
                catch (Exception)
                {
                    try { File.Move(file, file + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss")); }
                    catch { }
                }
            }
            foreach (ClipItem it in Items)
            {
                if (it.AppColor == null) it.AppColor = AppResolver.ColorOf(it.App);
                if (it.Modified == DateTime.MinValue) it.Modified = it.Created;
            }
            Items = Items.OrderByDescending(i => i.Created).ToList();
        }

        public long LastPayloadBytes;
        public long LastFileBytes;
        public double LastSaveMs;

        public void Save()
        {
            SaveSnapshot(new List<ClipItem>(Items));
        }

        /// <summary>把整份历史序列化+加密+写盘。始终在后台线程跑，避免卡界面。</summary>
        void SaveSnapshot(List<ClipItem> snapshot)
        {
            EnsureDirs();
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                HistoryData data = new HistoryData();
                data.Items = snapshot;
                byte[] raw;
                using (MemoryStream ms = new MemoryStream())
                {
                    XmlSerializer ser = new XmlSerializer(typeof(HistoryData));
                    ser.Serialize(ms, data);
                    raw = ms.ToArray();
                }
                byte[] enc;
                using (Perf.Span("store.encrypt"))
                {
                    enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                }
                string file = Path.Combine(Dir, "history.dat");
                string tmp = file + ".tmp";
                File.WriteAllBytes(tmp, enc);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                sw.Stop();
                LastPayloadBytes = raw.Length;
                LastFileBytes = enc.Length;
                LastSaveMs = sw.Elapsed.TotalMilliseconds;
                Perf.Add("store.save", LastSaveMs);
            }
            catch { }
        }

        public void SaveSoon()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer();
                _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
                _saveTimer.Tick += delegate { _saveTimer.Stop(); SaveInBackground(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>后台落盘：UI 线程只做一次引用拷贝，序列化/加密/写文件都在线程池里。</summary>
        void SaveInBackground()
        {
            List<ClipItem> snapshot = new List<ClipItem>(Items);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { SaveSnapshot(snapshot); });
        }

        public void Flush()
        {
            if (_saveTimer != null) _saveTimer.Stop();
            Save();
        }

        void Notify()
        {
            if (Changed != null) Changed();
        }

        public ClipItem FindByHash(string hash)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Hash == hash) return Items[i];
            }
            return null;
        }

        public bool IsBlocked(string hash)
        {
            return _cfg.BlockedHashes != null && _cfg.BlockedHashes.Contains(hash);
        }

        public ClipItem AddText(string text, string app)
        {
            string norm = TextUtil.Normalize(text);
            if (string.IsNullOrEmpty(norm)) return null;
            string hash = "t:" + TextUtil.Sha256(Encoding.UTF8.GetBytes(norm));
            if (IsBlocked(hash)) return null;

            ClipItem existing = _cfg.Dedup ? FindByHash(hash) : null;
            if (existing != null)
            {
                // 同一次复制的多次系统通知，不重复累加
                if ((DateTime.Now - existing.Created).TotalMilliseconds < 1200) return existing;
                existing.Created = DateTime.Now;
                existing.Modified = DateTime.Now;
                existing.Count = existing.Count + 1;
                existing.App = app;
                existing.AppColor = AppResolver.ColorOf(app);
                Items.Remove(existing);
                Items.Insert(0, existing);
                SaveSoon();
                Notify();
                return existing;
            }

            ClipItem it = new ClipItem();
            it.Id = Guid.NewGuid().ToString("N");
            it.Kind = "text";
            it.Text = text;
            it.Hash = hash;
            it.App = app;
            it.AppColor = AppResolver.ColorOf(app);
            it.Created = DateTime.Now;
            it.Modified = DateTime.Now;
            it.Count = 1;
            string note = TextUtil.DetectSensitive(text);
            if (note != null) { it.Sensitive = true; it.SensitiveNote = note; }

            Items.Insert(0, it);
            Enforce();
            SaveSoon();
            Notify();
            return it;
        }

        public ClipItem AddImage(BitmapSource src, byte[] png, string app, string fmt)
        {
            string hash = "i:" + TextUtil.Sha256(png);
            if (IsBlocked(hash)) return null;

            ClipItem existing = _cfg.Dedup ? FindByHash(hash) : null;
            if (existing != null)
            {
                if ((DateTime.Now - existing.Created).TotalMilliseconds < 1200) return existing;
                existing.Created = DateTime.Now;
                existing.Modified = DateTime.Now;
                existing.Count = existing.Count + 1;
                existing.App = app;
                existing.AppColor = AppResolver.ColorOf(app);
                Items.Remove(existing);
                Items.Insert(0, existing);
                SaveSoon();
                Notify();
                return existing;
            }

            ClipItem it = new ClipItem();
            it.Id = Guid.NewGuid().ToString("N");
            it.Kind = "image";
            it.Hash = hash;
            it.App = app;
            it.AppColor = AppResolver.ColorOf(app);
            it.Created = DateTime.Now;
            it.Modified = DateTime.Now;
            it.Count = 1;
            it.W = src.PixelWidth;
            it.H = src.PixelHeight;
            it.Bytes = png.Length;
            it.Fmt = fmt;
            it.Image = it.Id + ".png";
            it.Thumb = it.Id + ".png";

            try
            {
                EnsureDirs();
                using (Perf.Span("store.image.write"))
                {
                    File.WriteAllBytes(Path.Combine(ImagesDir, it.Image), png);
                }
                using (Perf.Span("store.image.thumb"))
                {
                    BitmapSource thumb = MakeThumb(src, 140);
                    PngBitmapEncoder enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(thumb));
                    string tp = Path.Combine(ThumbsDir, it.Thumb);
                    using (FileStream fs = File.Create(tp))
                    {
                        enc.Save(fs);
                    }
                    // 刚生成的缩略图直接进内存缓存，第一次打开列表就不用再解码
                    PutThumbCache(tp, thumb);
                }
            }
            catch
            {
                return null;
            }

            Items.Insert(0, it);
            Enforce();
            SaveSoon();
            Notify();
            return it;
        }

        public static BitmapSource MakeThumb(BitmapSource src, int max)
        {
            double scale = Math.Min(1.0, (double)max / Math.Max(src.PixelWidth, src.PixelHeight));
            if (scale >= 1.0) return src;
            TransformedBitmap tb = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            tb.Freeze();
            return tb;
        }

        public string ImagePath(ClipItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Image)) return null;
            return Path.Combine(ImagesDir, it.Image);
        }

        public string ThumbPath(ClipItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Thumb)) return null;
            return Path.Combine(ThumbsDir, it.Thumb);
        }

        public void TouchPinned(ClipItem it, bool pinned)
        {
            it.Pinned = pinned;
            it.Modified = DateTime.Now;
            SaveSoon();
            Notify();
        }

        public void TouchFav(ClipItem it, bool fav)
        {
            it.Fav = fav;
            it.Modified = DateTime.Now;
            SaveSoon();
            Notify();
        }

        public void DeleteItem(ClipItem it)
        {
            Items.Remove(it);
            SaveSoon();
            Notify();
            if (Deleted != null) Deleted(it);
        }

        public void RestoreItem(ClipItem it, int index)
        {
            if (index < 0 || index > Items.Count) index = 0;
            Items.Insert(index, it);
            SaveSoon();
            Notify();
            if (Restored != null) Restored(it);
        }

        /// <summary>本机是否已经有这张图片的原图（远端条目只有索引，没有文件）。</summary>
        public bool HasLocalImage(ClipItem it)
        {
            if (it == null || !it.IsImage) return false;
            string p = ImagePath(it);
            return !string.IsNullOrEmpty(p) && File.Exists(p);
        }

        /// <summary>把按需拉取到的原图落到本机，并生成缩略图。</summary>
        public bool AttachImage(ClipItem it, byte[] png)
        {
            if (it == null || !it.IsImage || png == null || png.Length == 0) return false;
            try
            {
                EnsureDirs();
                if (string.IsNullOrEmpty(it.Image)) it.Image = it.Id + ".png";
                if (string.IsNullOrEmpty(it.Thumb)) it.Thumb = it.Id + ".png";
                File.WriteAllBytes(Path.Combine(ImagesDir, it.Image), png);

                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.StreamSource = new MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();

                BitmapSource thumb = MakeThumb(bmp, 140);
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(thumb));
                string tp = Path.Combine(ThumbsDir, it.Thumb);
                using (FileStream fs = File.Create(tp)) { enc.Save(fs); }
                PutThumbCache(tp, thumb);

                it.Bytes = png.Length;
                it.W = bmp.PixelWidth;
                it.H = bmp.PixelHeight;
                it.Modified = DateTime.Now;
                SaveSoon();
                Notify();
                return true;
            }
            catch { return false; }
        }

        /// <summary>把来自另一台电脑的条目并入本地历史（同哈希合并，保持时间倒序）。</summary>
        public void IngestRemote(ClipItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Hash)) return;
            int pos = 0;
            while (pos < Items.Count && Items[pos].Created >= it.Created) pos++;
            Items.Insert(pos, it);
            SaveSoon();
            Notify();
        }

        /// <summary>远端删除：本机手动置顶/收藏过的不删。</summary>
        public bool RemoveByHash(string hash)
        {
            ClipItem it = FindByHash(hash);
            if (it == null) return false;
            if (it.Pinned || it.Fav) return false;
            Items.Remove(it);
            DeleteFilesOnly(it);
            SaveSoon();
            Notify();
            return true;
        }

        /// <summary>同步服务合并远端状态后统一通知界面。</summary>
        public void NotifyChanged()
        {
            SaveSoon();
            Notify();
        }

        public void DeleteFilesOnly(ClipItem it)
        {
            if (it == null || !it.IsImage) return;
            try { if (!string.IsNullOrEmpty(it.Image)) File.Delete(Path.Combine(ImagesDir, it.Image)); } catch { }
            try { if (!string.IsNullOrEmpty(it.Thumb)) File.Delete(Path.Combine(ThumbsDir, it.Thumb)); } catch { }
        }

        public long TotalBytes()
        {
            long total = 0;
            try
            {
                foreach (ClipItem it in Items)
                {
                    if (it.IsImage) total += it.Bytes;
                    else if (it.Text != null) total += it.Text.Length * 2;
                }
            }
            catch { }
            return total;
        }

        public void BlockHash(ClipItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Hash)) return;
            if (_cfg.BlockedHashes == null) _cfg.BlockedHashes = new List<string>();
            if (!_cfg.BlockedHashes.Contains(it.Hash)) _cfg.BlockedHashes.Add(it.Hash);
            if (_cfg.BlockedHashes.Count > 500) _cfg.BlockedHashes.RemoveAt(0);
            DeleteItem(it);
            DeleteFilesOnly(it);
            _cfg.Save();
        }

        public void Enforce()
        {
            DateTime now = DateTime.Now;
            List<ClipItem> keep = new List<ClipItem>();
            List<ClipItem> removable = new List<ClipItem>();
            foreach (ClipItem it in Items)
            {
                if (it.Pinned || it.Fav) keep.Add(it);
                else removable.Add(it);
            }
            int count = 0;
            DateTime cutoff = _cfg.MaxDays > 0 ? now.AddDays(-_cfg.MaxDays) : DateTime.MinValue;
            List<ClipItem> drop = new List<ClipItem>();
            foreach (ClipItem it in removable)
            {
                count++;
                if (_cfg.MaxItems > 0 && count > _cfg.MaxItems) drop.Add(it);
                else if (_cfg.MaxDays > 0 && it.Created < cutoff) drop.Add(it);
            }
            foreach (ClipItem it in drop)
            {
                Items.Remove(it);
                DeleteFilesOnly(it);
            }
        }

        public void ClearAll()
        {
            foreach (ClipItem it in Items.ToList()) DeleteFilesOnly(it);
            Items.Clear();
            DropThumbCache();
            Save();
            Notify();
        }

        public void ClearImages()
        {
            foreach (ClipItem it in Items.Where(i => i.IsImage).ToList())
            {
                DeleteFilesOnly(it);
                Items.Remove(it);
            }
            DropThumbCache();
            Save();
            Notify();
        }

        public void SetClipboard(ClipItem it)
        {
            SuppressUntil = DateTime.UtcNow.AddMilliseconds(900);
            try
            {
                if (it == null) return;
                if (it.IsImage)
                {
                    BitmapImage bmp = LoadBitmap(ImagePath(it));
                    if (bmp != null) ClipboardApi.SetImage(bmp);
                }
                else if (!string.IsNullOrEmpty(it.Text))
                {
                    ClipboardApi.SetText(it.Text);
                }
            }
            catch { }
        }

        public static BitmapImage LoadBitmap(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // 缩略图内存缓存：切日期/重开浮层时不再重复解码同一张缩略图
        static readonly object _thumbLock = new object();
        static readonly Dictionary<string, BitmapSource> _thumbCache = new Dictionary<string, BitmapSource>();

        public static BitmapSource LoadThumbCached(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_thumbLock)
            {
                BitmapSource hit;
                if (_thumbCache.TryGetValue(path, out hit)) return hit;
            }
            BitmapSource bmp = null;
            try
            {
                BitmapImage img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 140;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                bmp = img;
            }
            catch { return null; }
            lock (_thumbLock)
            {
                if (_thumbCache.Count > 400) _thumbCache.Clear();
                _thumbCache[path] = bmp;
            }
            return bmp;
        }

        public static void PutThumbCache(string path, BitmapSource bmp)
        {
            if (string.IsNullOrEmpty(path) || bmp == null) return;
            try { if (!bmp.IsFrozen) bmp.Freeze(); } catch { }
            lock (_thumbLock)
            {
                if (_thumbCache.Count > 400) _thumbCache.Clear();
                _thumbCache[path] = bmp;
            }
        }

        public static void DropThumbCache()
        {
            lock (_thumbLock) { _thumbCache.Clear(); }
        }

        public void Reopen(string newDir)
        {
            Flush();
            Dir = newDir;
            Load();
            Notify();
        }
    }
}
