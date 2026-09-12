using System;
using System.IO;
using Xunit;

namespace Aurora.Tests
{
    /// <summary>
    /// 文件关联路径解析回归。
    /// 背景：`Assoc.Register` 曾用 `Assembly.GetExecutingAssembly().Location` 取自身路径，
    /// 而框架依赖应用里该属性返回的是 `AuroraPlayer.dll`（单文件发布下是空串），
    /// 于是关联命令被写成 `"…\AuroraPlayer.dll" "%1"`，用"打开方式 → Aurora"打开文件必然失败。
    /// </summary>
    public sealed class AssocTests
    {
        [Fact]
        public void ResolveExePathPrefersProcessPath()
        {
            Assert.Equal(@"C:\App\AuroraPlayer.exe",
                Assoc.ResolveExePath(@"C:\App\AuroraPlayer.exe", @"C:\App\"));
        }

        [Fact]
        public void ResolveExePathFallsBackToAppHostNotAssemblyDll()
        {
            string path = Assoc.ResolveExePath(null!, @"C:\App\");
            Assert.Equal(Path.Combine(@"C:\App\", "AuroraPlayer.exe"), path);
            Assert.EndsWith("AuroraPlayer.exe", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".dll", path, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResolveExePathFallsBackToBaseDirectoryWhenBothMissing()
        {
            string path = Assoc.ResolveExePath(null!, null!);
            Assert.EndsWith("AuroraPlayer.exe", path, StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathFullyQualified(path), path);
        }
    }
}
