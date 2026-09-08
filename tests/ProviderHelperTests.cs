/* ============================================================
 * ProviderHelperTests.cs — 联网匹配基类纯逻辑单元测试
 * 覆盖：歌名归一化（Normalize）、JSON 树解析与取值辅助
 *       （JObj/JGet/JArr/JStr/JLong/JInt）。不触网、不触盘。
 * ============================================================ */
using System.Collections.Generic;
using Xunit;

namespace Aurora.Tests
{
    /// <summary>通过派生类暴露基类 protected static 纯逻辑（不实例化网络行为）。</summary>
    public class ProviderHelperAccessor : LyricsProviderBase
    {
        public override string Name { get { return "test"; } }
        public override bool Match(NetMatchResult r, string musicPath, string title, string artist) { return false; }

        public static string Norm(string s) { return Normalize(s); }
        public static Dictionary<string, object> Parse(string json) { return JObj(json); }
        public static object Get(object o, string key) { return JGet(o, key); }
        public static object[] Arr(object o) { return JArr(o); }
        public static string Str(object o) { return JStr(o); }
        public static long L(object o) { return JLong(o); }
        public static int I(object o) { return JInt(o); }
    }

    public class ProviderHelperTests
    {
        /* ---------------- Normalize ---------------- */

        [Theory]
        [InlineData("Hello World", "helloworld")]
        [InlineData("晴天 (Live)", "晴天live")]                    // 括号注释内的空格去除
        [InlineData("Song [深情版]!", "song深情版")]
        [InlineData("  A  B\tC  ", "abc")]
        [InlineData("ABC-123", "abc123")]
        [InlineData("", "")]
        public void Normalize_StripsNonAlnum_AndLowercases(string input, string expected)
        {
            Assert.Equal(expected, ProviderHelperAccessor.Norm(input));
        }

        [Fact]
        public void Normalize_Null_ThrowsOrEmpty()
        {
            // 实现直接对 null 调用 s.Length 会抛异常——调用方均保证非空，这里仅验证行为约定
            try
            {
                var r = ProviderHelperAccessor.Norm(null);
                Assert.Equal("", r);
            }
            catch (System.NullReferenceException) { /* 可接受：上游保证非空 */ }
        }

        /* ---------------- JSON 树 ---------------- */

        [Fact]
        public void JObj_ValidNestedJson()
        {
            var o = ProviderHelperAccessor.Parse("{\"a\":{\"b\":[1,2]},\"s\":\"x\",\"n\":3.5,\"t\":true,\"z\":null}");
            Assert.NotNull(o);
            var a = ProviderHelperAccessor.Get(o, "a");
            var arr = ProviderHelperAccessor.Arr(ProviderHelperAccessor.Get(a, "b"));
            Assert.Equal(2, arr.Length);
            Assert.Equal(1L, arr[0]);
            Assert.Equal("x", ProviderHelperAccessor.Get(o, "s"));
            Assert.Equal(3.5, ProviderHelperAccessor.Get(o, "n"));
            Assert.Equal(true, ProviderHelperAccessor.Get(o, "t"));
            Assert.Null(ProviderHelperAccessor.Get(o, "z"));
        }

        [Fact]
        public void JObj_InvalidJson_ReturnsNull()
        {
            Assert.Null(ProviderHelperAccessor.Parse("{not json"));
            Assert.Null(ProviderHelperAccessor.Parse(""));
        }

        [Fact]
        public void JGet_MissingKeyOrWrongType_ReturnsNull()
        {
            var o = ProviderHelperAccessor.Parse("{\"k\":1}");
            Assert.Null(ProviderHelperAccessor.Get(o, "missing"));
            Assert.Null(ProviderHelperAccessor.Get("not a dict", "k"));
            Assert.Null(ProviderHelperAccessor.Get(null, "k"));
        }

        [Fact]
        public void JArr_NonArray_ReturnsNull()
        {
            var o = ProviderHelperAccessor.Parse("{\"a\":[1],\"b\":1}");
            Assert.NotNull(ProviderHelperAccessor.Arr(ProviderHelperAccessor.Get(o, "a")));
            Assert.Null(ProviderHelperAccessor.Arr(ProviderHelperAccessor.Get(o, "b")));
            Assert.Null(ProviderHelperAccessor.Arr(null));
        }

        [Fact]
        public void JStr_Null_ReturnsEmptyString()
        {
            Assert.Equal("", ProviderHelperAccessor.Str(null));
            Assert.Equal("42", ProviderHelperAccessor.Str(42L));
            Assert.Equal("v", ProviderHelperAccessor.Str("v"));
        }

        [Fact]
        public void JLong_JInt_Null_ReturnsZero()
        {
            Assert.Equal(0L, ProviderHelperAccessor.L(null));
            Assert.Equal(0, ProviderHelperAccessor.I(null));
        }

        [Fact]
        public void JLong_ConvertsDoubleAndString()
        {
            Assert.Equal(42L, ProviderHelperAccessor.L(42.0));
            Assert.Equal(42L, ProviderHelperAccessor.L("42"));
        }

        [Fact]
        public void JInt_Overflow_ReturnsZero()
        {
            Assert.Equal(0, ProviderHelperAccessor.I(long.MaxValue));   // 超出 int 范围 → 0
        }
    }
}
