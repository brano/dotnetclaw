using System.Text;
using System.Text.RegularExpressions;

namespace DotnetClaw.UI;

/// <summary>
/// Incrementally converts a practical subset of Markdown to ANSI SGR sequences
/// for terminals (xterm.js and modern Windows / Unix consoles).
/// </summary>
public sealed class MarkdownAnsiStreamFormatter
{
    private const string AnsiReset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Italic = "\x1b[3m";
    private const string Dim = "\x1b[2m";
    // Headers: strong visual hierarchy (no HTML — ANSI only)
    private static ReadOnlySpan<char> HeaderOpen(int level) => level switch
    {
        1 => "\x1b[1;4;97m", // bold + underline + bright white
        2 => "\x1b[1;96m",   // bold cyan
        3 => "\x1b[1;94m",   // bold blue
        4 => "\x1b[1;93m",   // bold yellow
        _ => "\x1b[1;90m",   // bold grey
    };

    private string _pending = string.Empty;

    public void Reset() => _pending = string.Empty;

    /// <summary>Append a streamed chunk from the model; returns text to write to the terminal (uses CRLF).</summary>
    public string Append(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return string.Empty;

        var norm = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        _pending += norm;

        var sb = new StringBuilder();
        while (true)
        {
            var nl = _pending.IndexOf('\n');
            if (nl < 0)
                break;

            var line = _pending[..nl];
            _pending = _pending[(nl + 1)..];
            sb.Append(FormatFullLine(line));
            sb.Append("\r\n");
        }

        var (safe, hold) = SplitHoldBack(_pending);
        _pending = hold;
        if (safe.Length > 0)
            sb.Append(FormatPartialLineContent(safe));

        return sb.ToString();
    }

    /// <summary>Flush trailing partial line (call at end of assistant turn).</summary>
    public string Flush()
    {
        if (_pending.Length == 0)
            return string.Empty;

        var s = TryFormatBlockLine(_pending, out var block)
            ? block
            : FormatInline(_pending, flush: true);
        _pending = string.Empty;
        return s;
    }

    private static string FormatFullLine(string line)
        => TryFormatBlockLine(line, out var rest)
            ? rest
            : FormatPartialLineContent(line);

    private static bool TryFormatBlockLine(string line, out string formatted)
    {
        formatted = string.Empty;
        var m = Regex.Match(line, @"^(\s{0,3})(#{1,6})\s+(.*)$");
        if (m.Success)
        {
            var level = m.Groups[2].Value.Length;
            var text = m.Groups[3].Value;
            formatted = $"{HeaderOpen(level)}{FormatInline(text, flush: true)}{AnsiReset}";
            return true;
        }

        m = Regex.Match(line, @"^(\s*)([-*])\s+(.*)$");
        if (m.Success)
        {
            var indent = m.Groups[1].Value;
            var content = m.Groups[3].Value;
            formatted =
                $"{indent}{Dim}•{AnsiReset} {FormatInline(content, flush: true)}";
            return true;
        }

        m = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.*)$");
        if (m.Success)
        {
            var indent = m.Groups[1].Value;
            var num = m.Groups[2].Value;
            var content = m.Groups[3].Value;
            formatted =
                $"{indent}{Dim}{num}.{AnsiReset} {FormatInline(content, flush: true)}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Moves a short ambiguous suffix back to <paramref name="hold"/> so chunk boundaries
    /// do not split markdown delimiters.
    /// </summary>
    private static (string safe, string hold) SplitHoldBack(string line)
    {
        if (line.Length == 0)
            return (string.Empty, string.Empty);

        // Incomplete ATX header marker (e.g. "##" still growing)
        if (Regex.IsMatch(line, @"^\s{0,3}#{1,6}$"))
            return (string.Empty, line);

        var unclosed = UnclosedInlineHoldStart(line);
        if (unclosed >= 0)
            return (line[..unclosed], line[unclosed..]);

        var holdFrom = line.Length;
        var i = line.Length - 1;

        // Odd run of trailing backticks → hold entire run (inline code fence)
        var bt = 0;
        while (i >= 0 && line[i] == '`')
        {
            bt++;
            i--;
        }

        if (bt % 2 == 1)
            holdFrom = Math.Min(holdFrom, i + 1);

        // Trailing * or ** (or __ / _) — up to 2 of each class from end
        i = line.Length - 1;
        var starRun = 0;
        while (i >= 0 && line[i] == '*')
        {
            starRun++;
            i--;
        }

        if (starRun is > 0 and <= 2)
            holdFrom = Math.Min(holdFrom, line.Length - starRun);

        i = line.Length - 1;
        var usRun = 0;
        while (i >= 0 && line[i] == '_')
        {
            usRun++;
            i--;
        }

        if (usRun is > 0 and <= 2)
            holdFrom = Math.Min(holdFrom, line.Length - usRun);

        if (holdFrom >= line.Length)
            return (line, string.Empty);

        return (line[..holdFrom], line[holdFrom..]);
    }

    /// <returns>Index of opening delimiter for an unclosed inline construct, or -1.</returns>
    private static int UnclosedInlineHoldStart(string line)
    {
        var inCode = false;
        var inBold = false;
        var inItalic = false;
        var boldStart = -1;
        var italicStart = -1;
        var codeStart = -1;
        var i = 0;

        while (i < line.Length)
        {
            if (line[i] == '`')
            {
                if (!inCode)
                {
                    inCode = true;
                    codeStart = i;
                }
                else
                {
                    inCode = false;
                    codeStart = -1;
                }

                i++;
                continue;
            }

            if (inCode)
            {
                i++;
                continue;
            }

            if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
            {
                if (!inBold)
                {
                    inBold = true;
                    boldStart = i;
                }
                else
                {
                    inBold = false;
                    boldStart = -1;
                }

                i += 2;
                continue;
            }

            if (line[i] == '*')
            {
                if (!inItalic)
                {
                    inItalic = true;
                    italicStart = i;
                }
                else
                {
                    inItalic = false;
                    italicStart = -1;
                }

                i++;
                continue;
            }

            if (i + 1 < line.Length && line[i] == '_' && line[i + 1] == '_')
            {
                if (!inBold)
                {
                    inBold = true;
                    boldStart = i;
                }
                else
                {
                    inBold = false;
                    boldStart = -1;
                }

                i += 2;
                continue;
            }

            if (line[i] == '_')
            {
                if (!inItalic)
                {
                    inItalic = true;
                    italicStart = i;
                }
                else
                {
                    inItalic = false;
                    italicStart = -1;
                }

                i++;
                continue;
            }

            i++;
        }

        if (inCode && codeStart >= 0)
            return codeStart;
        if (inBold && boldStart >= 0)
            return boldStart;
        if (inItalic && italicStart >= 0)
            return italicStart;
        return -1;
    }

    private static string FormatPartialLineContent(string line)
    {
        if (TryFormatBlockLine(line, out var block))
            return block;
        return FormatInline(line, flush: true);
    }

    /// <summary>Inline bold/italic/code for a single line (no newline).</summary>
    private static string FormatInline(string line, bool flush)
    {
        var sb = new StringBuilder();
        var i = 0;
        var openBold = false;
        var openItalic = false;
        var openCode = false;

        while (i < line.Length)
        {
            if (!openCode && i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
            {
                sb.Append(openBold ? AnsiReset : Bold);
                openBold = !openBold;
                i += 2;
                continue;
            }

            if (!openCode && i + 1 < line.Length && line[i] == '_' && line[i + 1] == '_')
            {
                sb.Append(openBold ? AnsiReset : Bold);
                openBold = !openBold;
                i += 2;
                continue;
            }

            if (!openCode && line[i] == '`')
            {
                sb.Append(openCode ? AnsiReset : Dim);
                openCode = !openCode;
                i++;
                continue;
            }

            if (!openBold && !openCode && line[i] == '*')
            {
                sb.Append(openItalic ? AnsiReset : Italic);
                openItalic = !openItalic;
                i++;
                continue;
            }

            if (!openBold && !openCode && line[i] == '_')
            {
                sb.Append(openItalic ? AnsiReset : Italic);
                openItalic = !openItalic;
                i++;
                continue;
            }

            sb.Append(line[i]);
            i++;
        }

        if (flush)
        {
            if (openCode)
                sb.Append(AnsiReset);
            if (openItalic)
                sb.Append(AnsiReset);
            if (openBold)
                sb.Append(AnsiReset);
        }
        else
        {
            // Streaming: close styles so the next chunk can reopen cleanly
            if (openCode || openItalic || openBold)
                sb.Append(AnsiReset);
        }

        return sb.ToString();
    }
}
