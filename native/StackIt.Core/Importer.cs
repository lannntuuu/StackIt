using System.Text;
using System.Text.RegularExpressions;

namespace StackIt.Core;

public sealed class ParseReport
{
    public int Weeks { get; set; }
    public int MondayOk { get; set; }
    public List<(int Line, string From, string To)> Corrections { get; } = new();
    public int Events { get; set; }
    public List<(string Token, int Count)> UnknownTokens { get; set; } = new();
    public int NoTimeCount { get; set; }
    public List<int> SkippedLines { get; set; } = new();
}

public sealed class ParseResult
{
    public required Db Db { get; init; }
    public required ParseReport Report { get; init; }
}

/// <summary>
/// 记录.md 导入器。对应 src/lib/importer.ts，逐条对照移植。
///
/// 核心事实（已实测验证，勿推翻）：
///  - 文件倒序：最新周在最前
///  - 周表起始日语义上必须是周一 → 用此约束反推年份，127/127 通过
///  - 仅 3 周有 off-by-one 笔误：第 1747/1776/1805 行（按行号定位，不按日期）
///  - 列结构随时间「扩展」：3列 → 4列 → 5列 → 6列(2026-02-02 加「其他维生素」)
/// </summary>
public static class Importer
{
    /// <summary>标头 off-by-one 笔误：按原文行号精确限定，不能用日期做键。</summary>
    private static readonly Dictionary<int, int> OffByOneFix = new()
    {
        [1747] = 16, // 06-15 -> 06-16
        [1776] = 9,  // 06-08 -> 06-09
        [1805] = 2,  // 06-01 -> 06-02
    };

    private const int StartYear = 2023;

    /// <summary>
    /// 时间锚点。⚠️ 必须要求「时分紧连」（无空格），否则 `B2 2 B6 2` 里的 "2 2" 会被误判成时间。
    /// 允许 15.10 / 15:10 / 9.55 / 09.28 / 16..07（双点笔误）。
    /// </summary>
    private static readonly Regex TimeRe =
        new(@"(\d{1,2})[.:]{1,2}(\d{1,2})(?![\d.:])", RegexOptions.Compiled);

    private static readonly Regex DoseRe =
        new(@"^(\d+)\s*(片|粒|颗|篇|边|次)?$", RegexOptions.Compiled);

    private static readonly Regex HeaderRe =
        new(@"^###\s+(\d{2})-(\d{2})~", RegexOptions.Compiled);

    /// <summary>时间紧贴品名：17.06E</summary>
    private static readonly Regex GluedTimeItemRe =
        new(@"^(\d{1,2}[.:]\d{1,2})([A-Za-z一-鿿]+)$", RegexOptions.Compiled);

    private static readonly Regex PeelGluedRe =
        new(@"^(.+?)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// 列头 → 品类。
    /// 关键：很多单元格只有时间没有品名（如锌列写 `15.57`、米诺地尔列写 `23.00`），
    /// 此时品类由**列头**决定。缺了这步会漏掉大量事件。
    /// </summary>
    private static readonly Dictionary<string, string> ColItem = new()
    {
        ["维生素C"] = "vitC",
        ["维C"] = "vitC",
        ["维生素B复合"] = "bComplex",
        ["B复合"] = "bComplex",
        ["锌"] = "zinc",
        ["米诺地尔"] = "minoxidil",
        ["二硫化硒"] = "selenium",
    };

    private static readonly string[] Weekdays =
        { "周一", "周二", "周三", "周四", "周五", "周六", "周天", "周日" };

    private sealed record Hit(int Hh, int Mm, int Start, int End);

    private sealed record Seg(string? Time, string Body);

    public static ParseResult ParseMarkdown(string md)
    {
        // 按 \r?\n 切分，保留行号语义（1-based）
        var lines = md.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // ---- 1. 收集周表头（文件倒序 → 正序）----
        var headers = new List<(int Line, int Sm, int Sd)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = HeaderRe.Match(lines[i]);
            if (m.Success)
                headers.Add((i + 1, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }

        var asc = new List<(int Line, int Sm, int Sd)>();
        for (var i = headers.Count - 1; i >= 0; i--)
            asc.Add(headers[i]);

        var year = StartYear;
        var prevMonth = 99;
        var mondayOk = 0;
        var corrections = new List<(int, string, string)>();
        var secStart = new Dictionary<int, string>();

        foreach (var h in asc)
        {
            if (prevMonth != 99 && h.Sm < prevMonth) year++;
            prevMonth = h.Sm;

            var day = h.Sd;
            if (OffByOneFix.TryGetValue(h.Line, out var fixedDay))
            {
                var before = $"{year}-{h.Sm:00}-{h.Sd:00}";
                day = fixedDay;
                corrections.Add((h.Line, before, $"{year}-{h.Sm:00}-{day:00}"));
            }

            var start = $"{year}-{h.Sm:00}-{day:00}";
            if (DateOf(start).DayOfWeek == DayOfWeek.Monday) mondayOk++;
            secStart[h.Line] = start;
        }

        // ---- 2. 逐行解析 ----
        var events = new List<IntakeEvent>();
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = new List<int>();
        var noTimeCount = 0;

        int? curLine = null;
        string[]? rawCols = null;
        var curWeekday = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var ln = lines[i];

            var hm = HeaderRe.Match(ln);
            if (hm.Success)
            {
                curLine = i + 1;
                rawCols = null;
                curWeekday = 0;
                continue;
            }

            if (!ln.StartsWith('|')) continue;
            if (Regex.IsMatch(ln, @"^\|\s*-")) continue;

            var p = ln.Split('|').Select(s => s.Trim()).ToArray();
            if (curLine is null) continue;

            // 表头行：按列名映射，禁止按列位置（列数在 3~6 列间漂移）
            if (p.Any(c => c == "维生素C" || c == "维生素B复合"))
            {
                rawCols = p;
                continue;
            }

            if (rawCols is null) continue;

            // 星期列
            var wi = Array.IndexOf(Weekdays, p.Length > 1 ? p[1] : "");
            if (wi >= 0) curWeekday = wi;
            if (p.Length > 1 && p[1] == "总结") continue;
            if (curWeekday < 0) continue;

            var slot = p.Length > 2 && (p[2] == "早" || p[2] == "中" || p[2] == "晚") ? p[2] : null;
            var date = AddDays(secStart[curLine.Value], curWeekday);

            for (var ci = 3; ci < p.Length; ci++)
            {
                var cell = p[ci];
                if (string.IsNullOrEmpty(cell)) continue;

                var colName = ci < rawCols.Length ? rawCols[ci].Trim() : "";
                ColItem.TryGetValue(colName, out var fallbackItemId);

                foreach (var seg in SplitSegments(cell))
                {
                    var parsed = ParseSegment(seg.Body, unknown, fallbackItemId);
                    if (parsed.Count == 0)
                    {
                        // 段内既无品名也无剂量，但有时间 → 整格就是列头品类的一个事件
                        if (seg.Time is not null && fallbackItemId is not null)
                        {
                            parsed.Add((fallbackItemId, null));
                        }
                        else if (seg.Body.Trim().Length > 0)
                        {
                            skipped.Add(i + 1);
                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var when = seg.Time is not null
                        ? new When { Date = date, Time = seg.Time, Precision = "minute" }
                        : new When { Date = date, Time = null, Precision = "day" };
                    if (seg.Time is null) noTimeCount++;

                    foreach (var (itemId, dose) in parsed)
                    {
                        events.Add(new IntakeEvent
                        {
                            Id = Guid.NewGuid().ToString("N")[..12],
                            ItemId = itemId,
                            When = when,
                            Dose = dose,
                            Slot = slot,
                            CreatedAt = 0,
                            Source = new Source
                            {
                                Line = i + 1,
                                WeekStart = secStart[curLine.Value],
                                ColHeader = colName,
                                Raw = cell,
                            },
                        });
                    }
                }
            }
        }

        var report = new ParseReport
        {
            Weeks = asc.Count,
            MondayOk = mondayOk,
            Events = events.Count,
            NoTimeCount = noTimeCount,
            UnknownTokens = unknown
                .Select(kv => (kv.Key, kv.Value))
                .OrderByDescending(x => x.Item2)
                .ToList(),
            SkippedLines = skipped.Distinct().OrderBy(x => x).ToList(),
        };
        foreach (var c in corrections) report.Corrections.Add(c);

        return new ParseResult
        {
            Db = new Db { Version = 1, Events = events, Observations = new() },
            Report = report,
        };
    }

    /// <summary>以时间 token 为锚点切段；支持时间夹在条目中间。</summary>
    private static List<Seg> SplitSegments(string raw)
    {
        var hits = new List<Hit>();
        foreach (Match m in TimeRe.Matches(raw))
        {
            hits.Add(new Hit(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Index,
                m.Index + m.Length));
        }

        if (hits.Count == 0) return new() { new Seg(null, raw) };

        var outp = new List<Seg>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var end = i + 1 < hits.Count ? hits[i + 1].Start : raw.Length;
            outp.Add(new Seg(NormTime(h.Hh, h.Mm), raw.Substring(h.End, end - h.End)));
        }

        return outp;
    }

    private static string? NormTime(int hh, int mm)
    {
        var h = hh;
        var m = mm;
        if (h == 24 && m == 0) h = 0; // 24.00 → 00:00
        if (h > 23 || m > 59) return null;
        return $"{h:00}:{m:00}";
    }

    /// <summary>品名与剂量粘连：复合2 → (复合维生素B, 2)；鱼油1 → (鱼油, 1)</summary>
    private static (string Name, int Dose)? PeelGlued(string tok)
    {
        var m = PeelGluedRe.Match(tok);
        if (!m.Success) return null;
        var name = m.Groups[1].Value;
        if (Items.Match(name) is null) return null;
        return (name, int.Parse(m.Groups[2].Value));
    }

    /// <summary>
    /// 段内品名+剂量匹配。
    /// 处理三种写法：`B2 2`（品名在前）、`复合 B2 2`（品名在前无剂量）、`复合2`（粘连）。
    /// 纯数字段（如锌列的 `15.57` 剩空 body）由调用方用列头兜底。
    /// </summary>
    private static List<(string ItemId, int? Dose)> ParseSegment(
        string body,
        Dictionary<string, int> unknown,
        string? fallbackItemId)
    {
        var outp = new List<(string, int?)>();
        string? pending = null;

        var toks = body.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var tokRaw in toks)
        {
            // 时间紧贴品名：17.06E
            var t = tokRaw;
            var glued = GluedTimeItemRe.Match(t);
            if (glued.Success) t = glued.Groups[2].Value;

            // 先尝试粘连（复合2 / 鱼油1）：必须早于 Match，
            // 否则 `复合2` 会被 aliases 里的 '复合2' 整体命中，导致剂量丢失
            var pg = PeelGlued(t);
            if (pg is not null)
            {
                var it = Items.Match(pg.Value.Name);
                if (it is not null)
                {
                    outp.Add((it.Id, pg.Value.Dose));
                    pending = null;
                    continue;
                }
            }

            var item = Items.Match(t);
            if (item is not null)
            {
                if (pending is not null) outp.Add((pending, null));
                pending = item.Id;
                continue;
            }

            var d = DoseRe.Match(t);
            if (d.Success)
            {
                var n = int.Parse(d.Groups[1].Value);
                if (pending is not null)
                {
                    outp.Add((pending, n));
                    pending = null;
                }
                else if (outp.Count > 0)
                {
                    var last = outp[^1];
                    outp[^1] = (last.Item1, n);
                }

                continue;
            }

            unknown[t] = unknown.TryGetValue(t, out var c) ? c + 1 : 1;
        }

        if (pending is not null) outp.Add((pending, null));

        // 段内只有剂量、没有品名（如维生素C列写 `15.10 2`）→ 用列头兜底，并带上该剂量
        if (outp.Count == 0 && fallbackItemId is not null)
        {
            int? n = null;
            foreach (var t in toks)
                if (DoseRe.Match(t).Success)
                {
                    n = int.Parse(DoseRe.Match(t).Groups[1].Value);
                    break;
                }

            outp.Add((fallbackItemId, n));
        }

        return outp;
    }

    private static DateTime DateOf(string iso)
    {
        var parts = iso.Split('-');
        return new DateTime(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }

    private static string AddDays(string iso, int n)
    {
        var d = DateOf(iso).AddDays(n);
        return $"{d.Year:0000}-{d.Month:00}-{d.Day:00}";
    }
}