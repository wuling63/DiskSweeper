using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskCleaner
{
    /// <summary>
    /// 介面語言。原始碼裡的中文（繁體）字串都包在 Lang.T("…") 裡；其他語言查內嵌的字典檔（en.txt、zh-CN.txt、ja.txt、ko.txt）換掉，
    /// 繁體中文直接回傳原字串（不查表，沒有成本）。格式字串（含 {0}）先翻再填值，所以動態訊息也是整句翻好的。
    /// 日文、韓文字典缺的句子先退回英文，再退回原字串。語言在程式啟動時決定，改了要重新啟動。
    /// </summary>
    public static class Lang
    {
        /// <summary>支援的語言代號，順序就是選項裡下拉選單的順序；名稱用各語言自己的寫法，不翻譯。</summary>
        public static readonly string[] Codes = { "zh", "zh-CN", "ja", "ko", "en" };
        public static readonly string[] NativeNames = { "中文（繁體）", "中文（简体）", "日本語", "한국어", "English" };

        private static Dictionary<string, string> _map;

        public static string Code { get; private set; } = "zh";

        /// <summary>介面字型：各語言用 Windows 內建、字最齊的 UI 字型（微軟正黑體沒有韓文字）。</summary>
        public static string FontName
        {
            get
            {
                switch (Code)
                {
                    case "zh-CN": return "Microsoft YaHei UI";
                    case "ja": return "Yu Gothic UI";
                    case "ko": return "Malgun Gothic";
                    default: return "Microsoft JhengHei UI";
                }
            }
        }

        public static void Init(string language)
        {
            Code = Array.IndexOf(Codes, language) >= 0 ? language : "zh";
            _map = null;
            if (Code == "zh") return;

            _map = Load(Code);
            if (Code == "ja" || Code == "ko")
            {
                foreach (var pair in Load("en"))
                {
                    if (!_map.ContainsKey(pair.Key)) _map[pair.Key] = pair.Value;
                }
            }
        }

        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text) || _map == null) return text;

            string en;
            if (_map.TryGetValue(text, out en)) return en;

            // 欄位定義「名稱:寬度[:r]」只翻名稱那段。
            var m = ColumnSpec.Match(text);
            if (m.Success && _map.TryGetValue(m.Groups[1].Value, out en)) return en + m.Groups[2].Value;

            return text;
        }

        private static readonly Regex ColumnSpec = new Regex(@"^(.+?)(:\d+(?::r)?)$", RegexOptions.Compiled);

        /// <summary>字典檔（en.txt 等）：一行一筆「中文&lt;Tab&gt;譯文」，\r \n \t \\ 用跳脫寫法，行尾的空白寫成 \s（編輯器常會吃掉行尾空白）。</summary>
        private static Dictionary<string, string> Load(string code)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (var stream = typeof(Lang).Assembly.GetManifestResourceStream("DiskCleaner." + code + ".txt"))
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
