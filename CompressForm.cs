using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 壓縮資料夾：用 Windows 內建的 compact.exe 做 NTFS 壓縮，檔案照常使用、只是佔的空間變小。
    /// LZX 壓縮率最高，適合很少改動的程式資料夾（例如遊戲、SDK）；常寫入的資料夾用標準壓縮。
    /// </summary>
    public class CompressForm : ToolWindow
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSizeW(string path, out uint high);

        private readonly TextBox _path = new TextBox { Width = 420 };
        private readonly ComboBox _algo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly Label _info = new Label { AutoSize = false, Height = 120, Dock = DockStyle.Top, Font = Theme.Title, Padding = new Padding(18, 18, 0, 0) };
        private readonly RoundButton _measure, _compress, _uncompress;
        private CancellationTokenSource _cts;

        public CompressForm() : base(Lang.T("壓縮資料夾"), Lang.T("用 NTFS 壓縮讓檔案佔的空間變小，檔案照常開啟使用"), "\uE7B8", new Size(860, 460))
        {
            _algo.Items.AddRange(new object[]
            {
                Lang.T("標準（NTFS，適合會修改的檔案）"),
                Lang.T("XPRESS 8K（快速，壓縮率中等）"),
                Lang.T("XPRESS 16K（較慢，壓縮率較高）"),
                Lang.T("LZX（最高壓縮率，適合很少改動的程式）")
            });
            _algo.SelectedIndex = 0;

            var browse = Light(Lang.T("瀏覽…"), 80);
            _measure = Light(Lang.T("計算大小"), 100);
            _compress = Primary(Lang.T("壓縮"), 100);
            _uncompress = Light(Lang.T("解除壓縮"), 100);

            var hint = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(18, 6, 18, 0), ForeColor = Theme.TextMuted,
                Text = Lang.T("說明：\r\n• 壓縮後讀取時 CPU 會多做一點解壓縮，現在的電腦幾乎感覺不出來。\r\n")
                     + Lang.T("• XPRESS／LZX 壓縮的檔案被修改時會自動變回未壓縮，所以只適合「裝好就不太動」的資料夾。\r\n")
                     + Lang.T("• Windows、Program Files 根目錄、使用者資料夾本身不能壓縮；正在執行的程式檔案會被略過。")
            };
            var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(2) };
            card.Controls.Add(hint);
            card.Controls.Add(_info);

            var second = Toolbar(new Label { Text = Lang.T("方式"), AutoSize = true }, _algo, _measure, _compress, _uncompress);
            Content.Controls.Add(card);
            Content.Controls.Add(second);
            Content.Controls.Add(Toolbar(new Label { Text = Lang.T("資料夾"), AutoSize = true }, _path, browse));

            browse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = _path.Text })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) { _path.Text = dlg.SelectedPath; Measure(); }
                }
            };
            _measure.Click += delegate { Measure(); };
            _compress.Click += delegate { Run(true); };
            _uncompress.Click += delegate { Run(false); };
            FormClosing += delegate { if (_cts != null) _cts.Cancel(); };
            _info.Text = Lang.T("選一個資料夾，按「計算大小」看目前佔多少空間。");
        }

        private bool Check()
        {
            string dir = _path.Text.Trim();
            if (!Directory.Exists(dir)) { Msg.Show(this, Lang.T("找不到這個資料夾。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (Cleaner.IsProtected(dir) || dir.StartsWith(win, StringComparison.OrdinalIgnoreCase))
            {
                Msg.Show(this, Lang.T("系統資料夾或使用者資料夾本身不能壓縮，請選它底下的子資料夾。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        /// <summary>(實際大小, 磁碟上佔用, 檔案數)</summary>
        private static Tuple<long, long, int> Sizes(string dir, CancellationToken token)
        {
            long size = 0, onDisk = 0;
            int files = 0;
            var pending = new Stack<string>();
            pending.Push(dir);
            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                string d = pending.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(d))
                    {
                        try
                        {
                            size += new FileInfo(f).Length;
                            uint high;
                            uint low = GetCompressedFileSizeW(f, out high);
                            if (low != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0) onDisk += ((long)high << 32) + low;
                            files++;
                        }
                        catch (Exception) { }
                    }
                    foreach (string sub in Directory.EnumerateDirectories(d))
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                    }
                }
                catch (Exception) { }
            }
            return Tuple.Create(size, onDisk, files);
        }

        private async void Measure()
        {
            if (!Directory.Exists(_path.Text.Trim())) return;
            string dir = _path.Text.Trim();
            _info.Text = Lang.T("計算中…");
            var s = await Task.Run(() => Sizes(dir, CancellationToken.None));
            ShowSizes(s, null);
        }

        private void ShowSizes(Tuple<long, long, int> s, Tuple<long, long, int> before)
        {
            double ratio = s.Item1 > 0 ? (double)s.Item2 / s.Item1 : 1;
            string text = string.Format(Lang.T("{0:N0} 個檔案　實際大小 {1}\r\n磁碟上佔用 {2}（{3:0}%）"), s.Item3, Format.Size(s.Item1), Format.Size(s.Item2), ratio * 100);
            if (before != null)
            {
                long saved = before.Item2 - s.Item2;
                text += string.Format("\r\n{0} {1}", saved >= 0 ? Lang.T("這次省下") : Lang.T("這次多用了"), Format.Size(Math.Abs(saved)));
            }
            _info.Text = text;
            _info.ForeColor = before != null && before.Item2 > s.Item2 ? Theme.AccentDark : Theme.Text;
        }

        private async void Run(bool compress)
        {
            if (_cts != null) { _cts.Cancel(); return; }
            if (!Check()) return;
            string dir = _path.Text.Trim();
            string algo = new[] { null, "xpress8k", "xpress16k", "lzx" }[_algo.SelectedIndex];

            if (compress && Msg.Show(this, string.Format(Lang.T("用「{0}」壓縮\r\n{1}\r\n底下的所有檔案？"), _algo.SelectedItem, dir), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var button = compress ? _compress : _uncompress;
            string label = button.Text;
            button.Text = Lang.T("停止");
            _info.Text = compress ? Lang.T("壓縮中…（大的資料夾要好幾分鐘）") : Lang.T("解除壓縮中…");

            var before = await Task.Run(() => Sizes(dir, token));
            int processed = 0;
            var progress = new Progress<int>(n => Status.Text = string.Format(Lang.T("已處理 {0:N0} 個檔案…"), n));

            await Task.Run(() =>
            {
                // 解除壓縮時兩種都跑一次：/exe 壓縮（XPRESS、LZX）要用 /exe 才解得開。
                var passes = new List<string>();
                if (compress) passes.Add(algo == null ? "/c" : "/c /exe:" + algo);
                else { passes.Add("/u /exe"); passes.Add("/u"); }

                foreach (string mode in passes)
                {
                    if (token.IsCancellationRequested) break;
                    var info = new ProcessStartInfo("compact.exe", string.Format("{0} /s:\"{1}\" /i /q *", mode, dir))
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using (var p = Process.Start(info))
                    {
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) ((IProgress<int>)progress).Report(Interlocked.Increment(ref processed)); };
                        p.ErrorDataReceived += delegate { };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        while (!p.WaitForExit(300))
                        {
                            if (token.IsCancellationRequested) { try { p.Kill(); } catch (Exception) { } break; }
                        }
                    }
                }
            });

            var after = await Task.Run(() => Sizes(dir, CancellationToken.None));
            ShowSizes(after, before);
            Status.Text = token.IsCancellationRequested ? Lang.T("已停止（已處理的檔案維持壓縮狀態）") : (compress ? Lang.T("壓縮完成") : Lang.T("解除壓縮完成"));
            button.Text = label;
            _cts = null;
        }
    }
}
