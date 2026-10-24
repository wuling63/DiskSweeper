using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DiskCleaner.Tests
{
    public class Winapp2Tests
    {
        [Fact]
        public void Parse_KeepsOnlyCacheLikeDetectedEntries()
        {
            using (var t = new TempDir())
            {
                t.File(@"Foo\Cache\a.bin");
                string ini = string.Join("\n",
                    "; comment",
                    "[Foo Cache *]",
                    "DetectFile=" + t.Path + @"\Foo",
                    "FileKey1=" + t.Path + @"\Foo\Cache|*|RECURSE",
                    "[Foo Cookies *]",                                   // 名稱是 Cookie 類：不收
                    "DetectFile=" + t.Path + @"\Foo",
                    "FileKey1=" + t.Path + @"\Foo|Cookies",
                    "[Bar Cache *]",                                     // 沒安裝：不收
                    "DetectFile=" + t.Path + @"\Bar",
                    "FileKey1=" + t.Path + @"\Bar\Cache|*",
                    "[Foo Logs Registry Only *]",                        // 只有 RegKey：不收
                    "DetectFile=" + t.Path + @"\Foo",
                    "RegKey1=HKCU\\Software\\Foo\\Logs");

                var r = Winapp2.Parse(ini);
                Assert.Equal(4, r.Total);
                Assert.Single(r.Items);
                var item = r.Items[0];
                Assert.Equal("Foo Cache", item.Name);
                Assert.True(item.Community);
                Assert.False(item.DefaultChecked);
                Assert.True(item.Rules[0].Recursive);
                Assert.Equal(1, r.Unsafe);
                Assert.Equal(1, r.NotInstalled);
            }
        }

        [Fact]
        public void Parse_BlocksEventLogsAndInstallerCache()
        {
            string ini = string.Join("\n",
                "[Windows Logs Test *]",
                @"FileKey1=%WinDir%\System32\winevt\logs|*",
                @"FileKey2=%WinDir%\Installer|*.msi",
                @"FileKey3=%WinDir%\Installer|*.tmp",
                @"FileKey4=%WinDir%\Temp|*.log");

            var r = Winapp2.Parse(ini);
            Assert.Single(r.Items);
            var patterns = r.Items[0].Rules.Select(x => Path.GetFileName(x.Root.TrimEnd('\\')) + "|" + x.Pattern).ToList();
            Assert.DoesNotContain(patterns, p => p.StartsWith("logs|"));
            Assert.DoesNotContain("Installer|*.msi", patterns);
            Assert.Contains("Installer|*.tmp", patterns);
            Assert.Contains("Temp|*.log", patterns);
        }

        [Fact]
        public void Parse_RefusesProtectedRoots()
        {
            string ini = string.Join("\n", "[Everything Cache *]", "FileKey1=%UserProfile%|*.tmp|RECURSE");
            Assert.Empty(Winapp2.Parse(ini).Items);
        }

        [Fact]
        public void ExpandVariables_ProgramFilesMeansBoth()
        {
            var paths = Winapp2.ExpandVariables(@"%ProgramFiles%\Foo").ToList();
            Assert.Contains(paths, p => p.EndsWith(@"Program Files\Foo", StringComparison.OrdinalIgnoreCase));
            if (Environment.Is64BitOperatingSystem) Assert.Contains(paths, p => p.EndsWith(@"Program Files (x86)\Foo", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ExpandVariables_UnknownVariableIsSkipped()
        {
            Assert.Empty(Winapp2.ExpandVariables(@"%NoSuchVariable%\Foo"));
        }

        [Fact]
        public void WildcardRegex_MatchesLikeWindows()
        {
            var r = Winapp2.WildcardRegex("*.log;data?.bin");
            Assert.Matches(r, "a.log");
            Assert.Matches(r, "data1.bin");
            Assert.DoesNotMatch(r, "a.log1");
        }
    }
}
