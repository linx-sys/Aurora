/* ============================================================
 * Lrc.cs — LRC 歌词解析（多时间标签 / [offset] / 元数据）
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aurora
{
    public class LrcLine
    {
        public double Time;
        public string Text;
    }

    public class LrcDoc
    {
        public List<LrcLine> Lines = new List<LrcLine>();
        public string Title, Artist, Album;
    }

    public static class Lrc
    {
        // [mm:ss.xx] / [mm:ss]
        static readonly Regex TimeTag = new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        static readonly Regex MetaTag = new Regex(@"^\[(ti|ar|al|by|offset):(.*)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex EnhTag = new Regex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

        public static LrcDoc Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var doc = new LrcDoc();
            double offset = 0;

            foreach (string rawLine in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                Match mm = MetaTag.Match(line);
                if (mm.Success)
                {
                    string key = mm.Groups[1].Value.ToLowerInvariant();
                    string val = mm.Groups[2].Value.Trim().Trim('"', '\'');
                    if (key == "offset")
                    {
                        double n;
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) offset = n;
                    }
                    else if (key == "ti") doc.Title = val;
                    else if (key == "ar") doc.Artist = val;
                    else if (key == "al") doc.Album = val;
                    continue;
                }

                MatchCollection tags = TimeTag.Matches(line);
                if (tags.Count == 0) continue; // 纯文本/增强格式行，忽略

                string content = TimeTag.Replace(line, "").Trim();
                content = EnhTag.Replace(content, "").Trim();

                foreach (Match t in tags)
                {
                    double sec = int.Parse(t.Groups[1].Value) * 60 + int.Parse(t.Groups[2].Value);
                    string frac = t.Groups[3].Value;
                    if (frac.Length > 0)
                        sec += int.Parse(frac) / Math.Pow(10, frac.Length);
                    doc.Lines.Add(new LrcLine { Time = sec, Text = content });
                }
            }

            if (doc.Lines.Count == 0) return null;
            doc.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));

            if (offset != 0)
            {
                // 正 offset 提前、负 offset 延后；钳制到 0 保证时间轴单调不回退
                double off = offset / 1000.0;
                foreach (LrcLine l in doc.Lines)
                    l.Time = Math.Max(0, l.Time + off);
            }
            return doc;
        }

        /// <summary>返回 time 秒所在行下标；早于首行返回 -1。二分查找。</summary>
        public static int IndexAt(List<LrcLine> lines, double time)
        {
            int lo = 0, hi = lines.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (lines[mid].Time <= time) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }
    }
}
