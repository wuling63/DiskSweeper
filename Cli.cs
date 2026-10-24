using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiskCleaner
{
    /// <summary>
    /// 命令列模式（給腳本、批次檔用）：
    ///   DiskSweeper.exe /list                         列出所有項目代號
    ///   DiskSweeper.exe /scan [/items:a,b]            掃描並列出可清理的大小
    ///   DiskSweeper.exe /clean [/items:a,b] /yes      清理（沒有 /yes 只預演，不會刪任何東西）
    ///   其他選項：/recycle 送進資源回收筒、/backup 先備份成 zip、/out:檔案 把結果另存成文字檔
    /// 沒指定 /items 時用「排程自動清理」的範圍（勾選的檔案類項目）。
    /// 程式是視窗程式，輸出接到呼叫它的主控台；在 cmd 裡請用 start /wait，PowerShell 請接 | Out-Host。
    /// </summary>
    public static class Cli
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        public static bool IsCliArgument(string arg)
        {
            var a = (arg ?? string.Empty).ToLowerInvariant();
            return a == "/list" || a == "/scan" || a == "/clean" || a == "/help" || a == "/?" || a == "-h" || a == "--help";
        }

        public static int Run(string[] args)
        {
            AttachConsole(-1);   // 接到呼叫者的主控台
            // UTF-8 時不要寫 BOM，否則第一行開頭會多一個看不見的字元，腳本解析會出錯。
            var encoding = Console.OutputEncoding.CodePage == 65001 ? new UTF8Encoding(false) : Console.OutputEncoding;
            var console = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
            var report = new StringBuilder();
            Action<string> write = line =>
            {
                console.WriteLine(line);
                report.AppendLine(line);
            };

            string command = args[0].ToLowerInvariant();
            string itemsArg = Value(args, "/items:");
            string outFile = Value(args, "/out:");
            bool yes = Has(args, "/yes"), recycle = Has(args, "/recycle"), backup = Has(args, "/backup");
            int code = 0;

            try
            {
                var settings = Settings.Load();
                var all = Cleaner.BuiltIn(settings.DevRoots);
                if (settings.CustomFolders.Count > 0) all.Add(Cleaner.Custom(settings.CustomFolders));
                all.AddRange(Winapp2.LoadInstalled().Items);

                if (command == "/list")
                {
                    write(Lang.T("代號\t預設\t名稱（分類）"));
                    foreach (var i in all) write(string.Format("{0}\t{1}\t{2}（{3}）", i.Id, i.DefaultChecked ? "✓" : "", i.Name, Lang.T(i.Category)));
                }
                else if (command == "/scan" || command == "/clean")
                {
                    var picked = Pick(all, itemsArg, settings, write);
                    if (picked == null) return 2;

                    var options = new ScanOptions { MinAgeHours = settings.MinAgeHours, Ignore = settings.Ignore };
                    long total = 0;
                    foreach (var item in picked)
                    {
                        Cleaner.Scan(item, options, CancellationToken.None, delegate { });
                        total += item.FoundBytes;
                        write(string.Format("{0,-28} {1,8:N0} {2,12}", item.Id, item.Count, Format.Size(item.FoundBytes)));
                    }
                    write(string.Format(Lang.T("合計 {0:N0} 個項目，{1}"), picked.Sum(i => i.Count), Format.Size(total)));

                    if (command == "/clean")
                    {
                        if (!yes)
                        {
                            write(Lang.T("預演模式：沒有刪除任何東西。確定要清理請加 /yes。"));
                        }
                        else
                        {
                            var found = picked.Where(i => i.Found.Count > 0).ToList();
                            if (backup && !recycle)
                            {
                                Backup.Purge(settings.BackupDays);
                                var b = Backup.Create(found, CancellationToken.None, delegate { });
                                if (b.Archive != null) write(string.Format(Lang.T("已備份 {0:N0} 個檔案：{1}"), b.Files, b.Archive));
                            }
                            var r = Cleaner.Clean(found, recycle, CancellationToken.None, delegate { });
                            write(string.Format(Lang.T("已清理 {0:N0} 個項目，釋放 {1}；{2:N0} 個使用中或沒有權限，已略過"), r.Deleted, Format.Size(r.Bytes), r.Failed));
                            if (r.Deleted > 0)
                            {
                                settings.History.Add(new HistoryEntry { Time = DateTime.Now, Files = r.Deleted, Bytes = r.Bytes, Failed = r.Failed });
                                settings.Save();
                            }
                        }
                    }
                }
                else
                {
                    Help(write);
                }
            }
            catch (Exception ex)
            {
                write(Lang.T("錯誤：") + ex.Message);
                code = 1;
            }

            if (!string.IsNullOrEmpty(outFile))
            {
                try { File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(true)); }
                catch (Exception ex) { console.WriteLine(Lang.T("無法寫入報告檔：") + ex.Message); code = 1; }
            }
            return code;
        }

        /// <summary>/items:a,b 指定的項目；沒指定就用排程清理的範圍。不認得的代號直接報錯，不猜。</summary>
        private static List<CleanItem> Pick(List<CleanItem> all, string itemsArg, Settings settings, Action<string> write)
        {
            if (string.IsNullOrEmpty(itemsArg))
            {
                return all.Where(i =>
                {
                    bool on;
                    if (!settings.Checked.TryGetValue(i.Id, out on)) on = i.DefaultChecked;
                    return on && i.SafeForAuto;
                }).ToList();
            }

            var ids = itemsArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            var unknown = ids.Where(id => !all.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                write(Lang.T("不認得的項目代號：") + string.Join(", ", unknown) + Lang.T("（用 /list 看所有代號）"));
                return null;
            }
            return all.Where(i => ids.Any(id => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static string Value(string[] args, string prefix)
        {
            var a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return a == null ? null : a.Substring(prefix.Length).Trim('"');
        }

        private static bool Has(string[] args, string flag)
        {
            return args.Any(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));
        }

        private static void Help(Action<string> write)
        {
            write("DiskSweeper " + Lang.T("命令列模式"));
            write("  /list                        " + Lang.T("列出所有項目代號"));
            write("  /scan  [/items:a,b]          " + Lang.T("掃描並列出可清理的大小（不刪除）"));
            write("  /clean [/items:a,b] /yes     " + Lang.T("清理；沒有 /yes 只預演"));
            write("  /recycle                     " + Lang.T("送進資源回收筒而不是直接刪除"));
            write("  /backup                      " + Lang.T("清理前先備份成 zip"));
            write("  /out:report.txt              " + Lang.T("把結果另存成文字檔"));
            write(Lang.T("沒有 /items 時用「排程自動清理」的範圍。在 cmd 請用 start /wait DiskSweeper.exe …，PowerShell 請接 | Out-Host。"));
        }
    }
}
