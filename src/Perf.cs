using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ClipHelper
{
    /// <summary>轻量性能计数：在内存里保留最近若干次耗时，按需写诊断日志；开销极低（无日志时不落盘）。</summary>
    public static class Perf
    {
        public class Stat
        {
            public int N;
            public double P50;
            public double P95;
            public double Max;
            public double Avg;
            public double Last;
        }

        const int Cap = 300;
        static readonly object _lock = new object();
        static readonly Dictionary<string, List<double>> _samples = new Dictionary<string, List<double>>();
        static readonly Dictionary<string, double> _last = new Dictionary<string, double>();
        static readonly Dictionary<string, double> _max = new Dictionary<string, double>();

        /// <summary>是否把每次耗时追加到 perf.log（设置里的「性能诊断」开关）。</summary>
        public static bool Verbose;

        static string LogPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHelper");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "perf.log");
            }
        }

        public static void Add(string key, double ms)
        {
            List<double> list;
            lock (_lock)
            {
                if (!_samples.TryGetValue(key, out list))
                {
                    list = new List<double>(Cap);
                    _samples[key] = list;
                }
                if (list.Count >= Cap) list.RemoveAt(0);
                list.Add(ms);
                _last[key] = ms;
                double mx;
                if (!_max.TryGetValue(key, out mx) || ms > mx) _max[key] = ms;
            }
            if (Verbose) Write(key, ms);
        }

        static void Write(string key, double ms)
        {
            try
            {
                FileInfo fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + key.PadRight(22) + ms.ToString("0.0").PadLeft(8) + " ms\r\n";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch { }
        }

        public static Stat Get(string key)
        {
            lock (_lock)
            {
                List<double> list;
                if (!_samples.TryGetValue(key, out list) || list.Count == 0) return new Stat();
                double[] arr = list.ToArray();
                Array.Sort(arr);
                Stat s = new Stat();
                s.N = arr.Length;
                s.P50 = arr[(int)Math.Floor((arr.Length - 1) * 0.50)];
                s.P95 = arr[(int)Math.Ceiling((arr.Length - 1) * 0.95)];
                s.Max = arr[arr.Length - 1];
                s.Avg = arr.Average();
                double last;
                s.Last = _last.TryGetValue(key, out last) ? last : 0;
                return s;
            }
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _samples.Clear();
                _last.Clear();
                _max.Clear();
            }
        }

        /// <summary>用 using 包一段代码，自动记录耗时。</summary>
        public static IDisposable Span(string key)
        {
            return new Timer(key);
        }

        class Timer : IDisposable
        {
            readonly string _key;
            readonly Stopwatch _sw;
            public Timer(string key)
            {
                _key = key;
                _sw = Stopwatch.StartNew();
            }
            public void Dispose()
            {
                _sw.Stop();
                Perf.Add(_key, _sw.Elapsed.TotalMilliseconds);
            }
        }
    }
}
