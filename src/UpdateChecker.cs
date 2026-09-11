/* ============================================================
 * UpdateChecker.cs — GitHub Release 检查与安装包安全下载。
 * digest 与安装包同源，只用于完整性校验，不等同于独立可信签名。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Aurora
{
    public static class UpdateChecker
    {
        const string LastCheckKey = "update_last_check";
        const string SetupName = "AuroraPlayer-Setup.exe";
        static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        // 自己逐跳校验 Location，禁止自动跳转绕过 HTTPS/域名限制。
        static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(10) };

        public class UpdateInfo
        {
            public string Version = "";
            public string? Notes;
            public string? SetupUrl;
            public long? SetupSize;
            public string? SetupDigest;
        }

        public sealed class ReleaseDto
        {
            [JsonPropertyName("tag_name")] public string? TagName { get; set; }
            [JsonPropertyName("body")] public string? Body { get; set; }
            [JsonPropertyName("draft")] public bool Draft { get; set; }
            [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
            [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
        }

        public sealed class AssetDto
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
            [JsonPropertyName("size")] public long? Size { get; set; }
            [JsonPropertyName("digest")] public string? Digest { get; set; }
        }

        public static async Task CheckAsync(Action<UpdateInfo> onNewVersion, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested || Settings.Get("update_check", "1") == "0") return;
            long now = DateTime.UtcNow.Ticks;
            long.TryParse(Settings.Get(LastCheckKey, "0"), out long last);
            if (last > 0 && now >= last && now - last < CheckInterval.Ticks) return;
            Settings.Set(LastCheckKey, now.ToString(CultureInfo.InvariantCulture));
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var info = await FetchLatestAsync(timeout.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (info != null && IsNewer(info.Version, AppInfo.Version)) onNewVersion(info);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MainViewModel.Dbg("Update check FAIL: " + ex.Message); }
        }

        /// <summary>注入的客户端必须禁用自动重定向；测试可使用纯内存 HttpMessageHandler。</summary>
        public static async Task<UpdateInfo?> FetchLatestAsync(CancellationToken cancellationToken = default, HttpClient? client = null)
        {
            using var response = await SendAsync(client ?? Client, new Uri(AppInfo.ReleasesApi), false, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseRelease(json);
        }

        public static UpdateInfo? ParseRelease(string json)
        {
            var release = JsonSerializer.Deserialize<ReleaseDto>(json);
            if (release == null || release.Draft || release.Prerelease ||
                !TryParseVersion(release.TagName, out string version, out _)) return null;
            var info = new UpdateInfo { Version = version, Notes = release.Body };
            if (info.Notes?.Length > 800) info.Notes = info.Notes.Substring(0, 800) + "\n…（更多见发布页）";
            if (release.Assets != null)
            {
                foreach (var asset in release.Assets)
                {
                    if (asset == null || asset.Name != SetupName || !IsTrustedSetupUrl(asset.DownloadUrl)) continue;
                    var uri = new Uri(asset.DownloadUrl!);
                    string tag = uri.AbsolutePath.Split('/')[5];
                    if (!TryParseVersion(tag, out string assetVersion, out _) || assetVersion != version) continue;
                    info.SetupUrl = asset.DownloadUrl;
                    info.SetupSize = asset.Size;
                    info.SetupDigest = asset.Digest;
                    break;
                }
            }
            return info;
        }

        public static bool IsTrustedSetupUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsHttps(uri) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0) return false;
            string[] parts = uri.AbsolutePath.Split('/');
            string projectPath = new Uri(AppInfo.RepoUrl).AbsolutePath;
            return parts.Length == 7 && uri.AbsolutePath.StartsWith(projectPath + "/releases/download/", StringComparison.Ordinal) &&
                parts[6] == SetupName && TryParseVersion(parts[5], out _, out _);
        }

        static bool IsHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
            uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

        static bool IsAllowedDownloadUri(Uri uri)
        {
            if (!IsHttps(uri)) return false;
            if (IsTrustedSetupUrl(uri.AbsoluteUri)) return true;
            return (string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) &&
                uri.AbsolutePath.StartsWith("/github-production-release-asset-", StringComparison.Ordinal);
        }

        static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, bool download, CancellationToken token)
        {
            for (int redirects = 0; ; redirects++)
            {
                token.ThrowIfCancellationRequested();
                bool allowed = download ? IsAllowedDownloadUri(uri) : uri.AbsoluteUri == AppInfo.ReleasesApi;
                if (!allowed) throw new InvalidDataException("更新地址不是受信任的 HTTPS GitHub 项目地址。");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("AuroraPlayer/" + AppInfo.Version);
                if (!download) request.Headers.Accept.ParseAdd("application/vnd.github+json");
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                try
                {
                    if (response.RequestMessage?.RequestUri is Uri actual && actual != uri)
                        throw new InvalidDataException("更新客户端不得自动重定向。");
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        var location = response.Headers.Location;
                        if (redirects >= 5 || location == null) throw new InvalidDataException("更新重定向无效或过多。");
                        uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        response.Dispose();
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                catch { response.Dispose(); throw; }
            }
        }

        /// <summary>兼容旧调用；UI 应使用可取消的异步方法。</summary>
        public static bool DownloadSetup(string url, string target, Action<int> onPercent) =>
            DownloadSetupAsync(url, target, onPercent).GetAwaiter().GetResult();

        public static Task<bool> DownloadSetupAsync(UpdateInfo info, string target, Action<int>? onPercent = null,
            CancellationToken cancellationToken = default, HttpClient? client = null)
        {
            if (!TryParseVersion(info.Version, out _, out _)) throw new ArgumentException("更新版本无效。", nameof(info));
            if (!IsTrustedSetupUrl(info.SetupUrl) ||
                !TryParseVersion(new Uri(info.SetupUrl!).AbsolutePath.Split('/')[5], out string assetVersion, out _) ||
                !TryParseVersion(info.Version, out string version, out _) || assetVersion != version)
                throw new ArgumentException("安装包与更新版本不匹配。", nameof(info));
            return DownloadSetupAsync(info.SetupUrl!, target, onPercent, cancellationToken, client, info.SetupSize, info.SetupDigest);
        }

        /// <summary>同目录临时文件全部校验成功后原子提交；失败不覆盖旧安装包。</summary>
        public static async Task<bool> DownloadSetupAsync(string url, string target, Action<int>? onPercent = null,
            CancellationToken cancellationToken = default, HttpClient? client = null, long? expectedSize = null, string? digest = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTrustedSetupUrl(url)) throw new ArgumentException("安装包地址不可信。", nameof(url));
            if (expectedSize.HasValue && expectedSize.Value <= 0) throw new InvalidDataException("安装包大小无效。");
            byte[]? expectedHash = ParseDigest(digest);
            string fullTarget = Path.GetFullPath(target);
            string part = Path.Combine(Path.GetDirectoryName(fullTarget)!, "." + Path.GetFileName(fullTarget) + "." + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using var response = await SendAsync(client ?? Client, new Uri(url), true, cancellationToken).ConfigureAwait(false);
                long? length = response.Content.Headers.ContentLength;
                if (length.HasValue && (length.Value <= 0 || (expectedSize.HasValue && expectedSize.Value != length.Value)))
                    throw new InvalidDataException("安装包长度与发布信息不符。");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long done = 0;
                int lastPercent = -1;
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    int n;
                    while ((n = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        done = checked(done + n);
                        if ((length.HasValue && done > length.Value) || (expectedSize.HasValue && done > expectedSize.Value))
                            throw new InvalidDataException("安装包超出预期长度。");
                        await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, n);
                        long total = length ?? expectedSize ?? 0;
                        if (total > 0)
                        {
                            int percent = Math.Min(90, (int)(100d * done / total) / 10 * 10);
                            if (percent != lastPercent) { lastPercent = percent; onPercent?.Invoke(percent); }
                        }
                    }
                    if (done == 0 || (length.HasValue && done != length.Value) || (expectedSize.HasValue && done != expectedSize.Value))
                        throw new InvalidDataException("安装包为空或下载未完成。");
                    if (expectedHash != null && !CryptographicOperations.FixedTimeEquals(expectedHash, hash.GetHashAndReset()))
                        throw new InvalidDataException("安装包 SHA-256 校验失败。");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(fullTarget)) File.Replace(part, fullTarget, null);
                else File.Move(part, fullTarget);
                return true;
            }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); }
                catch (Exception ex) { MainViewModel.Dbg("Update part cleanup FAIL: " + ex.Message); }
            }
        }

        static byte[]? ParseDigest(string? digest)
        {
            if (string.IsNullOrEmpty(digest)) return null;
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
                throw new InvalidDataException("安装包摘要格式无效。");
            try { return Convert.FromHexString(digest.Substring(7)); }
            catch (FormatException ex) { throw new InvalidDataException("安装包摘要格式无效。", ex); }
        }

        /// <summary>仅接受稳定版三段非负整数，可带单个 v 前缀，不接受路径或版本后缀。</summary>
        static bool TryParseVersion(string? value, out string normalized, out int[] numbers)
        {
            normalized = "";
            numbers = new int[3];
            if (string.IsNullOrEmpty(value) || value.Length > 33) return false;
            string core = value[0] == 'v' || value[0] == 'V' ? value.Substring(1) : value;
            string[] parts = core.Split('.');
            if (parts.Length != 3) return false;
            for (int i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0')) return false;
                foreach (char c in parts[i]) if (c < '0' || c > '9') return false;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
            }
            normalized = core;
            return true;
        }

        public static bool IsNewer(string candidate, string current)
        {
            if (!TryParseVersion(candidate, out _, out int[] a) || !TryParseVersion(current, out _, out int[] b)) return false;
            for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i] > b[i];
            return false;
        }
    }
}
