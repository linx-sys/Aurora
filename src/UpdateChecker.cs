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

        /// <summary>新版本信息（P2-5 更新体验：版本 + 更新日志 + 安装包直链）。</summary>
        public class UpdateInfo
        {
            public string Version;      // 不带 v 前缀
            public string Notes;        // Release notes（markdown 原文，已截断）
            public string SetupUrl;     // AuroraPlayer-Setup.exe 直链（无则 null）
        }

        /// <summary>
        /// 异步检查更新。onNewVersion(info) 在线程池线程回调；
        /// 未发现新版本 / 距上次检查不足 24h / 关闭开关 / 网络失败 → 不回调。
        /// </summary>
        public static void CheckAsync(Action<UpdateInfo> onNewVersion)
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
                    UpdateInfo info = FetchLatest();
                    if (info == null) return;
                    if (IsNewer(info.Version, AppInfo.Version))
                    {
                        MainViewModel.Dbg("Update found: " + info.Version + " (current " + AppInfo.Version + ")");
                        if (onNewVersion != null) onNewVersion(info);
                    }
                }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("Update check FAIL: " + ex.Message);
                }
            });
        }

        /// <summary>拉取最新 Release：tag + notes + Setup 包直链（缺失字段尽力而为）。</summary>
        static UpdateInfo FetchLatest()
        {
            string json = Fetch(AppInfo.ReleasesApi, "application/vnd.github+json");
            if (json == null) return null;
            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success) return null;

            var info = new UpdateInfo { Version = tag.Groups[1].Value.TrimStart('v', 'V') };
            Match body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (body.Success)
            {
                // JSON 字符串反转义 + 截断（release notes 是给用户看的，太长截掉）
                string notes = body.Groups[1].Value
                    .Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
                if (notes.Length > 800) notes = notes.Substring(0, 800) + "\n…（更多见发布页）";
                info.Notes = notes;
            }
            // assets 里找 AuroraPlayer-Setup.exe（同一 JSON 对象内 name 在 browser_download_url 之前）
            Match asset = Regex.Match(json, "\"name\"\\s*:\\s*\"AuroraPlayer-Setup\\.exe\"[^}]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            if (asset.Success) info.SetupUrl = asset.Groups[1].Value;
            return info;
        }

        /// <summary>下载安装包到 target（P2-5：带百分比回调，线程池线程调用）。</summary>
        public static bool DownloadSetup(string url, string target, Action<int> onPercent)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "AuroraPlayer/" + AppInfo.Version;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var input = resp.GetResponseStream())
            using (var output = File.Create(target))
            {
                long total = resp.ContentLength;
                var buf = new byte[65536];
                long done = 0;
                int lastPercent = -1;
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    done += n;
                    if (total > 0 && onPercent != null)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent != lastPercent && percent % 10 == 0) { lastPercent = percent; onPercent(percent); }
                    }
                }
            }
            return File.Exists(target);
        }

        static string Fetch(string url, string accept)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.UserAgent = "AuroraPlayer/" + AppInfo.Version;   // GitHub API 必需 UA
            req.Accept = accept;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                return sr.ReadToEnd();
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
