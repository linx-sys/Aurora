/* ============================================================
 * UpdateChecker.cs — 更新检查（P3）
 * 启动后后台查询 GitHub Releases 最新版本（24h 节流），
 * 发现新版本回调通知（UI 层 Toast + 打开发布页）。
 * 不做静默自更新：下载/安装交给用户在发布页完成（单机个人应用，
 * 自动替换主程序的风险大于收益）。
 * ============================================================ */
using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace Aurora
{
    public static class UpdateChecker
    {
        const string LastCheckKey = "update_last_check";   // UTC ticks
        static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// 异步检查更新。onNewVersion(newVersionWithoutV) 在线程池线程回调；
        /// 未发现新版本 / 距上次检查不足 24h / 关闭开关 / 网络失败 → 不回调。
        /// </summary>
        public static void CheckAsync(Action<string> onNewVersion)
        {
            if (Settings.Get("update_check", "1") == "0") return;

            // 24h 节流：检查无论成功与否都记录时间，避免每次启动都打 API
            long now = DateTime.UtcNow.Ticks;
            long last;
            long.TryParse(Settings.Get(LastCheckKey, "0"), out last);
            if (last > 0 && now - last < CheckInterval.Ticks) return;
            Settings.Set(LastCheckKey, now.ToString());

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string tag = FetchLatestTag();
                    if (tag == null) return;
                    string ver = tag.TrimStart('v', 'V');
                    if (IsNewer(ver, AppInfo.Version))
                    {
                        MainViewModel.Dbg("Update found: " + ver + " (current " + AppInfo.Version + ")");
                        if (onNewVersion != null) onNewVersion(ver);
                    }
                }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("Update check FAIL: " + ex.Message);
                }
            });
        }

        static string FetchLatestTag()
        {
            var req = (HttpWebRequest)WebRequest.Create(AppInfo.ReleasesApi);
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.UserAgent = "AuroraPlayer/" + AppInfo.Version;   // GitHub API 必需 UA
            req.Accept = "application/vnd.github+json";

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                string json = sr.ReadToEnd();
                Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        /// <summary>数值段比较：1.2.10 > 1.2.9；段数不齐按 0 补齐；非数字段忽略后缀。</summary>
        public static bool IsNewer(string candidate, string current)
        {
            int[] a = Parse(candidate);
            int[] b = Parse(current);
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i]) return a[i] > b[i];
            }
            return false;
        }

        static int[] Parse(string version)
        {
            var result = new int[3];
            if (string.IsNullOrEmpty(version)) return result;
            string core = version.Trim().TrimStart('v', 'V');
            int dash = core.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) core = core.Substring(0, dash);
            string[] parts = core.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int.TryParse(parts[i], out result[i]);
            }
            return result;
        }
    }
}
