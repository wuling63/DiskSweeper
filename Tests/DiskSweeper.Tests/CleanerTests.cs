using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace DiskCleaner.Tests
{
    public class CleanerTests
    {
        private static CleanItem CustomItem(string folder, string pattern = "*")
        {
            return Cleaner.Custom(new[] { new CustomFolder { Path = folder, Pattern = pattern, Recursive = true } });
        }

        private static void Scan(CleanItem item)
        {
            Cleaner.Scan(item, new ScanOptions(), CancellationToken.None, delegate { });
        }

        [Fact]
        public void ExpandRoot_ExpandsWildcardSegments()
        {
            using (var t = new TempDir())
            {
                t.Dir(@"Profiles\Default\Cache");
                t.Dir(@"Profiles\Profile 1\Cache");
                t.Dir(@"Profiles\System\Other");
                t.Dir(@"App123\Data");
                t.Dir(@"App456\Data");
                t.Dir(@"Zzz\Data");

                var caches = Cleaner.ExpandRoot(Path.Combine(t.Path, @"Profiles\*\Cache")).ToList();
                Assert.Equal(2, caches.Count);

                var apps = Cleaner.ExpandRoot(Path.Combine(t.Path, @"App*\Data")).ToList();
                Assert.Equal(2, apps.Count);

                var each = Cleaner.ExpandRoot(Path.Combine(t.Path, @"Profiles\*")).ToList();
                Assert.Equal(3, each.Count);

                Assert.Empty(Cleaner.ExpandRoot(Path.Combine(t.Path, @"Missing\*\Cache")));
            }
        }

        [Fact]
        public void Scan_ExtensionPatternIsStrict()
        {
            using (var t = new TempDir())
            {
                t.File("a.log");
                t.File("b.log1");          // Windows 的 *.log 會比對到，我們不要
                t.File(@"sub\c.log");
                var item = CustomItem(t.Path, "*.log");
                Scan(item);
                var names = item.Found.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToList();
                Assert.Equal(new[] { "a.log", "c.log" }, names);
            }
        }

        [Fact]
        public void Clean_DeletesFilesButNeverFollowsJunctions()
        {
            using (var t = new TempDir())
            {
                string target = t.Dir("target");
                t.File(@"target\a.tmp");
                string ro = t.File(@"target\readonly.txt");
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                t.File(@"target\sub\deeper\c.dat");
                string precious = t.File(@"outside\precious.txt", "keep");
                t.Junction(Path.Combine(target, "link"), Path.Combine(t.Path, "outside"));

                var item = CustomItem(target);
                Scan(item);
                Assert.DoesNotContain(item.Found, f => f.Path.EndsWith("precious.txt"));

                var result = Cleaner.Clean(new List<CleanItem> { item }, false, CancellationToken.None, delegate { });
                Assert.Equal(3, result.Deleted);
                Assert.True(File.Exists(precious), "junction 指到的外部檔案不能被刪");
                Assert.True(Directory.Exists(target), "根資料夾要留著");
                Assert.False(Directory.Exists(Path.Combine(target, "sub")), "空的子資料夾要清掉");
            }
        }

        [Fact]
        public void Scan_RespectsIgnoreList()
        {
            using (var t = new TempDir())
            {
                t.File("keep\\a.txt");
                t.File("drop\\b.txt");
                var item = CustomItem(t.Path);
                Cleaner.Scan(item, new ScanOptions { Ignore = new List<string> { Path.Combine(t.Path, "keep") } }, CancellationToken.None, delegate { });
                Assert.Single(item.Found);
                Assert.EndsWith("b.txt", item.Found[0].Path);
            }
        }

        [Fact]
        public void IsProtected_BlocksImportantFolders()
        {
            Assert.True(Cleaner.IsProtected(@"C:\"));
            Assert.True(Cleaner.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            Assert.True(Cleaner.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            Assert.True(Cleaner.IsProtected(""));
            Assert.False(Cleaner.IsProtected(Path.Combine(Path.GetTempPath(), "something")));
        }

        [Fact]
        public void Custom_RefusesProtectedFolders()
        {
            var item = Cleaner.Custom(new[] { new CustomFolder { Path = @"C:\", Pattern = "*", Recursive = true } });
            Assert.Empty(item.Rules);
        }

        [Fact]
        public void Custom_SplitsMultiplePatterns()
        {
            var item = Cleaner.Custom(new[] { new CustomFolder { Path = @"D:\X", Pattern = "*.log; *.bak", Recursive = false } });
            Assert.Equal(new[] { "*.log", "*.bak" }, item.Rules.Select(r => r.Pattern).ToArray());
        }

        [Fact]
        public void SafeForAuto_ExcludesPrivacyAndCommunity()
        {
            Assert.True(new CleanItem().SafeForAuto);
            Assert.False(new CleanItem { Privacy = true }.SafeForAuto);
            Assert.False(new CleanItem { Community = true }.SafeForAuto);
            Assert.False(new CleanItem { Kind = ItemKind.RecycleBin }.SafeForAuto);
            Assert.False(new CleanItem { Kind = ItemKind.Command }.SafeForAuto);
        }
    }
}
