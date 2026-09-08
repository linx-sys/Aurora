/* ============================================================
 * LrcTests.cs — LRC 歌词解析单元测试
 * 覆盖：单/多时间标签、元数据、offset 平移与钳制、增强格式剥离、
 *       排序、非法/空输入、IndexAt 二分查找边界。
 * ============================================================ */
using System;
using System.Collections.Generic;
using Xunit;

namespace Aurora.Tests
{
    public class LrcTests
    {
        [Fact]
        public void Parse_BasicSingleTimestamp()
        {
            var doc = Lrc.Parse("[00:12.34]hello world");
            Assert.NotNull(doc);
            Assert.Single(doc.Lines);
            Assert.Equal(12.34, doc.Lines[0].Time, 2);
            Assert.Equal("hello world", doc.Lines[0].Text);
        }

        [Fact]
        public void Parse_MinutesOnlyFormat()
        {
            var doc = Lrc.Parse("[01:02]text");
            Assert.Equal(62, doc.Lines[0].Time, 3);
        }

        [Fact]
        public void Parse_Fraction_TwoAndThreeDigits()
        {
            // [mm:ss.xx] 两位小数 → 百分秒；三位 → 千分秒
            Assert.Equal(62.50, Lrc.Parse("[01:02.50]a").Lines[0].Time, 3);
            Assert.Equal(62.05, Lrc.Parse("[01:02.05]a").Lines[0].Time, 3);
            Assert.Equal(62.005, Lrc.Parse("[01:02.005]a").Lines[0].Time, 3);
        }

        [Fact]
        public void Parse_MultipleTimestamps_DuplicatesLineContent()
        {
            var doc = Lrc.Parse("[00:10]chorus\n[00:50]chorus");
            Assert.Equal(2, doc.Lines.Count);
            Assert.All(doc.Lines, l => Assert.Equal("chorus", l.Text));
            Assert.Equal(10, doc.Lines[0].Time, 3);
            Assert.Equal(50, doc.Lines[1].Time, 3);
        }

        [Fact]
        public void Parse_SortsLinesByTime()
        {
            var doc = Lrc.Parse("[00:30]b\n[00:10]a\n[00:20]c");
            Assert.Equal(new[] { "a", "c", "b" }, new[] { doc.Lines[0].Text, doc.Lines[1].Text, doc.Lines[2].Text });
        }

        [Fact]
        public void Parse_MetadataTags_TitleArtistAlbum()
        {
            var doc = Lrc.Parse("[ti:晴天]\n[ar:周杰伦]\n[al:叶惠美]\n[00:01]line");
            Assert.Equal("晴天", doc.Title);
            Assert.Equal("周杰伦", doc.Artist);
            Assert.Equal("叶惠美", doc.Album);
        }

        [Fact]
        public void Parse_MetadataStripsQuotes()
        {
            var doc = Lrc.Parse("[ti:\"quoted\"]\n[00:01]line");
            Assert.Equal("quoted", doc.Title);
        }

        [Fact]
        public void Parse_PositiveOffset_ShiftsTimes()
        {
            var doc = Lrc.Parse("[offset:2000]\n[00:10]a");
            Assert.Equal(12, doc.Lines[0].Time, 3);
        }

        [Fact]
        public void Parse_NegativeOffset_ClampsAtZero()
        {
            var doc = Lrc.Parse("[offset:-5000]\n[00:02]a\n[00:10]b");
            Assert.Equal(0, doc.Lines[0].Time, 3);   // 钳制到 0，保证时间轴单调
            Assert.Equal(5, doc.Lines[1].Time, 3);
        }

        [Fact]
        public void Parse_InvalidOffset_Ignored()
        {
            var doc = Lrc.Parse("[offset:abc]\n[00:10]a");
            Assert.Equal(10, doc.Lines[0].Time, 3);
        }

        [Fact]
        public void Parse_EnhancedWordTags_Stripped()
        {
            var doc = Lrc.Parse("[00:01.00]<00:01.50>word<00:02>by<00:03>word");
            Assert.Equal("wordbyword", doc.Lines[0].Text);
        }

        [Fact]
        public void Parse_EmptyOrNull_ReturnsNull()
        {
            Assert.Null(Lrc.Parse(null));
            Assert.Null(Lrc.Parse(""));
        }

        [Fact]
        public void Parse_NoTimestampLines_ReturnsNull()
        {
            Assert.Null(Lrc.Parse("just plain text\n[ti:only metadata]"));
        }

        [Fact]
        public void Parse_PlainTextBetweenTimestamps_Ignored()
        {
            var doc = Lrc.Parse("[00:01]a\nrandom note\n[00:02]b");
            Assert.Equal(2, doc.Lines.Count);
        }

        [Fact]
        public void Parse_HandlesCrLfAndCr()
        {
            var doc = Lrc.Parse("[00:01]a\r\n[00:02]b\r[00:03]c");
            Assert.Equal(3, doc.Lines.Count);
        }

        [Fact]
        public void IndexAt_BeforeFirstLine_ReturnsMinusOne()
        {
            var doc = Lrc.Parse("[00:10]a");
            Assert.Equal(-1, Lrc.IndexAt(doc.Lines, 5));
        }

        [Fact]
        public void IndexAt_ExactAndBetween()
        {
            var doc = Lrc.Parse("[00:10]a\n[00:20]b\n[00:30]c");
            Assert.Equal(0, Lrc.IndexAt(doc.Lines, 10));
            Assert.Equal(0, Lrc.IndexAt(doc.Lines, 15));    // 15s → 仍在第 0 行
            Assert.Equal(1, Lrc.IndexAt(doc.Lines, 20));
            Assert.Equal(1, Lrc.IndexAt(doc.Lines, 29.9));  // 30s 前仍是第 1 行
            Assert.Equal(2, Lrc.IndexAt(doc.Lines, 30));
        }

        [Fact]
        public void IndexAt_AfterLast_ReturnsLastIndex()
        {
            var doc = Lrc.Parse("[00:10]a\n[00:20]b");
            Assert.Equal(1, Lrc.IndexAt(doc.Lines, 999));
        }

        [Fact]
        public void IndexAt_EmptyList_ReturnsMinusOne()
        {
            Assert.Equal(-1, Lrc.IndexAt(new List<LrcLine>(), 10));
        }
    }
}
