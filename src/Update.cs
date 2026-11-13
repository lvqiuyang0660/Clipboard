using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClipHelper
{
    public class UpdateInfo
    {
        public string Version = "";
        public string Date = "";
        public List<string> Notes = new List<string>();
        public string Url = "";
        public string Sha256 = "";
        public long Size;
        public string Source = "";
        public bool HadPrefixNoise;
    }

    public class UpdateCheckResult
    {
        public bool Ok;
        public bool HasUpdate;
        public string Message = "";
        public UpdateInfo Info;
    }

    public class UpdateApplyResult
    {
        public bool Ok;
        public string Message = "";
        public string SavedPath = "";
    }

    /// <summary>
    /// P0：检查更新 → 展示更新内容 → 用户确认 → 下载并三重校验 → 保存到本地。
    /// 本阶段不替换正在运行的 exe；任何一步异常都保留当前版本，绝不写入未校验的文件。
    /// </summary>
    public static class UpdateService
    {
        public const string DefaultSource = "http://8.163.124.237:47820/v1/update/latest";

        const int ManifestTimeoutMs = 8000;
        const int PackageTimeoutMs = 30000;
        const long MaxResponseBytes = 32L * 1024 * 1024;

        static readonly object Gate = new object();
        static UpdateInfo _pending;
        static DispatcherTimer _timer;
        static AppSettings _cfg;
        static TrayIcon _tray;
        static volatile bool _checking;

        public static UpdateInfo Pending
        {
            get { lock (Gate) { return _pending; } }
            set { lock (Gate) { _pending = value; } }
        }

        public static string SourceOf(AppSettings cfg)
        {
            string s = cfg == null ? "" : cfg.UpdateSource;
            return string.IsNullOrWhiteSpace(s) ? DefaultSource : s.Trim();
        }

        public static void StartWatching(AppSettings cfg, TrayIcon tray)
        {
            Stop();
            if (cfg == null || !cfg.UpdateCheckEnabled) return;
            _cfg = cfg;
            _tray = tray;
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(20);
            _timer.Tick += delegate
            {
                _timer.Interval = TimeSpan.FromHours(24);
                CheckSilent();
            };
            _timer.Start();
        }

        public static void Stop()
        {
            if (_timer != null) { try { _timer.Stop(); } catch { } }
            _timer = null;
        }

        static void CheckSilent()
        {
            if (_checking || _cfg == null) return;
            _checking = true;
            CheckAsync(_cfg, delegate(UpdateCheckResult r)
            {
                _checking = false;
                try { _cfg.LastUpdateCheckUtc = DateTime.UtcNow; _cfg.Save(); } catch { }
                if (!r.Ok) return;
                if (!r.HasUpdate || r.Info == null) { Pending = null; return; }
                Pending = r.Info;
                if (_tray != null)
                {
                    _tray.Balloon("发现新版本 v" + r.Info.Version,
                        "托盘菜单或「设置 → 关于」里点「检查更新」，可查看本次更新内容。");
                }
            });
        }

        /// <summary>用户主动点「检查更新」：优先复用自动检查缓存的清单，避免重复请求。</summary>
        public static void CheckNow(AppSettings cfg, Window owner)
        {
            if (_checking) { Ui.Info(owner, "正在检查", "上一次检查还没结束，请稍等片刻。"); return; }
            UpdateInfo cached = Pending;
            if (cached != null)
            {
                if (cfg != null && string.Equals(cached.Version, cfg.SkippedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Pending = null;
                    Ui.Info(owner, "已是最新版本", "v" + cached.Version + " 已被跳过，不会再提醒。");
                    return;
                }
                ShowWindow(cfg, owner, cached);
                return;
            }

            _checking = true;
            CheckAsync(cfg, delegate(UpdateCheckResult r)
            {
                _checking = false;
                try { cfg.LastUpdateCheckUtc = DateTime.UtcNow; cfg.Save(); } catch { }
                if (!r.Ok) { Ui.Info(owner, "检查更新失败", r.Message); return; }
                if (!r.HasUpdate) { Ui.Info(owner, "已是最新版本", "当前 v" + BuildInfo.AppVersion + "，没有可用的新版本。"); return; }
                Pending = r.Info;
                ShowWindow(cfg, owner, r.Info);
            });
        }

        static void ShowWindow(AppSettings cfg, Window owner, UpdateInfo info)
        {
            try
            {
                UpdateWindow w = new UpdateWindow(cfg, info);
                if (owner != null && owner.IsVisible)
                {
                    w.Owner = owner;
                    w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                w.ShowDialog();
            }
            catch { }
        }

        static void CheckAsync(AppSettings cfg, Action<UpdateCheckResult> done)
        {
            string source = SourceOf(cfg);
            string skipped = cfg == null ? "" : cfg.SkippedVersion;
            Thread t = new Thread(delegate()
            {
                UpdateCheckResult r = Check(source, skipped);
                Invoke(done, r);
            });
            t.IsBackground = true;
            t.Start();
        }

        static UpdateCheckResult Check(string source, string skipped)
        {
            UpdateCheckResult fail = new UpdateCheckResult();
            byte[] raw;
            string err;
            if (!HttpGet(source, ManifestTimeoutMs, out raw, out err))
            {
                fail.Message = "连不上更新源：" + err + "\n\n当前版本未改动，可稍后再试。";
                return fail;
            }

            // 企业出网策略有时会在响应前追加头部。只取 JSON 主体参加解析，
            // 但字段不全即判失败；exe 本身的校验不做任何容错。
            string text = Encoding.UTF8.GetString(raw).TrimStart('\uFEFF');
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                fail.Message = "更新清单不是有效 JSON（可能被网络策略改写），已保留当前版本。";
                return fail;
            }
            Dictionary<string, object> o = Json.ParseObject(text.Substring(start, end - start + 1));
            if (o == null)
            {
                fail.Message = "更新清单无法解析（可能被网络策略改写），已保留当前版本。";
                return fail;
            }

            string ver = Json.GetString(o, "version");
            string url = Json.GetString(o, "url");
            string sha = Json.GetString(o, "sha256");
            long size = Json.GetLong(o, "size", 0);
            if (!BuildInfo.IsVersionString(ver) || string.IsNullOrEmpty(url)
                || !IsSha256(sha) || size <= 0 || size > MaxResponseBytes)
            {
                fail.Message = "更新清单字段不完整或格式不对（可能被网络策略改写），已保留当前版本。";
                return fail;
            }

            UpdateInfo info = new UpdateInfo();
            info.Version = ver;
            info.Date = Json.GetString(o, "date");
            info.Url = ResolveUrl(source, url);
            info.Sha256 = sha;
            info.Size = size;
            info.Source = source;
            info.HadPrefixNoise = start > 0;
            List<object> notes = Json.GetArray(o, "notes");
            for (int i = 0; i < notes.Count; i++)
            {
                string n = notes[i] as string;
                if (!string.IsNullOrEmpty(n)) info.Notes.Add(n);
            }

            UpdateCheckResult r = new UpdateCheckResult();
            r.Ok = true;
            r.Info = info;
            r.HasUpdate = BuildInfo.IsNewer(ver, BuildInfo.AppVersion)
                          && !string.Equals(ver, skipped, StringComparison.OrdinalIgnoreCase);
            if (!r.HasUpdate) r.Message = "已是最新版本，或该版本已被跳过。";
            return r;
        }

        public static void ApplyAsync(UpdateInfo info, Action<UpdateApplyResult> done)
        {
            UpdateInfo snapshot = info;
            Thread t = new Thread(delegate()
            {
                UpdateApplyResult r = Apply(snapshot);
                Invoke(done, r);
            });
            t.IsBackground = true;
            t.Start();
        }

        static UpdateApplyResult Apply(UpdateInfo info)
        {
            UpdateApplyResult r = new UpdateApplyResult();
            byte[] data;
            string err;
            if (!HttpGet(info.Url, PackageTimeoutMs, out data, out err))
            {
                r.Message = "下载失败：" + err + "\n\n当前版本未改动。";
                return r;
            }
            if (data.Length != info.Size)
            {
                r.Message = "文件大小与清单不一致（清单 " + info.Size + " 字节，实际 " + data.Length
                    + " 字节）。文件可能被网络策略改写，已保留当前版本。";
                return r;
            }
            if (data.Length < 2 || data[0] != (byte)'M' || data[1] != (byte)'Z')
            {
                r.Message = "下载内容不是有效的 Windows 程序（PE 头缺失）。文件可能被网络策略改写，已保留当前版本。";
                return r;
            }
            string hash = HexSha256(data);
            if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                r.Message = "校验值不匹配，文件可能被网络策略改写或传输损坏，已保留当前版本。";
                return r;
            }

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClipboardHelper", "update");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "ClipboardHelper-v" + info.Version + ".exe");
                File.WriteAllBytes(path, data);
                r.Ok = true;
                r.SavedPath = path;
                r.Message = "新版已通过完整校验，保存在：\n" + path
                    + "\n\n当前运行的版本没有改动。本阶段不自动替换，手动替换前请先从托盘退出程序。";
            }
            catch (Exception ex)
            {
                r.Message = "保存失败：" + ex.Message + "\n\n当前版本未改动。";
            }
            return r;
        }

        static bool HttpGet(string url, int timeoutMs, out byte[] data, out string err)
        {
            data = null;
            err = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.Proxy = null;
                req.UserAgent = "ClipboardHelper-Update/" + BuildInfo.AppVersion;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200)
                    {
                        err = "HTTP " + (int)resp.StatusCode;
                        return false;
                    }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (Stream s = resp.GetResponseStream())
                        {
                            byte[] buf = new byte[8192];
                            int n;
                            long total = 0;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                total += n;
                                if (total > MaxResponseBytes) { err = "响应超过 32 MB"; return false; }
                                ms.Write(buf, 0, n);
                            }
                        }
                        data = ms.ToArray();
                    }
                }
                return true;
            }
            catch (WebException we)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                err = resp != null ? "HTTP " + (int)resp.StatusCode : we.Message;
                return false;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        static void Invoke<T>(Action<T> done, T result)
        {
            try
            {
                if (Application.Current != null && Application.Current.Dispatcher != null)
                    Application.Current.Dispatcher.BeginInvoke(new Action(delegate { done(result); }));
                else done(result);
            }
            catch { }
        }

        static string ResolveUrl(string source, string url)
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            try { return new Uri(new Uri(source), url).AbsoluteUri; }
            catch { return url; }
        }

        static bool IsSha256(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        static string HexSha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < h.Length; i++)
                    sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
