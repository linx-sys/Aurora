/* ============================================================
 * LibraryTests.cs — 音乐库扫描与轨道模型单元测试
 * 覆盖：音频扩展名识别、目录递归枚举、BuildTracks 的 lrc 配对/排序、
 *       文件名推断回退（TagReaderService）、Track 模型展示逻辑。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using Xunit;

namespace Aurora.Tests
{
    public class LibraryTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "aurora_tests_" + Guid.NewGuid().ToString("N"));

        string Write(string name, byte[]? bytes)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes ?? new byte[] { 1, 2, 3 });
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        /* ---------------- IsAudio ---------------- */

        [Theory]
        [InlineData("a.mp3", true)]
        [InlineData("a.flac", true)]
        [InlineData("a.m4a", true)]
        [InlineData("a.wav", true)]
        [InlineData("a.ogg", true)]
        [InlineData("a.oga", true)]
        [InlineData("a.aac", true)]
        [InlineData("a.opus", true)]
        [InlineData("a.wma", true)]
        [InlineData("a.MP3", true)]
        [InlineData("a.txt", false)]
        [InlineData("a.lrc", false)]
        [InlineData("a.jpg", false)]
        [InlineData("a", false)]
        public void IsAudio_RecognizesSupportedExtensions(string path, bool expected)
        {
            Assert.Equal(expected, Library.IsAudio(path));
        }

        /* ---------------- EnumerateFiles ---------------- */

        [Fact]
        public void EnumerateFiles_FindsAudioAndLrc_Recursively()
        {
            Write("root.mp3", null);
            Write("cover.txt", null);
            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "nested.flac"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(sub, "nested.lrc"), new byte[] { 1 });

            var files = new List<string>();
            Library.EnumerateFiles(dir, files, 0);

            Assert.Equal(3, files.Count);   // root.mp3 + nested.flac + nested.lrc（txt 排除）
        }

        [Fact]
        public void EnumerateFiles_NonexistentDir_DoesNotThrow()
        {
            var files = new List<string>();
            Library.EnumerateFiles(Path.Combine(dir, "missing_dir"), files, 0);
            Assert.Empty(files);
        }

        /* ---------------- BuildTracks ---------------- */

        [Fact]
        public void BuildTracks_PairsLrcByFileName()
        {
            Write("song.mp3", new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
            Write("song.lrc", Encoding.UTF8.GetBytes("[00:01]line"));
            Write("unrelated.lrc", Encoding.UTF8.GetBytes("[00:01]x"));

            var tracks = Library.BuildTracks(Directory.GetFiles(dir));
            Assert.Single(tracks);
            Assert.NotNull(tracks[0].LrcText);
            Assert.Contains("[00:01]line", tracks[0].LrcText);
        }

        [Fact]
        public void BuildTracks_LyricsStayInTheirOwnDirectory()
        {
            string a = Path.Combine(dir, "a"), b = Path.Combine(dir, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            string first = Path.Combine(a, "song.mp3"), second = Path.Combine(b, "song.mp3");
            File.WriteAllBytes(first, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 1, 2, 3 });
            string lyric = Path.Combine(a, "song.lrc");
            File.WriteAllText(lyric, "[00:01]only-a");
            var tracks = Library.BuildTracks(new[] { first, second, lyric });
            Assert.Contains("only-a", tracks.Single(t => t.FilePath == first).LrcText);
            Assert.Null(tracks.Single(t => t.FilePath == second).LrcText);
        }

        [Fact]
        public void ScanningAndMaterialization_RespectCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => Library.EnumerateFiles(dir, new List<string>(), 0, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => Library.BuildTracksIncremental(Array.Empty<string>(), null, null, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => Library.BuildTracksFromRows(Array.Empty<TrackRow>(), false, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => Library.FromMetadata("song.mp3", "Song", null, null,
                TimeSpan.Zero, null, null, readFsExtras: false, cancellationToken: cancellation.Token));
        }

        [Fact]
        public void BuildTracks_SortsByFileName()
        {
            Write("c.mp3", null);
            Write("a.mp3", null);
            Write("b.mp3", null);

            var tracks = Library.BuildTracks(Directory.GetFiles(dir));
            Assert.Equal(new[] { "a.mp3", "b.mp3", "c.mp3" },
                new[] { tracks[0].FileName, tracks[1].FileName, tracks[2].FileName });
        }

        [Fact]
        public void BuildTracks_SkipsNonAudioAndLrc()
        {
            Write("note.txt", null);
            Write("only.lrc", null);

            Assert.Empty(Library.BuildTracks(Directory.GetFiles(dir)));
        }

        [Fact]
        public void BuildTracks_JunkAudio_StillBuildsTrackViaFilenameInference()
        {
            // 无有效标签的"损坏"音频：文件名推断回退（"Artist - Title.mp3"）
            Write("周杰伦 - 晴天.mp3", new byte[] { 1, 2, 3 });

            var tracks = Library.BuildTracks(Directory.GetFiles(dir));
            Assert.Single(tracks);
            Assert.Equal("晴天", tracks[0].Title);
            Assert.Equal("周杰伦", tracks[0].Artist);
        }

        [Fact]
        public void BuildTracks_NumberedPrefix_Stripped()
        {
            Write("01 - 晴天.mp3", new byte[] { 1, 2, 3 });

            var tracks = Library.BuildTracks(Directory.GetFiles(dir));
            Assert.Single(tracks);
            Assert.Equal("晴天", tracks[0].Title);
        }
    }

    public class TrackModelTests
    {
        static Track T(string title = "T", string artist = "A", string album = "AL", int seconds = 0)
        {
            return new Track
            {
                Title = title, Artist = artist, Album = album,
                Duration = TimeSpan.FromSeconds(seconds),
                FilePath = "x.mp3", FileName = "x.mp3",
            };
        }

        [Fact]
        public void DurationText_Zero_ShowsPlaceholder()
        {
            Assert.Equal("--:--", T(seconds: 0).DurationText);
        }

        [Fact]
        public void DurationText_FormatsMinutesSeconds()
        {
            Assert.Equal("2:05", T(seconds: 125).DurationText);
            Assert.Equal("0:01", T(seconds: 1).DurationText);
            Assert.Equal("12:59", T(seconds: 779).DurationText);
        }

        [Fact]
        public void SubText_UnknownArtist_Fallback()
        {
            // 无歌手且无专辑 → 纯"未知歌手"；有专辑时附加专辑名
            Assert.Equal("未知歌手", T(artist: "", album: "").SubText);
            Assert.Equal("未知歌手 · AL", T(artist: "", album: "AL").SubText);
        }

        [Fact]
        public void SubText_AlbumSameAsTitle_NotRepeated()
        {
            Assert.Equal("A", T(title: "单曲", artist: "A", album: "单曲").SubText);
        }

        [Fact]
        public void SubText_AlbumDifferent_ShowsArtistAndAlbum()
        {
            Assert.Equal("A · AL", T(title: "T", artist: "A", album: "AL").SubText);
        }

        [Fact]
        public void HasLrc_NullOrEmpty_False()
        {
            Assert.False(T().HasLrc);
            var t = T(); t.LrcText = ""; Assert.False(t.HasLrc);
            t.LrcText = "[00:01]x"; Assert.True(t.HasLrc);
        }

        [Fact]
        public void ArtistChange_RaisesSubTextNotification()
        {
            var t = T();
            string? notified = null;
            t.PropertyChanged += (s, e) => { if (e.PropertyName == "SubText") notified = e.PropertyName; };
            t.Artist = "新歌手";
            Assert.Equal("SubText", notified);
        }
    }
}
