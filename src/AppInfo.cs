/* ============================================================
 * AppInfo.cs — 应用元信息（版本 / 仓库地址）
 * 主程序与安装器共用（installer.csproj 也编译本文件）。
 * ============================================================ */
namespace Aurora
{
    public static class AppInfo
    {
        /// <summary>应用版本（更新检查据此比较；发布时打 tag v{Version}）。</summary>
        public const string Version = "3.0.0";

        public const string RepoUrl = "https://github.com/linx-sys/Aurora";
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string ReleasesApi = "https://api.github.com/repos/linx-sys/Aurora/releases/latest";

        /// <summary>
        /// 是否便携版（P2-5 更新体验分流）：单文件发布（PublishSingleFile）时
        /// EntryAssembly.Location 为空串；安装版运行 AuroraPlayer.dll，Location 非空。
        /// </summary>
        public static bool IsPortable
        {
            get
            {
                try { return string.IsNullOrEmpty(System.Reflection.Assembly.GetEntryAssembly()?.Location); }
                catch { return false; }
            }
        }
    }
}
