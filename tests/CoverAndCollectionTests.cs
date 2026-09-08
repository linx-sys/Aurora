/* ============================================================
 * CoverAndCollectionTests.cs — 封面配色 + 批量集合单元测试
 * 覆盖：PaletteFor 确定性/边界、BatchObservableCollection.ReplaceAll
 *       的单次 Reset 语义与标准集合行为。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Xunit;

namespace Aurora.Tests
{
    public class CoverArtTests
    {
        [Fact]
        public void PaletteFor_Deterministic()
        {
            var a = CoverArt.PaletteFor("晴天", "周杰伦");
            var b = CoverArt.PaletteFor("晴天", "周杰伦");
            Assert.Equal(a[0], b[0]);
            Assert.Equal(a[a.Length - 1], b[b.Length - 1]);
        }

        [Fact]
        public void PaletteFor_NullOrEmptyTitle_DoesNotThrow()
        {
            Assert.NotEmpty(CoverArt.PaletteFor(null, "x"));
            Assert.NotEmpty(CoverArt.PaletteFor("", "x"));
            Assert.NotEmpty(CoverArt.PaletteFor("t", null));
        }

        [Fact]
        public void PaletteFor_DifferentSongs_CanMapDifferentPalettes()
        {
            // 大样本下应出现多种配色（哈希分布；3 首歌全撞同一组的概率极低）
            var distinct = new[] { "A", "B", "C", "D", "E", "F", "G", "H" }
                .Select(t => CoverArt.PaletteFor(t, "artist")[0])
                .Distinct().Count();
            Assert.True(distinct >= 2, "8 首歌应映射到至少 2 种配色");
        }
    }

    public class BatchObservableCollectionTests
    {
        static BatchObservableCollection<string> Make(params string[] items)
        {
            var c = new BatchObservableCollection<string>();
            foreach (var i in items) c.Add(i);
            return c;
        }

        [Fact]
        public void ReplaceAll_ReplacesContent()
        {
            var c = Make("a", "b", "c");
            c.ReplaceAll(new[] { "x", "y" });
            Assert.Equal(new[] { "x", "y" }, c);
        }

        [Fact]
        public void ReplaceAll_RaisesExactlyOneCollectionChange()
        {
            // 回归测试：ReplaceAll 直接操作底层存储，只发一次 Reset
            // （Clear + 逐个 Add 会发 n+1 次通知，大列表重建是 O(n²)）
            var c = Make("a", "b", "c");
            var events = new List<NotifyCollectionChangedAction>();
            c.CollectionChanged += (s, e) => events.Add(e.Action);

            c.ReplaceAll(new[] { "x", "y", "z", "w" });
            Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Reset, events[0]);
        }

        [Fact]
        public void ReplaceAll_NotifiesCountProperty()
        {
            var c = Make("a", "b");
            var props = new List<string>();
            ((System.ComponentModel.INotifyPropertyChanged)c).PropertyChanged += (s, e) => props.Add(e.PropertyName);

            c.ReplaceAll(new[] { "x" });
            Assert.Contains("Count", props);
            Assert.Contains("Item[]", props);
        }

        [Fact]
        public void ReplaceAll_WithEmpty_ClearsCollection()
        {
            var c = Make("a", "b");
            c.ReplaceAll(new string[0]);
            Assert.Empty(c);
        }

        [Fact]
        public void Add_Remove_StandardNotifications()
        {
            var c = new BatchObservableCollection<string>();
            var actions = new List<NotifyCollectionChangedAction>();
            c.CollectionChanged += (s, e) => actions.Add(e.Action);

            c.Add("a");
            c.Remove("a");
            Assert.Equal(2, actions.Count);
            Assert.Equal(NotifyCollectionChangedAction.Add, actions[0]);
            Assert.Equal(NotifyCollectionChangedAction.Remove, actions[1]);
        }
    }
}
