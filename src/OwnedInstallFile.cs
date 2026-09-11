/* Windows 文件身份保护：禁止写入/重命名祖先，按已打开句柄校验并隔离确切文件。
 * 不使用“按路径校验后 File.Delete/Replace”的检查使用分离模式。
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aurora
{
    internal static class OwnedInstallFile
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag info, uint size);
        [StructLayout(LayoutKind.Sequential)]
        struct AttributeTag { public uint Attributes; public uint Tag; }
        static IOException Error(string path) => new IOException("文件操作失败：" + path, new Win32Exception(Marshal.GetLastWin32Error()));
        // 原生调用不享受 BCL 的长路径支持，必须显式转为扩展长度路径，
        // 否则深层安装目录（含 .AuroraInstall-<sha256> 恢复目录）会撞 MAX_PATH 报“文件名或扩展名太长”。
        static string Extended(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }
        static void RejectLink(SafeFileHandle handle, string path)
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var info, 8)) throw Error(path);
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("不操作链接路径：" + path);
        }
        internal sealed class DirectoryGuard : IDisposable
        {
            readonly List<SafeFileHandle> handles = new();
            internal DirectoryGuard(params string[] directories)
            {
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string directory in directories)
                    {
                        var chain = new Stack<string>();
                        for (string? p = Path.GetFullPath(directory); p != null; p = Path.GetDirectoryName(p)) chain.Push(p);
                        foreach (string p in chain)
                        {
                            if (!seen.Add(p)) continue;
                            var h = CreateFileW(Extended(p), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                            if (h.IsInvalid)
                            {
                                int code = Marshal.GetLastWin32Error(); h.Dispose();
                                if (code == 2 || code == 3) break;
                                throw Error(p);
                            }
                            handles.Add(h);
                            RejectLink(h, p);
                        }
                    }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
        }
        // 返回 false 仅代表文件不存在或内容已改变。访问/共享错误向上传播，不能伪装成功。
        internal static bool MoveVerified(string source, string destination, string hash, Action? whileLocked = null)
        {
            using var dirs = new DirectoryGuard(Path.GetDirectoryName(source)!, Path.GetDirectoryName(destination)!);
            using var handle = CreateFileW(Extended(source), 0x80010000, 0, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                if (code == 2 || code == 3) return false;
                throw Error(source);
            }
            RejectLink(handle, source);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase)) return false;
            whileLocked?.Invoke();
            byte[] name = Encoding.Unicode.GetBytes(Extended(destination));
            int lengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int nameOffset = lengthOffset + 4;
            // 名字后必须留 2 字节 NUL 终止符：内核按终止符读文件名字符串，只给 FileNameLength 会在
            // 分配区外继续读，把相邻堆内存写进文件名（实测产生 "AuroraPlayer.exeon" 这类乱码尾巴）。
            int size = nameOffset + name.Length + 2;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buffer, size);
                Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) throw Error(source);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return true;
        }
        internal static string? ReadHash(string path)
        {
            InstallManifest.EnsureNoReparsePoints(path);
            try { return InstallManifest.HashFile(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }
}
