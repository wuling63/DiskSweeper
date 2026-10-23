using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace DiskCleaner
{
    /// <summary>資料夾大小樹的一個節點。</summary>
    public class DirNode
    {
        public string Name;
        public string Path;
        public long Size;          // 含所有子孫
        public long OwnFiles;      // 直接放在這一層的檔案大小
        public int FileCount;      // 含所有子孫
        public DirNode Parent;
        public List<DirNode> Children = new List<DirNode>();
    }

    public class BigFile
    {
        public string Path;
        public long Size;
        public DateTime Modified;
    }

    /// <summary>大檔案分析、重複檔案、空資料夾的搜尋邏輯（不含畫面）。</summary>
    public static class Analysis
    {
        public const int KeepBigFiles = 500;

        // ───────────────────────────── 資料夾大小 ─────────────────────────────

        /// <summary>算出整棵資料夾大小樹，順便留下最大的 KeepBigFiles 個檔案。不跟進 junction。</summary>
        public static DirNode BuildTree(string root, List<BigFile> bigFiles, CancellationToken token, Action<string> progress)
        {
            var node = new DirNode { Name = root, Path = root };
            var big = new List<BigFile>();
            Walk(new DirectoryInfo(root), node, big, token, progress);

            bigFiles.Clear();
            bigFiles.AddRange(big.OrderByDescending(f => f.Size).Take(KeepBigFiles));
            return node;
        }

        private static void Walk(DirectoryInfo dir, DirNode node, List<BigFile> big, CancellationToken token, Action<string> progress)
        {
            if (token.IsCancellationRequested) return;
            progress(dir.FullName);

            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    long size;
                    try { size = f.Length; } catch (Exception) { continue; }
                    node.OwnFiles += size;
                    node.FileCount++;
                    big.Add(new BigFile { Path = f.FullName, Size = size, Modified = f.LastWriteTime });

                    // 不要整份留著：太多時排序後只留前面那些。
                    if (big.Count > KeepBigFiles * 4)
                    {
                        var keep = big.OrderByDescending(b => b.Size).Take(KeepBigFiles).ToList();
                        big.Clear();
                        big.AddRange(keep);
                    }
                }

                foreach (var sub in dir.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var child = new DirNode { Name = sub.Name, Path = sub.FullName, Parent = node };
                    Walk(sub, child, big, token, progress);
                    node.Children.Add(child);
                    node.Size += child.Size;
                    node.FileCount += child.FileCount;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            node.Size += node.OwnFiles;
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }

        // ───────────────────────────── 重複檔案 ─────────────────────────────

        /// <summary>
        /// 先依大小分組，同大小的再比前 64 KB 的雜湊，還一樣才算整個檔案的雜湊。
        /// 大部分檔案在第一、二步就被排除，不用整個讀完。回傳內容完全相同的檔案群組（大的在前）。
        /// </summary>
        public static List<List<BigFile>> FindDuplicates(string root, long minSize, CancellationToken token, Action<string> progress)
        {
            var bySize = new Dictionary<long, List<BigFile>>();
            foreach (var f in EnumerateFiles(root, token, progress))
            {
                if (f.Size < minSize) continue;
                List<BigFile> list;
                if (!bySize.TryGetValue(f.Size, out list)) bySize[f.Size] = list = new List<BigFile>();
                list.Add(f);
            }

            var groups = new List<List<BigFile>>();
            foreach (var sameSize in bySize.Values.Where(l => l.Count > 1))
            {
                foreach (var sameHead in GroupBy(sameSize, f => Hash(f.Path, 64 * 1024), token, progress))
                {
                    // 檔案小於 64 KB 時，前段雜湊就是整個檔案的雜湊。
                    if (sameHead[0].Size <= 64 * 1024) { groups.Add(sameHead); continue; }
                    groups.AddRange(GroupBy(sameHead, f => Hash(f.Path, long.MaxValue), token, progress));
                }
                if (token.IsCancellationRequested) break;
            }

            return groups.OrderByDescending(g => g[0].Size * (g.Count - 1)).ToList();
        }

        private static List<List<BigFile>> GroupBy(List<BigFile> files, Func<BigFile, string> key, CancellationToken token, Action<string> progress)
        {
            var map = new Dictionary<string, List<BigFile>>();
            foreach (var f in files)
            {
                if (token.IsCancellationRequested) break;
                progress(Lang.T("比對 ") + f.Path);
                string k = key(f);
                if (k == null) continue;   // 讀不到（使用中）就不算
                List<BigFile> list;
                if (!map.TryGetValue(k, out list)) map[k] = list = new List<BigFile>();
                list.Add(f);
            }
            return map.Values.Where(l => l.Count > 1).ToList();
        }

        private static string Hash(string path, long maxBytes)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                {
                    if (maxBytes >= stream.Length) return Convert.ToBase64String(md5.ComputeHash(stream));

                    var buffer = new byte[maxBytes];
                    int read = 0, n;
                    while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                    return Convert.ToBase64String(md5.ComputeHash(buffer, 0, read));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IEnumerable<BigFile> EnumerateFiles(string root, CancellationToken token, Action<string> progress)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) yield break;
                var dir = pending.Pop();
                progress(dir.FullName);

                FileInfo[] files = null;
                DirectoryInfo[] subs = null;
                try
                {
                    files = dir.GetFiles();
                    subs = dir.GetDirectories();
                }
                catch (Exception) { }

                if (files != null)
                {
                    foreach (var f in files)
                    {
                        BigFile item = null;
                        try { item = new BigFile { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime }; }
                        catch (Exception) { }
                        if (item != null) yield return item;
                    }
                }
                if (subs == null) continue;
                foreach (var sub in subs)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                }
            }
        }

        // ───────────────────────────── 空資料夾 ─────────────────────────────

        /// <summary>
        /// 找「底下完全沒有檔案」的資料夾。整棵都空的只回報最上層那個（刪它就連子資料夾一起刪了），
        /// 搜尋的根本身不算；系統與使用者的重要資料夾不列。
        /// </summary>
        public static List<string> FindEmptyDirs(string root, CancellationToken token, Action<string> progress)
        {
            var result = new List<string>();
            try
            {
                foreach (var sub in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (IsEmpty(sub, result, token, progress) && !Cleaner.IsProtected(sub.FullName)) result.Add(sub.FullName);
                }
            }
            catch (Exception) { }
            return result;
        }

        /// <summary>整棵都沒有檔案回 true；不是的話，把底下整棵空的子資料夾加進 result。</summary>
        private static bool IsEmpty(DirectoryInfo dir, List<string> result, CancellationToken token, Action<string> progress)
        {
            if (token.IsCancellationRequested) return false;
            progress(dir.FullName);

            try
            {
                if (dir.EnumerateFiles().Any()) { CollectEmptyChildren(dir, result, token, progress); return false; }

                var emptyChildren = new List<string>();
                bool allEmpty = true;
                foreach (var sub in dir.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { allEmpty = false; continue; }
                    if (IsEmpty(sub, result, token, progress)) emptyChildren.Add(sub.FullName);
                    else allEmpty = false;
                }

                if (allEmpty) return true;
                result.AddRange(emptyChildren.Where(p => !Cleaner.IsProtected(p)));
                return false;
            }
            catch (Exception)
            {
                return false;   // 沒權限讀的當成不空，不去動它
            }
        }

        private static void CollectEmptyChildren(DirectoryInfo dir, List<string> result, CancellationToken token, Action<string> progress)
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (IsEmpty(sub, result, token, progress) && !Cleaner.IsProtected(sub.FullName)) result.Add(sub.FullName);
            }
        }
    }
}
