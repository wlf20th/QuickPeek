using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QuickPeek;

/// <summary>
/// 极简 Markdown → RTF 渲染器。
///
/// 为什么不是逐段调 RichTextBox 的 SelectionFont：那样每写一小段都要 Select + 设置属性，
/// 20KB 的回答要花 10 秒。改成一次性生成 RTF 再赋值给控件，同样内容只要几十毫秒。
///
/// 覆盖范围：标题、粗体/斜体/删除线、行内代码、代码块、列表、引用、分隔线、表格。
/// 不做完整规范（不处理嵌套、图片、HTML），目标是让回答有层次、能扫读。
/// </summary>
internal static class MarkdownRenderer
{
    // RTF 颜色表索引（从 1 开始，0 表示自动色）
    private const int CText = 1;
    private const int CHead = 2;
    private const int CCodeFg = 3;
    private const int CCodeBg = 4;
    private const int CBlockBg = 5;
    private const int CQuote = 6;
    private const int CRule = 7;
    private const int CLink = 8;
    private const int CAccent = 9;
    private const int CBlockFg = 10;

    private const int BodyFs = 21;      // 半磅：10.5pt
    private const int CodeFs = 20;      // 10pt

    // 段落间距单位 twips（1/1440 英寸），会随 DPI 自动缩放
    private const int SaNormal = 150;

    public static void Render(RichTextBox rtb, string md)
    {
        var rtf = BuildRtf(md, rtb.DeviceDpi / 96f);
        if (Environment.GetEnvironmentVariable("QP_DUMP_RTF") == "1")
            Log.Write("RTF 片段: " + rtf.Substring(0, Math.Min(1800, rtf.Length)));
        rtb.Rtf = rtf;
        rtb.Select(0, 0);
    }

    /// <summary>纯文本展示（关掉 Markdown 渲染时用）。</summary>
    public static void RenderPlain(RichTextBox rtb, string text)
    {
        rtb.Text = text;
        rtb.Select(0, 0);
    }

    private static string BuildRtf(string md, float scale)
    {
        var sb = new StringBuilder(md.Length * 3 + 1024);

        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\nouicompat\uc1");
        sb.Append(@"{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei UI;}{\f1\fmodern\fcharset0 Consolas;}}");
        sb.Append(@"{\colortbl ;" +
                  @"\red232\green232\blue232;" +   // 1 正文
                  @"\red255\green255\blue255;" +   // 2 标题
                  @"\red240\green194\blue138;" +   // 3 行内代码前景
                  @"\red54\green54\blue64;" +      // 4 行内代码背景
                  @"\red50\green50\blue60;" +      // 5 代码块背景
                  @"\red166\green174\blue186;" +   // 6 引用
                  @"\red85\green85\blue88;" +      // 7 分隔线
                  @"\red108\green168\blue255;" +   // 8 链接
                  @"\red91\green155\blue255;" +    // 9 强调
                  @"\red220\green220\blue228;" +   // 10 代码块前景
                  "}");
        sb.Append(@"\viewkind4\pard\plain\f0\fs21\cf1 ");

        var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            string trimmed = lines[i].TrimEnd().TrimStart();

            // 空行不再占一整行：段间距交给 \sa 处理
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // ---- 代码块（``` 或 ~~~ 都可以）----
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                string fence = trimmed[..3];
                i++;
                var block = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence))
                {
                    block.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                WriteCodeBlock(sb, block);
                continue;
            }

            // ---- 分隔线 ----
            if (IsRule(trimmed))
            {
                var rule = new StringBuilder();
                rule.Append(@"{\cf").Append(CRule).Append(' ');
                AppendEscaped(rule, new string('─', 48));
                rule.Append('}');
                Para(sb, 0, 0, SaNormal, 0, 0, CText, rule.ToString());
                i++;
                continue;
            }

            // ---- 标题 ----
            var hm = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
            if (hm.Success)
            {
                WriteHeading(sb, hm.Groups[1].Value.Length, hm.Groups[2].Value);
                i++;
                continue;
            }

            // ---- 引用 ----
            if (trimmed.StartsWith(">"))
            {
                string body = trimmed[1..].Trim();
                var quote = new StringBuilder();
                quote.Append(@"{\cf").Append(CAccent).Append(@" ▎ }{\cf").Append(CQuote).Append(' ');
                EmitTokens(quote, ParseInline(body));
                quote.Append('}');
                Para(sb, 240, 0, SaNormal, 0, 0, CQuote, quote.ToString());
                i++;
                continue;
            }

            // ---- 表格 ----
            if (trimmed.Contains('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                var header = SplitRow(trimmed);
                var rows = new List<string[]>();
                int j = i + 2;
                while (j < lines.Length)
                {
                    string t = lines[j].Trim();
                    if (t.Length == 0 || !t.Contains('|')) break;
                    rows.Add(SplitRow(t));
                    j++;
                }
                WriteTable(sb, header, rows, scale);
                i = j;
                continue;
            }

            // ---- 列表 ----
            var lm = Regex.Match(trimmed, @"^([-*+]|\d+[.)])\s+(.*)$");
            if (lm.Success)
            {
                WriteListItem(sb, lm.Groups[1].Value, lm.Groups[2].Value);
                i++;
                continue;
            }

            // ---- 普通段落 ----
            var lineSb = new StringBuilder();
            EmitTokens(lineSb, ParseInline(trimmed));
            Para(sb, 0, 0, SaNormal, 0, 0, CText, lineSb.ToString());
            i++;
        }

        sb.Append('}');
        return sb.ToString();
    }

    // ---------------- 块级 ----------------

    private static void WriteHeading(StringBuilder sb, int level, string text)
    {
        int[] fs = { 33, 29, 26, 24, 23, 22 };       // 16.5 / 14.5 / 13 / 12 / 11.5 / 11 pt
        int f = fs[Math.Clamp(level, 1, 6) - 1];

        var inner = new StringBuilder();
        EmitTokens(inner, ParseInline(text));

        sb.Append(@"\pard\plain\f0\fs").Append(f).Append(@"\cf").Append(CHead)
          .Append(@"\b\li0\fi0\ri0\sb").Append(level <= 2 ? 220 : 160)
          .Append(@"\sa").Append(level <= 2 ? 140 : 100)
          .Append(' ').Append(inner).Append(@"\b0\par").Append("\r\n");
    }

    private static void WriteListItem(StringBuilder sb, string marker, string text)
    {
        bool ordered = char.IsDigit(marker[0]);
        var inner = new StringBuilder();

        inner.Append(@"{\cf").Append(ordered ? CQuote : CAccent).Append(' ');
        AppendEscaped(inner, ordered ? marker : "•");
        inner.Append(@"\tab}");
        EmitTokens(inner, ParseInline(text));

        // 悬挂缩进：\li 左缩进 + \fi 负首行缩进，\tx 让正文与符号对齐
        sb.Append(@"\pard\plain\f0\fs").Append(BodyFs).Append(@"\cf").Append(CText)
          .Append(@"\li330\fi-330\tx330\ri0\sb0\sa90 ")
          .Append(inner).Append(@"\par").Append("\r\n");
    }

    private static void WriteCodeBlock(StringBuilder sb, List<string> lines)
    {
        if (lines.Count == 0) lines.Add("");

        foreach (var line in lines)
        {
            sb.Append(@"\pard\plain\f1\fs").Append(CodeFs).Append(@"\cf").Append(CBlockFg)
              .Append(@"\highlight").Append(CBlockBg)
              .Append(@"\li210\fi0\ri120\sb0\sa0 ");
            AppendEscaped(sb, line);
            sb.Append(@"\highlight0\par").Append("\r\n");
        }

    }

    private static void WriteTable(StringBuilder sb, string[] header, List<string[]> rows, float scale)
    {
        int cols = header.Length;
        foreach (var r in rows) cols = Math.Max(cols, r.Length);
        if (cols == 0) return;

        // 列宽按字体实测像素算（Consolas 下中文走回退字体，按字数估算会错位），
        // 再换算成 twips 制表位
        var colW = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int w = Measure(c < header.Length ? header[c] : "");
            foreach (var r in rows)
                if (c < r.Length) w = Math.Max(w, Measure(r[c]));
            colW[c] = w + 20;
        }

        var tabs = new StringBuilder();
        int acc = 0;
        for (int c = 0; c < cols - 1; c++)
        {
            acc += colW[c];
            tabs.Append(@"\tx").Append((int)Math.Round(acc * 15 / Math.Max(1f, scale)));
        }

        WriteRow(header, CHead, bold: true, sa: 0, sbTw: 120);

        int totalW = colW.Sum();                                  // 所有列，之前只累加了第一列
        int dashCount = Math.Clamp(totalW / 9, 6, 200);            // 全角 ─ 在 7pt 下约 9px 宽
        sb.Append(@"\pard\plain\f0\fs10\cf").Append(CRule)
          .Append(@"\li90\fi0\ri0\sb30\sa50 ")
          .Append(new string('─', dashCount))
          .Append(@"\par").Append("\r\n");

        for (int k = 0; k < rows.Count; k++)
            WriteRow(rows[k], CText, bold: false, sa: k == rows.Count - 1 ? 150 : 0);


        void WriteRow(string[] cells, int color, bool bold, int sa = 0, int sbTw = 0)
        {
            sb.Append(@"\pard\plain\f1\fs").Append(CodeFs).Append(@"\cf").Append(color)
              .Append(@"\li90\fi0\ri0\sb").Append(sbTw).Append(@"\sa").Append(sa).Append(tabs);
            if (bold) sb.Append(@"\b");
            sb.Append(' ');

            for (int c = 0; c < cells.Length; c++)
            {
                if (c > 0) sb.Append(@"\tab ");
                EmitTokens(sb, ParseInline(cells[c]));
            }
            if (bold) sb.Append(@"\b0");
            sb.Append(@"\par").Append("\r\n");
        }
    }

    private static void Para(StringBuilder sb, int li, int fi, int sa, int sbTw, int ri,
                             int color, string content)
    {
        sb.Append(@"\pard\plain\f0\fs").Append(BodyFs).Append(@"\cf").Append(color)
          .Append(@"\li").Append(li).Append(@"\fi").Append(fi)
          .Append(@"\ri").Append(ri)
          .Append(@"\sb").Append(sbTw).Append(@"\sa").Append(sa).Append(' ')
          .Append(content).Append(@"\par").Append("\r\n");
    }

    // ---------------- 行内 ----------------

    private static void EmitTokens(StringBuilder sb, List<Token> tokens)
    {
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case Kind.Bold:
                    sb.Append(@"{\b ");
                    EmitTokens(sb, ParseInline(t.Text));
                    sb.Append('}');
                    break;
                case Kind.Italic:
                    sb.Append(@"{\i ");
                    EmitTokens(sb, ParseInline(t.Text));
                    sb.Append('}');
                    break;
                case Kind.Strike:
                    sb.Append(@"{\strike ");
                    AppendEscaped(sb, t.Text);
                    sb.Append('}');
                    break;
                case Kind.Code:
                    sb.Append(@"{\f1\fs").Append(CodeFs).Append(@"\cf").Append(CCodeFg)
                      .Append(@"\highlight").Append(CCodeBg).Append(' ');
                    AppendEscaped(sb, t.Text);
                    sb.Append(@"\highlight0}");
                    break;
                case Kind.Link:
                    sb.Append(@"{\cf").Append(CLink).Append(' ');
                    AppendEscaped(sb, t.Text);
                    sb.Append('}');
                    break;
                default:
                    AppendEscaped(sb, t.Text);
                    break;
            }
        }
    }

    /// <summary>RTF 转义：控制字符加反斜杠，非 ASCII 走 \uN?（N 为有符号 16 位）。</summary>
    private static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\t': sb.Append(@"\tab "); break;
                case '\n': sb.Append(@"\line "); break;
                case '\r': break;
                default:
                    if (c >= 32 && c < 128) sb.Append(c);
                    else
                    {
                        int v = c;
                        if (v > 32767) v -= 65536;      // RTF 里 \u 是 signed short
                        sb.Append(@"\u").Append(v).Append('?');
                    }
                    break;
            }
        }
    }

    private enum Kind { Normal, Bold, Italic, Strike, Code, Link }

    private readonly record struct Token(string Text, Kind Kind);

    /// <summary>行内语法扫描：只认 **粗体**、*斜体*、`代码`、~~删除线~~、[文字](链接)。</summary>
    private static List<Token> ParseInline(string s)
    {
        var list = new List<Token>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            list.Add(new Token(sb.ToString(), Kind.Normal));
            sb.Clear();
        }

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            // 只对 Markdown 的强调符号做转义；反斜杠本身不在这里折叠，
            // 否则 Windows 路径和正则里的 \\ 会被吃掉一层，看日志时最容易踩到
            if (c == '\\' && i + 1 < s.Length && "`*_~[]()#>-".Contains(s[i + 1]))
            {
                sb.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    list.Add(new Token(s[(i + 1)..end], Kind.Code));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    Flush();
                    list.Add(new Token(s[(i + 2)..end], Kind.Bold));
                    i = end + 2;
                    continue;
                }
            }

            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                int end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    Flush();
                    list.Add(new Token(s[(i + 2)..end], Kind.Strike));
                    i = end + 2;
                    continue;
                }
            }

            if (c == '*')
            {
                int end = s.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    list.Add(new Token(s[(i + 1)..end], Kind.Italic));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int rb = s.IndexOf(']', i + 1);
                if (rb > i + 1 && rb + 1 < s.Length && s[rb + 1] == '(')
                {
                    int rp = s.IndexOf(')', rb + 2);
                    if (rp > rb + 1)
                    {
                        Flush();
                        list.Add(new Token(s[(i + 1)..rb], Kind.Link));
                        i = rp + 1;
                        continue;
                    }
                }
            }

            sb.Append(c);
            i++;
        }

        Flush();
        if (list.Count == 0) list.Add(new Token("", Kind.Normal));
        return list;
    }

    // ---------------- 工具 ----------------

    private static int Measure(string text)
    {
        using var font = new Font("Consolas", CodeFs / 2f, FontStyle.Regular, GraphicsUnit.Point);
        return TextRenderer.MeasureText(text.Length == 0 ? " " : text, font).Width;
    }

    private static bool IsRule(string s)
    {
        if (s.Length < 3) return false;
        char c = s[0];
        if (c != '-' && c != '*' && c != '_') return false;
        return s.All(x => x == c || x == ' ');
    }

    private static bool IsTableSeparator(string line)
    {
        string t = line.Trim();
        if (t.Length < 3 || !t.Contains('-')) return false;
        return t.All(c => c is '-' or '|' or ':' or ' ');
    }

    private static string[] SplitRow(string line)
    {
        string t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(x => x.Trim()).ToArray();
    }
}
