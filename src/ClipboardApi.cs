using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;

namespace ClipHelper
{
    /// <summary>直接使用 Win32 剪贴板接口：比 WPF 的 OLE 封装快，也不会和别的程序互相卡住。</summary>
    public static class ClipboardApi
    {
        const uint CF_TEXT = 1;
        const uint CF_BITMAP = 2;
        const uint CF_DIB = 8;
        const uint CF_UNICODETEXT = 13;
        const uint CF_HDROP = 15;
        const uint CF_DIBV5 = 17;
        const uint GMEM_MOVEABLE = 0x0002;

        const int MaxFormatBytes = 24 * 1024 * 1024;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchFormatName);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        static extern UIntPtr GlobalSize(IntPtr hMem);

        /// <summary>开发期诊断日志输出口（由 Program 指向本地文件）。</summary>
        public static Action<string> Log;

        public class FmtInfo
        {
            public uint Id;
            public string Name;
            public int Size = -1;
            public string Magic;
        }

        public class ReadResult
        {
            public bool Excluded;
            public bool HasText;
            public string Text;
            public BitmapSource Image;
            public bool Placeholder;      // 文本只是「[图片]」这类占位符
            public string Source = "";    // 图片取自哪种格式，便于排查
            public List<FmtInfo> Formats = new List<FmtInfo>();
        }

        static readonly string[] Placeholders = new string[]
        {
            "[图片]", "[image]", "[图片消息]", "[表情]", "[动画表情]", "[贴图]",
            "[视频]", "[文件]", "[语音]", "[链接]", "[位置]", "[名片]"
        };

        public static bool IsPlaceholder(string text)
        {
            if (text == null) return false;
            string t = text.Trim();
            if (t.Length == 0) return false;
            for (int i = 0; i < Placeholders.Length; i++)
            {
                if (string.Equals(t, Placeholders[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool Open()
        {
            for (int i = 0; i < 8; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        static string FormatName(uint fmt)
        {
            if (fmt <= 0x400) return "";
            try
            {
                StringBuilder sb = new StringBuilder(256);
                int n = GetClipboardFormatName(fmt, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        static byte[] BytesOf(uint fmt, int max)
        {
            IntPtr h = GetClipboardData(fmt);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                int size = (int)GlobalSize(h);
                if (size <= 0 || size > max) return null;
                byte[] b = new byte[size];
                Marshal.Copy(p, b, 0, size);
                return b;
            }
            finally { GlobalUnlock(h); }
        }

        static string MagicOf(byte[] b)
        {
            if (b == null || b.Length < 4) return "";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "PNG";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
            if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return "GIF";
            if (b[0] == (byte)'B' && b[1] == (byte)'M') return "BMP";
            if (b[0] == (byte)'I' && b[1] == (byte)'I' && b[2] == 0x2A) return "TIFF";
            return "";
        }

        static bool IsImageMagic(string magic)
        {
            return magic == "PNG" || magic == "JPEG" || magic == "GIF" || magic == "BMP" || magic == "TIFF";
        }

        static BitmapSource DecodeBytes(byte[] b)
        {
            if (b == null || b.Length < 16) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(b))
                {
                    BitmapImage img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch (Exception ex)
            {
                if (Log != null) Log("  decode fail: " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        static BitmapSource DecodeFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                byte[] b = File.ReadAllBytes(path);
                return DecodeBytes(b);
            }
            catch { return null; }
        }

        static bool IsImagePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".bmp" || e == ".tif" || e == ".tiff";
        }

        /// <summary>按优先级取图：PNG → DIBV5 → DIB → BITMAP → HDROP → HTML → RTF → 私有格式魔数。</summary>
        public static ReadResult Read()
        {
            ReadResult r = new ReadResult();
            if (!Open()) return null;
            try
            {
                List<uint> ids = new List<uint>();
                uint fmt = 0;
                while ((fmt = EnumClipboardFormats(fmt)) != 0) ids.Add(fmt);

                Dictionary<uint, byte[]> custom = new Dictionary<uint, byte[]>();

                bool hasText = false;
                bool hasBitmap = false;
                uint dibFmt = 0, pngFmt = 0, htmlFmt = 0, rtfFmt = 0, hdropFmt = 0;

                for (int i = 0; i < ids.Count; i++)
                {
                    uint id = ids[i];
                    string name = FormatName(id);
                    FmtInfo fi = new FmtInfo();
                    fi.Id = id;
                    fi.Name = name;
                    r.Formats.Add(fi);

                    if (id == CF_UNICODETEXT) { hasText = true; continue; }
                    if (id == CF_DIBV5) { dibFmt = CF_DIBV5; continue; }
                    if (id == CF_DIB) { if (dibFmt != CF_DIBV5) dibFmt = CF_DIB; continue; }
                    if (id == CF_BITMAP) { hasBitmap = true; continue; }
                    if (id == CF_HDROP) { hdropFmt = id; continue; }
                    if (name.Length == 0) continue;

                    string n = name.ToUpperInvariant();
                    if (n == "PNG") { pngFmt = id; continue; }
                    if (n == "HTML FORMAT") { htmlFmt = id; continue; }
                    if (n == "RICH TEXT FORMAT" || n == "RICH TEXT FORMAT WITHOUT OBJECTS") { rtfFmt = id; continue; }
                    if (n == "EXCLUDECLIPBOARDCONTENTFROMMONITORPROCESSING" || n == "CLIPBOARD VIEWER IGNORE")
                    {
                        r.Excluded = true;
                        continue;
                    }
                    if (n == "CANINCLUDEINCLIPBOARDHISTORY")
                    {
                        IntPtr hh = GetClipboardData(id);
                        if (hh != IntPtr.Zero)
                        {
                            IntPtr pp = GlobalLock(hh);
                            if (pp != IntPtr.Zero)
                            {
                                int v = Marshal.ReadInt32(pp);
                                GlobalUnlock(hh);
                                if (v == 0) r.Excluded = true;
                            }
                        }
                        continue;
                    }

                    // 第三方/私有格式：主动请求一次数据（会触发延迟渲染），用于日志与图片魔数嗅探
                    byte[] cb = BytesOf(id, MaxFormatBytes);
                    if (cb != null)
                    {
                        fi.Size = cb.Length;
                        fi.Magic = MagicOf(cb);
                        custom[id] = cb;
                    }
                }
                if (r.Excluded) return r;

                // ---- 文本
                if (hasText)
                {
                    IntPtr h = GetClipboardData(CF_UNICODETEXT);
                    if (h != IntPtr.Zero)
                    {
                        IntPtr p = GlobalLock(h);
                        if (p != IntPtr.Zero)
                        {
                            try { r.Text = Marshal.PtrToStringUni(p); }
                            finally { GlobalUnlock(h); }
                        }
                    }
                    r.HasText = r.Text != null;
                }

                // ---- 图片：按优先级依次尝试
                if (pngFmt != 0)
                {
                    byte[] b = BytesOf(pngFmt, MaxFormatBytes);
                    if (b != null && IsImageMagic(MagicOf(b)))
                    {
                        r.Image = DecodeBytes(b);
                        if (r.Image != null) r.Source = "PNG";
                    }
                }

                if (r.Image == null && dibFmt != 0)
                {
                    byte[] b = BytesOf(dibFmt, MaxFormatBytes);
                    if (b != null)
                    {
                        r.Image = DibToBitmap(b, dibFmt == CF_DIBV5);
                        if (r.Image != null) r.Source = dibFmt == CF_DIBV5 ? "CF_DIBV5" : "CF_DIB";
                    }
                }

                if (r.Image == null && hasBitmap)
                {
                    IntPtr hbmp = GetClipboardData(CF_BITMAP);
                    if (hbmp != IntPtr.Zero)
                    {
                        try
                        {
                            BitmapSource s = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hbmp, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            r.Image = new WriteableBitmap(s);   // 复制一份，避免持有别人的 HBITMAP
                            r.Source = "CF_BITMAP";
                        }
                        catch { }
                    }
                }

                if (r.Image == null && hdropFmt != 0)
                {
                    r.Image = FromHDrop(hdropFmt);
                    if (r.Image != null) r.Source = "CF_HDROP";
                }

                if (r.Image == null && htmlFmt != 0)
                {
                    r.Image = FromHtml(htmlFmt);
                    if (r.Image != null) r.Source = "HTML";
                }

                if (r.Image == null && rtfFmt != 0)
                {
                    r.Image = FromRtf(rtfFmt);
                    if (r.Image != null) r.Source = "RTF";
                }

                if (r.Image == null && custom.Count > 0)
                {
                    foreach (KeyValuePair<uint, byte[]> kv in custom)
                    {
                        if (!IsImageMagic(MagicOf(kv.Value))) continue;
                        r.Image = DecodeBytes(kv.Value);
                        if (r.Image != null)
                        {
                            r.Source = "自定义格式:" + FormatName(kv.Key);
                            break;
                        }
                    }
                }

                if (r.Image == null && r.HasText && IsPlaceholder(r.Text)) r.Placeholder = true;
                return r;
            }
            catch { return r; }
            finally { CloseClipboard(); }
        }

        static BitmapSource FromHDrop(uint fmt)
        {
            byte[] b = BytesOf(fmt, 4 * 1024 * 1024);
            if (b == null || b.Length < 20) return null;
            int offset = BitConverter.ToInt32(b, 0);
            bool wide = BitConverter.ToInt32(b, 16) != 0;
            List<string> files = new List<string>();
            int p = offset;
            if (wide)
            {
                while (p + 1 < b.Length)
                {
                    int end = p;
                    while (end + 1 < b.Length && !(b[end] == 0 && b[end + 1] == 0)) end += 2;
                    if (end == p) break;
                    files.Add(Encoding.Unicode.GetString(b, p, end - p));
                    p = end + 2;
                }
            }
            else
            {
                while (p < b.Length)
                {
                    int end = p;
                    while (end < b.Length && b[end] != 0) end++;
                    if (end == p) break;
                    files.Add(Encoding.Default.GetString(b, p, end - p));
                    p = end + 1;
                }
            }
            for (int i = 0; i < files.Count && i < 8; i++)
            {
                if (!IsImagePath(files[i])) continue;
                BitmapSource s = DecodeFile(files[i]);
                if (s != null) return s;
            }
            return null;
        }

        static BitmapSource FromHtml(uint fmt)
        {
            byte[] b = BytesOf(fmt, 4 * 1024 * 1024);
            if (b == null) return null;
            string html = Encoding.UTF8.GetString(b);
            string[] keys = new string[] { "src=\"", "src='", "href=\"" };
            for (int k = 0; k < keys.Length; k++)
            {
                int idx = 0;
                while (true)
                {
                    idx = html.IndexOf(keys[k], idx, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    int start = idx + keys[k].Length;
                    char quote = keys[k].EndsWith("'") ? '\'' : '"';
                    int end = html.IndexOf(quote, start);
                    if (end < 0) break;
                    string url = html.Substring(start, end - start);
                    idx = end + 1;
                    if (!url.Contains("file://")) continue;
                    string local = url;
                    try { local = new Uri(url).LocalPath; } catch { }
                    local = Uri.UnescapeDataString(local);
                    if (!IsImagePath(local)) continue;
                    BitmapSource s = DecodeFile(local);
                    if (s != null) return s;
                }
            }
            return null;
        }

        static BitmapSource FromRtf(uint fmt)
        {
            byte[] b = BytesOf(fmt, 8 * 1024 * 1024);
            if (b == null) return null;
            string rtf = Encoding.ASCII.GetString(b);
            int at = rtf.IndexOf("pngblip", StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = rtf.IndexOf("jpegblip", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            // 跳过控制字，找到第一段足够长的十六进制像素数据
            int i = at + 7;
            while (i < rtf.Length)
            {
                if (!Uri.IsHexDigit(rtf[i])) { i++; continue; }
                int start = i;
                while (i < rtf.Length && Uri.IsHexDigit(rtf[i])) i++;
                if (i - start < 256) continue;   // 太短，多半是 \picw 之类的参数
                StringBuilder hex = new StringBuilder(i - start);
                for (int j = start; j < i; j++) hex.Append(rtf[j]);
                if (hex.Length % 2 != 0) hex.Length--;
                byte[] data = new byte[hex.Length / 2];
                for (int j = 0; j < data.Length; j++)
                    data[j] = Convert.ToByte(hex.ToString(j * 2, 2), 16);
                if (!IsImageMagic(MagicOf(data))) return null;
                return DecodeBytes(data);
            }
            return null;
        }

        /// <summary>OLE 兜底：部分程序只在真正被请求时才生成图片（延迟渲染）。</summary>
        public static BitmapSource TryOleImage()
        {
            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage()) return null;
                using (System.Drawing.Image img = System.Windows.Forms.Clipboard.GetImage())
                {
                    if (img == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return DecodeBytes(ms.ToArray());
                    }
                }
            }
            catch { return null; }
        }

        public static bool SetText(string text)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!Open()) { Thread.Sleep(40); continue; }
                try
                {
                    EmptyClipboard();
                    byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                    IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                    if (h == IntPtr.Zero) return false;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return false;
                    Marshal.Copy(bytes, 0, p, bytes.Length);
                    GlobalUnlock(h);
                    if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) return false;
                    return true;
                }
                catch { return false; }
                finally { CloseClipboard(); }
            }
            return false;
        }

        public static void SetImage(BitmapSource src)
        {
            try { System.Windows.Clipboard.SetImage(src); }
            catch { }
        }

        static BitmapSource DibToBitmap(byte[] dib, bool v5)
        {
            if (dib.Length < 40) return null;
            int headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40 || headerSize > dib.Length) return null;

            int bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int colors = bitCount <= 8 ? (clrUsed == 0 ? (1 << bitCount) : clrUsed) : 0;

            // V4/V5 头 WIC 不一定认，统一压成 40 字节 BITMAPINFOHEADER；
            // 32bpp 一律按 BI_RGB 处理（不再需要 3 个掩码）。
            int srcPos = headerSize + ((compression == 3 && headerSize == 40) ? 12 : 0) + colors * 4;
            if (srcPos > dib.Length) return null;

            bool keepMasks = (compression == 3 && bitCount == 16);
            int maskBytes = keepMasks ? 12 : 0;
            byte[] body = new byte[dib.Length - srcPos];
            Buffer.BlockCopy(dib, srcPos, body, 0, body.Length);

            if (bitCount == 32)
            {
                bool opaque = true;
                for (int i = 3; i < body.Length; i += 4) { if (body[i] != 0) { opaque = false; break; } }
                if (opaque) for (int i = 3; i < body.Length; i += 4) body[i] = 255;
            }

            byte[] bmp = new byte[14 + 40 + maskBytes + colors * 4 + body.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            WriteInt(bmp, 2, bmp.Length);
            WriteInt(bmp, 10, 14 + 40 + maskBytes + colors * 4);
            Buffer.BlockCopy(dib, 0, bmp, 14, 40);
            WriteInt(bmp, 14, 40);
            if (bitCount == 32) WriteInt(bmp, 30, 0);   // biCompression = BI_RGB
            if (maskBytes > 0) Buffer.BlockCopy(dib, headerSize, bmp, 54, maskBytes);
            if (colors > 0) Buffer.BlockCopy(dib, headerSize + maskBytes, bmp, 54 + maskBytes, colors * 4);
            Buffer.BlockCopy(body, 0, bmp, 54 + maskBytes + colors * 4, body.Length);

            return DecodeBytes(bmp);
        }

        static void WriteInt(byte[] buf, int offset, int value)
        {
            byte[] b = BitConverter.GetBytes(value);
            Array.Copy(b, 0, buf, offset, 4);
        }
    }
}
