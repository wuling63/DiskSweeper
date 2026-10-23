using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskCleaner
{
    /// <summary>資源回收筒：查總大小與項目數、清空。</summary>
    public static class RecycleBin
    {
        // shellapi.h 在 32 位元與 64 位元的對齊不同（cbSize 後面有沒有補 4 個位元組），
        // cbSize 對不上會回 E_INVALIDARG，所以兩種排法都備著，先試自然對齊。
        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SHQUERYRBINFO_PACK4
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBin")]
        private static extern int SHQueryRecycleBinPack4(string pszRootPath, ref SHQUERYRBINFO_PACK4 pSHQueryRBInfo);

        private const int SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, int dwFlags);

        public static bool Query(out long size, out long count)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                size = info.i64Size;
                count = info.i64NumItems;
                return true;
            }

            var packed = new SHQUERYRBINFO_PACK4 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO_PACK4)) };
            int hr = SHQueryRecycleBinPack4(null, ref packed);
            size = packed.i64Size;
            count = packed.i64NumItems;
            return hr == 0;
        }

        // SHFILEOPSTRUCT 在 32 位元是 1 位元組對齊、64 位元是自然對齊，兩種都備著依行程位元數挑。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd;
            public int wFunc;
            public string pFrom;
            public string pTo;
            public short fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd;
            public int wFunc;
            public string pFrom;
            public string pTo;
            public short fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        private const int FO_DELETE = 3;
        private const short FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
                            FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>
        /// 把檔案或資料夾送進資源回收筒（一次可以很多個）。
        /// 進不了回收筒的（例如隨身碟、太大的檔案）會跳出系統詢問要不要直接刪除，不會默默永久刪掉。
        /// </summary>
        public static bool Send(IEnumerable<string> paths)
        {
            // pFrom 是用 \0 分隔、結尾兩個 \0 的清單。
            string from = string.Join("\0", paths) + "\0\0";
            short flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING;

            if (IntPtr.Size == 8)
            {
                var op = new SHFILEOPSTRUCT64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                return SHFileOperation64(ref op) == 0 && !op.fAnyOperationsAborted;
            }
            else
            {
                var op = new SHFILEOPSTRUCT32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                return SHFileOperation32(ref op) == 0 && !op.fAnyOperationsAborted;
            }
        }

        public static bool Empty()
        {
            // 已經是空的時候會回 E_UNEXPECTED，當成成功。
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 || hr == unchecked((int)0x8000FFFF);
        }
    }

    /// <summary>讀 .lnk 的目標路徑（IShellLink），判斷捷徑是不是已經失效。</summary>
    public static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            // 後面的方法用不到，不宣告也不影響 vtable 前段的呼叫。
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        }

        private const int SLGP_RAWPATH = 0x4;

        public static string Target(string lnk)
        {
            object link = null;
            try
            {
                link = new CShellLink();
                ((IPersistFile)link).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
                return Environment.ExpandEnvironmentVariables(sb.ToString());
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
            }
        }

        /// <summary>
        /// 只有「目標在本機固定磁碟、而且確定不存在」才算失效。
        /// 沒有路徑的（MSI 宣告式捷徑、「本機」這類殼層項目）、網路磁碟、隨身碟上的一律當成有效，寧可漏掉也不誤刪。
        /// </summary>
        public static bool IsBroken(string lnk)
        {
            string target = Target(lnk);
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target)) return false;
            if (target.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(target));
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return false;
            }
            catch (Exception)
            {
                return false;
            }

            if (Exists(target)) return false;

            // 32 位元程式讀到的 %ProgramFiles% 會被換成「Program Files (x86)」，
            // 兩個 Program Files 都找過才算真的不在。
            string native = Environment.GetEnvironmentVariable("ProgramW6432");
            string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var pair in new[] { new[] { x86, native }, new[] { native, x86 } })
            {
                if (string.IsNullOrEmpty(pair[0]) || string.IsNullOrEmpty(pair[1]) || pair[0] == pair[1]) continue;
                if (target.StartsWith(pair[0] + "\\", StringComparison.OrdinalIgnoreCase)
                    && Exists(pair[1] + target.Substring(pair[0].Length)))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Exists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }
    }

    /// <summary>系統內建圖示（資料夾、回收筒、捷徑…），樹狀清單用。</summary>
    public static class StockIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public int cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        private const int SHGSI_ICON = 0x100, SHGSI_SMALLICON = 0x1;

        [DllImport("shell32.dll")]
        private static extern int SHGetStockIconInfo(int siid, int uFlags, ref SHSTOCKICONINFO psii);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public const int Folder = 3, RecyclerFull = 32, Link = 29, World = 13, Shield = 77, Warning = 78,
                         ImageFiles = 72, DriveFixed = 8, Application = 2, Settings = 106, DocNoAssoc = 0,
                         Key = 81, Lock = 47;

        public static Bitmap Get(int siid, int size)
        {
            var info = new SHSTOCKICONINFO { cbSize = Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            try
            {
                if (SHGetStockIconInfo(siid, SHGSI_ICON | SHGSI_SMALLICON, ref info) == 0 && info.hIcon != IntPtr.Zero)
                {
                    using (var icon = Icon.FromHandle(info.hIcon))
                    {
                        return new Bitmap(icon.ToBitmap(), size, size);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon);
            }
            return new Bitmap(SystemIcons.Application.ToBitmap(), size, size);
        }
    }

    /// <summary>檔案總管的「內容」對話框。</summary>
    public static class ShellProperties
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public int fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public int dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const int SEE_MASK_INVOKEIDLIST = 0xC;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        public static void Show(IntPtr owner, string path)
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = owner,
                lpVerb = "properties",
                lpFile = path,
                nShow = 1
            };
            ShellExecuteEx(ref info);
        }
    }
}
