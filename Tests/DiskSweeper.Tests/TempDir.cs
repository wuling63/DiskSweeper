using System;
using System.Diagnostics;
using System.IO;

namespace DiskCleaner.Tests
{
    /// <summary>每個測試一個獨立的暫存資料夾，結束時整個刪掉（含 junction，不跟進）。</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; private set; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskSweeperTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string relative, string content = "x")
        {
            string full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            System.IO.File.WriteAllText(full, content);
            return full;
        }

        public string Dir(string relative)
        {
            string full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>建立 junction（不需要系統管理員）。</summary>
        public void Junction(string link, string target)
        {
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", string.Format("/c mklink /J \"{0}\" \"{1}\"", link, target))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            }))
            {
                p.WaitForExit();
            }
        }

        public void Dispose()
        {
            // rmdir /s 對 junction 只刪連結本身，不會刪到目標。
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c rmdir /s /q \"" + Path + "\"") { UseShellExecute = false, CreateNoWindow = true }))
            {
                p.WaitForExit();
            }
        }
    }
}
