/* ============================================================
 * NetMatchSettingsTests.cs — 联网匹配按格式开关的纯逻辑测试
 * （不触磁盘：设置查询通过委托注入）
 * ============================================================ */
using System.Collections.Generic;
using Xunit;

namespace Aurora.Tests
{
    public class NetMatchSettingsTests
    {
        static Dictionary<string, string> With(params (string k, string v)[] entries)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in entries) d[k] = v;
            return d;
        }

        [Fact]
        public void MissingSetting_DefaultsToEnabled()
        {
            var d = With();   // 空设置：未配置过 → 默认启用（保持既有行为）
            Assert.True(NetMatch.IsEnabledForExt("mp3", k => d.TryGetValue(k, out var v) ? v : "1"));
        }

        [Fact]
        public void ExplicitZero_Disables()
        {
            var d = With(("netmatch.ext.mp3", "0"));
            Assert.False(NetMatch.IsEnabledForExt("mp3", k => d.TryGetValue(k, out var v) ? v : "1"));
            Assert.True(NetMatch.IsEnabledForExt("flac", k => d.TryGetValue(k, out var v) ? v : "1"));
        }

        [Fact]
        public void ExplicitOne_Enables()
        {
            var d = With(("netmatch.ext.flac", "1"));
            Assert.True(NetMatch.IsEnabledForExt("flac", k => d.TryGetValue(k, out var v) ? v : "1"));
        }

        [Fact]
        public void Extension_IsNormalized()
        {
            var d = With(("netmatch.ext.m4a", "0"));
            // 大小写 / 带点前缀都归一到同一键
            Assert.False(NetMatch.IsEnabledForExt("M4A", k => d.TryGetValue(k, out var v) ? v : "1"));
            Assert.False(NetMatch.IsEnabledForExt(".m4a", k => d.TryGetValue(k, out var v) ? v : "1"));
        }

        [Fact]
        public void NullOrEmptyExt_Enabled()
        {
            Assert.True(NetMatch.IsEnabledForExt(null, _ => "0"));
            Assert.True(NetMatch.IsEnabledForExt("", _ => "0"));
        }

        [Fact]
        public void SettingsKey_Format()
        {
            Assert.Equal("netmatch.ext.mp3", NetMatch.SettingsKeyForExt("mp3"));
            Assert.Equal("netmatch.ext.opus", NetMatch.SettingsKeyForExt(".OPUS"));
        }
    }
}
