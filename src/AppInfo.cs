/* ============================================================
 * AppInfo.cs — 应用元信息（版本 / 仓库地址）
 * 主程序、安装器与卸载器共用。
 * ============================================================ */
using System.Runtime.Versioning;

// GenerateAssemblyInfo 已关闭，显式声明与各工程目标框架一致的平台基线。
#if WINDOWS10_0_19041_0_OR_GREATER
[assembly: SupportedOSPlatform("windows10.0.17763.0")]
#else
[assembly: SupportedOSPlatform("windows7.0")]
#endif

namespace Aurora
{
    public static class AppInfo
    {
        /// <summary>应用版本（更新检查据此比较；发布时打 tag v{Version}）。</summary>
        public const string Version = "3.0.1";

        public const string RepoUrl = "https://github.com/linx-sys/Aurora";
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string ReleasesApi = "https://api.github.com/repos/linx-sys/Aurora/releases/latest";

        /// <summary>
        /// 是否便携版（P2-5 更新体验分流）：由主程序的 PublishSingleFile 构建属性决定，
        /// 避免依赖单文件包中不可用的程序集文件路径。
        /// </summary>
        public static bool IsPortable
        {
            get
            {
#if AURORA_SINGLE_FILE
                return true;
#else
                return false;
#endif
            }
        }
    }
}
