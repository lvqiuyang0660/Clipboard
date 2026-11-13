using System;
using System.Reflection;

[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ClipHelper
{
    /// <summary>
    /// 编译期版本信息。类名不叫 Version，避免与 Sync.cs 里 Environment.OSVersion.Version 冲突。
    /// 发版时同步修改本文件的 AppVersion 与 release.json 的 version，build.ps1 会校验两者一致。
    /// </summary>
    public static class BuildInfo
    {
        public const string AppVersion = "1.1.0";

        /// <summary>语义化版本比较：a 更新返回 1，相等 0，a 更旧返回 -1；格式异常按 0 处理。</summary>
        public static int Compare(string a, string b)
        {
            int[] x = Parts(a);
            int[] y = Parts(b);
            int n = Math.Max(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int l = i < x.Length ? x[i] : 0;
                int r = i < y.Length ? y[i] : 0;
                if (l > r) return 1;
                if (l < r) return -1;
            }
            return 0;
        }

        public static bool IsNewer(string candidate, string current)
        {
            return Compare(candidate, current) > 0;
        }

        public static bool IsVersionString(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Trim().TrimStart('v', 'V').Split('.');
            if (p.Length < 2 || p.Length > 4) return false;
            for (int i = 0; i < p.Length; i++)
            {
                int n;
                if (!int.TryParse(p[i], out n) || n < 0) return false;
            }
            return true;
        }

        static int[] Parts(string s)
        {
            if (string.IsNullOrEmpty(s)) return new int[] { 0 };
            string[] seg = s.Trim().TrimStart('v', 'V').Split('.');
            int[] r = new int[seg.Length];
            for (int i = 0; i < seg.Length; i++)
            {
                int n;
                r[i] = int.TryParse(seg[i], out n) && n >= 0 ? n : 0;
            }
            return r;
        }
    }
}
