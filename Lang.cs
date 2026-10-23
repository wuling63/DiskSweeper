using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskCleaner
{
    /// <summary>
    /// 介面語言。原始碼裡的中文字串都包在 Lang.T("…") 裡；英文模式時查內嵌的 en.txt 換成英文，
    /// 中文模式直接回傳原字串（不查表，沒有成本）。格式字串（含 {0}）先翻再填值，所以動態訊息也是整句英文。
    /// 語言在程式啟動時決定，改了要重新啟動。
    /// </summary>
    public static class Lang
    {
        private static Dictionary<string, string> _map;

        public static bool English { get; private set; }

        public static void Init(string language)
        {
            English = language == "en";
            if (English) _map = Load();
        }

        public static string T(string text)
        {
            if (!English || string.IsNullOrEmpty(text) || _map == null) return text;

            string en;
            if (_map.TryGetValue(text, out en)) return en;

            // 欄位定義「名稱:寬度[:r]」只翻名稱那段。
            var m = ColumnSpec.Match(text);
            if (m.Success && _map.TryGetValue(m.Groups[1].Value, out en)) return en + m.Groups[2].Value;

            return text;
        }

        private static readonly Regex ColumnSpec = new Regex(@"^(.+?)(:\d+(?::r)?)$", RegexOptions.Compiled);

        /// <summary>en.txt：一行一筆「中文&lt;Tab&gt;English」，\r \n \t \\ 用跳脫寫法，行尾的空白寫成 \s（編輯器常會吃掉行尾空白）。</summary>
        private static Dictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (var stream = typeof(Lang).Assembly.GetManifestResourceStream("DiskCleaner.en.txt"))
                {
                    if (stream == null) return map;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            int tab = line.IndexOf('\t');
                            if (tab <= 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                            map[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
                        }
                    }
                }
            }
            catch (Exception) { }
            return map;
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'r' ? '\r' : c == 'n' ? '\n' : c == 't' ? '\t' : c == 's' ? ' ' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
