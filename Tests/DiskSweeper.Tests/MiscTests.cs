using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace DiskCleaner.Tests
{
    public class LangTests
    {
        [Fact]
        public void English_TranslatesExactFormatAndColumnSpecs()
        {
            Lang.Init("en");
            try
            {
                Assert.Equal("Scan", Lang.T("掃瞄"));
                Assert.Equal("Name:220", Lang.T("名稱:220"));
                Assert.Equal("Size:100:r", Lang.T("大小:100:r"));
                Assert.Equal("Found 3 items. Click \"Clean\" to delete them (unchecked items are skipped)",
                    string.Format(Lang.T("找到 {0:N0} 個可清理的項目，點擊「清理」{1}（取消勾選的項目不會清）"), 3, "delete"));
                Assert.Equal("Cleaned today  ", Lang.T("今天清理  "));   // 行尾空白（\s）要保留
                Assert.Equal("沒有這句", Lang.T("沒有這句"));
            }
            finally
            {
                Lang.Init("zh");
            }
        }

        [Fact]
        public void Chinese_ReturnsInput()
        {
            Lang.Init("zh");
            Assert.Equal("掃瞄", Lang.T("掃瞄"));
        }
    }

    public class SplitCommandTests
    {
        [Theory]
        [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
        [InlineData("C:\\Program Files\\Foo Bar\\uninstall.exe --remove", "C:\\Program Files\\Foo Bar\\uninstall.exe", "--remove")]
        [InlineData("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}", "MsiExec.exe", "/I{12345678-1234-1234-1234-123456789012}")]
        [InlineData("RunDll32 C:\\x\\Ctor.dll,LaunchSetup \"C:\\y\\setup.exe\" -l0x404", "RunDll32", "C:\\x\\Ctor.dll,LaunchSetup \"C:\\y\\setup.exe\" -l0x404")]
        public void Splits(string command, string exe, string args)
        {
            var r = InstalledApps.SplitCommand(command);
            Assert.Equal(exe, r.Item1);
            Assert.Equal(args, r.Item2);
        }
    }

    public class FormatTests
    {
        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1536L, "1.5 KB")]
        [InlineData(5L * 1024 * 1024, "5 MB")]
        [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
        public void Size(long bytes, string text)
        {
            Assert.Equal(text, Format.Size(bytes));
        }
    }

    public class HealthTests
    {
        [Fact]
        public void Score_DeductsForWarningsAndProblems()
        {
            var items = new List<HealthItem>
            {
                new HealthItem { Level = HealthLevel.Ok },
                new HealthItem { Level = HealthLevel.Info },
                new HealthItem { Level = HealthLevel.Warn },
                new HealthItem { Level = HealthLevel.Bad }
            };
            Assert.Equal(100 - 6 - 15, HealthCheck.Score(items));
            Assert.Equal(0, HealthCheck.Score(Enumerable.Repeat(new HealthItem { Level = HealthLevel.Bad }, 10)));
        }
    }

    public class BackupTests
    {
        [Fact]
        public void BackupThenRestore_RoundTrips()
        {
            using (var t = new TempDir())
            {
                string a = t.File("a.txt", "hello");
                string b = t.File(@"sub\b.txt", "world");
                var item = Cleaner.Custom(new[] { new CustomFolder { Path = t.Path, Pattern = "*", Recursive = true } });
                Cleaner.Scan(item, new ScanOptions(), CancellationToken.None, delegate { });

                var backup = Backup.Create(new[] { item }, CancellationToken.None, delegate { });
                try
                {
                    Assert.Equal(2, backup.Files);
                    Cleaner.Clean(new List<CleanItem> { item }, false, CancellationToken.None, delegate { });
                    Assert.False(File.Exists(a));

                    var restored = Backup.Restore(backup.Archive);
                    Assert.Equal(2, restored.Item1);
                    Assert.Equal("hello", File.ReadAllText(a).Trim());
                    Assert.Equal("world", File.ReadAllText(b).Trim());
                }
                finally
                {
                    File.Delete(backup.Archive);
                }
            }
        }
    }
}
