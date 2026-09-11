using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Aurora.Tests
{
    public sealed class UpdateCheckerTests : IDisposable
    {
        const string SetupUrl = AppInfo.ReleasesUrl + "/download/v3.1.0/AuroraPlayer-Setup.exe";
        readonly string directory = Path.Combine(Path.GetTempPath(), "Aurora-UpdateTests-" + Guid.NewGuid().ToString("N"));
        string Target => Path.Combine(directory, "setup.exe");

        public UpdateCheckerTests() => Directory.CreateDirectory(directory);
        public void Dispose() => Directory.Delete(directory, true);

        [Fact]
        public void ParseRelease_HandlesFieldOrderAndJsonEscapes()
        {
            string json = "{\"assets\":[{\"browser_download_url\":\"" + SetupUrl + "\",\"digest\":null,\"size\":12,\"name\":\"AuroraPlayer-Setup.exe\"}]," +
                "\"body\":\"line1\\n\\u4e2d\\t\\\"quoted\\\"\\\\path\",\"tag_name\":\"v3.1.0\"}";
            var info = UpdateChecker.ParseRelease(json);
            Assert.NotNull(info);
            Assert.Equal("3.1.0", info.Version);
            Assert.Equal("line1\n中\t\"quoted\"\\path", info.Notes);
            Assert.Equal(SetupUrl, info.SetupUrl);
            Assert.Equal(12L, info.SetupSize);
        }

        [Theory]
        [InlineData("../3.1.0")]
        [InlineData("3.1.0/../../evil")]
        [InlineData("3.1.0\\evil")]
        [InlineData("v3.1")]
        [InlineData("3.1.0-preview")]
        [InlineData("3.1.0+meta")]
        [InlineData("03.1.0")]
        [InlineData("2147483648.1.0")]
        [InlineData(" 3.1.0")]
        [InlineData("vv3.1.0")]
        public void ParseRelease_RejectsInvalidVersions(string version)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new { tag_name = version });
            Assert.Null(UpdateChecker.ParseRelease(json));
            Assert.False(UpdateChecker.IsNewer(version, "3.0.0"));
        }

        [Fact]
        public void ParseRelease_RejectsDraftAndMismatchedAsset()
        {
            Assert.Null(UpdateChecker.ParseRelease("{\"tag_name\":\"3.1.0\",\"draft\":true}"));
            Assert.Null(UpdateChecker.ParseRelease("{\"tag_name\":\"3.1.0\",\"prerelease\":true}"));
            var info = UpdateChecker.ParseRelease(System.Text.Json.JsonSerializer.Serialize(new
            {
                tag_name = "3.2.0",
                assets = new[] { new { name = "AuroraPlayer-Setup.exe", browser_download_url = SetupUrl } }
            }));
            Assert.NotNull(info);
            Assert.Null(info.SetupUrl);
            Assert.True(UpdateChecker.IsNewer("v3.1.10", "3.1.9"));
            Assert.False(UpdateChecker.IsNewer("3.1.0", "3.1.0"));
        }

        [Theory]
        [InlineData("http://github.com/linx-sys/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe")]
        [InlineData("https://github.com.evil.invalid/linx-sys/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe")]
        [InlineData("https://github.com/other/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe")]
        [InlineData("https://github.com/linx-sys/Aurora/releases/download/v3.1.0/evil.exe")]
        [InlineData("https://user@github.com/linx-sys/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe")]
        [InlineData("https://github.com:444/linx-sys/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe")]
        [InlineData("https://github.com/linx-sys/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe?redirect=evil")]
        [InlineData("https://github.com/linx-sys/Aurora/releases/download/v3.1.0/AuroraPlayer-Setup.exe#fragment")]
        [InlineData("https://github.com/linx-sys/Aurora/releases/download/v3.1.0%2fevil/AuroraPlayer-Setup.exe")]
        public async Task Download_RejectsUntrustedInitialUrlBeforeRequest(string url)
        {
            int requests = 0;
            using var client = Client(_ => { requests++; return Ok(Array.Empty<byte>()); });
            Assert.False(UpdateChecker.IsTrustedSetupUrl(url));
            await Assert.ThrowsAsync<ArgumentException>(() => UpdateChecker.DownloadSetupAsync(url, Target, client: client));
            Assert.Equal(0, requests);
            Assert.Empty(Directory.GetFiles(directory));
        }

        [Fact]
        public async Task FetchLatest_UsesInjectedClientOnly()
        {
            using var client = Client(request =>
            {
                Assert.Equal(AppInfo.ReleasesApi, request.RequestUri!.AbsoluteUri);
                Assert.NotEmpty(request.Headers.UserAgent);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"v3.1.0\"}") };
            });
            var info = await UpdateChecker.FetchLatestAsync(client: client);
            Assert.Equal("3.1.0", info!.Version);
        }

        [Fact]
        public async Task Download_VerifiesDigestThenReplacesOldFile()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("verified installer");
            File.WriteAllText(Target, "old installer");
            using var client = Client(_ => Ok(bytes));
            var info = new UpdateChecker.UpdateInfo
            {
                Version = "3.1.0", SetupUrl = SetupUrl, SetupSize = bytes.Length,
                SetupDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes))
            };
            Assert.True(await UpdateChecker.DownloadSetupAsync(info, Target, client: client));
            Assert.Equal(bytes, File.ReadAllBytes(Target));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
        }

        [Fact]
        public async Task Download_UnknownLengthStillRejectsEmptyBody()
        {
            File.WriteAllText(Target, "old");
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ControlledStream(Array.Empty<byte>()))
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            AssertOldTarget();
        }

        [Fact]
        public async Task Download_HttpErrorPreservesOldFile()
        {
            File.WriteAllText(Target, "old");
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            await Assert.ThrowsAsync<HttpRequestException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            AssertOldTarget();
        }

        [Theory]
        [InlineData(2)]
        [InlineData(20)]
        public async Task Download_LengthMismatchPreservesOldFile(long declaredLength)
        {
            File.WriteAllText(Target, "old");
            using var client = Client(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new ControlledStream(Encoding.UTF8.GetBytes("payload"))) };
                response.Content.Headers.ContentLength = declaredLength;
                return response;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            AssertOldTarget();
        }

        [Fact]
        public async Task Download_ReleaseSizeMismatchPreservesOldFile()
        {
            File.WriteAllText(Target, "old");
            using var client = Client(_ => Ok(Encoding.UTF8.GetBytes("payload")));
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client, expectedSize: 99));
            AssertOldTarget();
        }

        [Theory]
        [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000")]
        [InlineData("sha256:invalid")]
        [InlineData("md5:00000000000000000000000000000000")]
        public async Task Download_BadDigestPreservesOldFile(string digest)
        {
            File.WriteAllText(Target, "old");
            using var client = Client(_ => Ok(Encoding.UTF8.GetBytes("payload")));
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client, digest: digest));
            AssertOldTarget();
        }

        [Fact]
        public async Task Download_ReadFailureCleansPartAndPreservesOldFile()
        {
            File.WriteAllText(Target, "old");
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ControlledStream(Encoding.UTF8.GetBytes("payload"), () => throw new IOException("中途断开")))
            });
            await Assert.ThrowsAsync<IOException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            AssertOldTarget();
        }

        [Fact]
        public async Task Download_CancellationDuringReadCleansPartAndPreservesOldFile()
        {
            File.WriteAllText(Target, "old");
            using var cancellation = new CancellationTokenSource();
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ControlledStream(Encoding.UTF8.GetBytes("payload"), cancellation.Cancel))
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UpdateChecker.DownloadSetupAsync(SetupUrl, Target, cancellationToken: cancellation.Token, client: client));
            AssertOldTarget();
        }

        [Fact]
        public async Task Download_PreCancelledDoesNotSendRequest()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var client = Client(_ => throw new InvalidOperationException("不应发起请求"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UpdateChecker.DownloadSetupAsync(SetupUrl, Target, cancellationToken: cancellation.Token, client: client));
            Assert.Empty(Directory.GetFiles(directory));
        }

        [Theory]
        [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset-test/payload")]
        [InlineData("https://evil.invalid/payload")]
        [InlineData("https://release-assets.githubusercontent.com.evil.invalid/github-production-release-asset-test/payload")]
        [InlineData("https://github.com/other/project/releases/download/v3.1.0/AuroraPlayer-Setup.exe")]
        public async Task Download_RejectsUnsafeRedirectBeforeFollowing(string redirect)
        {
            File.WriteAllText(Target, "old");
            int requests = 0;
            using var client = Client(_ =>
            {
                requests++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(redirect);
                return response;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            Assert.Equal(1, requests);
            AssertOldTarget();
        }

        [Fact]
        public async Task Download_AllowsValidatedGithubAssetRedirect()
        {
            int requests = 0;
            byte[] payload = Encoding.UTF8.GetBytes("payload");
            using var client = Client(request =>
            {
                if (++requests == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset-test/payload?signature=test");
                    return redirect;
                }
                Assert.Equal("https", request.RequestUri!.Scheme);
                return Ok(payload);
            });
            Assert.True(await UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            Assert.Equal(2, requests);
            Assert.Equal(payload, File.ReadAllBytes(Target));
        }

        [Fact]
        public async Task Download_RejectsClientThatAutomaticallyRedirected()
        {
            using var client = Client(_ =>
            {
                var response = Ok(Encoding.UTF8.GetBytes("payload"));
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://github.com/");
                return response;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadSetupAsync(SetupUrl, Target, client: client));
            Assert.Empty(Directory.GetFiles(directory));
        }

        [Fact]
        public void BuiltInProviders_ObservePreCancellationBeforeHttp()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            ILyricsProvider[] providers = { new KugouProvider(), new NeteaseProvider() };
            foreach (var provider in providers)
                Assert.ThrowsAny<OperationCanceledException>(() => provider.Match(new NetMatchResult(), "song.mp3", "title", "artist", cancellation.Token));
        }

        void AssertOldTarget()
        {
            Assert.Equal("old", File.ReadAllText(Target));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
        }

        static HttpResponseMessage Ok(byte[] bytes) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) => new HttpClient(new FakeHandler(response));

        sealed class FakeHandler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, HttpResponseMessage> response;
            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => this.response = response;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(response(request));
            }
        }

        sealed class ControlledStream : Stream
        {
            readonly byte[] bytes;
            readonly Action? onSecondRead;
            int position;
            int reads;
            public ControlledStream(byte[] bytes, Action? onSecondRead = null)
            { this.bytes = bytes; this.onSecondRead = onSecondRead; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (++reads == 2) onSecondRead?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(3, Math.Min(buffer.Length, bytes.Length - position));
                bytes.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return ValueTask.FromResult(count);
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
