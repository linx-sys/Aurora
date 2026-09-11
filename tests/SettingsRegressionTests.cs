using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Aurora.Tests
{
    public class SettingsRegressionTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "aurora_settings_" + Guid.NewGuid().ToString("N"));
        string FilePath => Path.Combine(root, "settings.ini");
        enum TestMode { First, Second }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [Fact]
        public void TypedGetters_RejectInvalidOutOfRangeAndNonFiniteValues()
        {
            var store = new SettingsStore(FilePath);
            store.Set("integer", "999999999999999");
            Assert.Equal(3, store.GetInt("integer", 3, 0, 5));
            store.Set("integer", "-1");
            Assert.Equal(3, store.GetInt("integer", 3, 0, 5));
            store.Set("integer", "5");
            Assert.Equal(5, store.GetInt("integer", 3, 0, 5));
            foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "1e999", "nonsense", "2" })
            {
                store.Set("double", invalid);
                Assert.Equal(0.5, store.GetDouble("double", 0.5, 0, 1));
            }
            store.Set("double", "0.75");
            Assert.Equal(0.75, store.GetDouble("double", 0.5, 0, 1));
            store.Set("mode", "987");
            Assert.Equal(TestMode.First, store.GetEnum("mode", TestMode.First));
            store.Set("mode", "second");
            Assert.Equal(TestMode.Second, store.GetEnum("mode", TestMode.First));
        }

        [Fact]
        public void MultilineValues_RoundTripWithoutInjectingKeys()
        {
            var store = new SettingsStore(FilePath);
            string value = " D:\\Music\\a.mp3\r\nD:\\Music\\b.mp3\nother=not-a-setting\n末尾 ";
            store.Set("jumpList", value);
            store.Set("other", "original");
            var reopened = new SettingsStore(FilePath);
            Assert.Equal(value, reopened.Get("jumpList", ""));
            Assert.Equal("original", reopened.Get("other", ""));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }

        [Fact]
        public void LegacyIni_IsReadAndMigratedWithoutLosingValues()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(FilePath, "volume=0.75\r\nlastDir=D:\\音乐\r\nurl=a=b\r\n");
            var store = new SettingsStore(FilePath);
            Assert.Equal("D:\\音乐", store.Get("lastDir", ""));
            Assert.Equal("a=b", store.Get("url", ""));
            store.Set("new", "line1\nline2");
            var reopened = new SettingsStore(FilePath);
            Assert.Equal(0.75, reopened.GetDouble("volume", 0));
            Assert.Equal("D:\\音乐", reopened.Get("lastDir", ""));
            Assert.Equal("line1\nline2", reopened.Get("new", ""));
        }

        [Fact]
        public async Task ConcurrentAccess_PersistsEveryKeyAsOneAtomicSnapshot()
        {
            var store = new SettingsStore(FilePath);
            var tasks = new Task[40];
            for (int i = 0; i < tasks.Length; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() =>
                {
                    store.Set("key" + index, "value\n" + index);
                    Assert.Equal("value\n" + index, store.Get("key" + index, ""));
                });
            }
            await Task.WhenAll(tasks);
            var reopened = new SettingsStore(FilePath);
            for (int i = 0; i < tasks.Length; i++) Assert.Equal("value\n" + i, reopened.Get("key" + i, ""));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
    }
}
