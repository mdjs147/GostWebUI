using System;
using System.Reflection;

namespace PortForwarder.Core
{
    // 应用版本号:单一来源是 csproj 的 <Version>,运行时从程序集特性读取。
    // 托盘悬停提示、网页顶栏与 GET /api/health 共用同一值,不在代码里另写死版本字符串。
    public static class AppVersion
    {
        public static readonly string Display = ReadVersion();

        // 优先取 InformationalVersion(与 <Version> 一致,如 "2.0.0");
        // 若日后接入 SourceLink 会带 "+commitHash" 后缀,展示时截掉。
        // 特性缺失时回退 AssemblyVersion(四段式),保证任何情况下都有值。
        private static string ReadVersion()
        {
            Assembly assembly = typeof(AppVersion).Assembly;
            AssemblyInformationalVersionAttribute attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = null;
            if (attribute != null)
            {
                version = attribute.InformationalVersion;
            }
            if (string.IsNullOrWhiteSpace(version))
            {
                Version fallback = assembly.GetName().Version;
                if (fallback == null)
                {
                    return "0.0.0";
                }
                return fallback.ToString();
            }
            int plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
            {
                version = version.Substring(0, plusIndex);
            }
            return version;
        }
    }
}
