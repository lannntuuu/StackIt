using System.Text.Json;

namespace StackIt.Core;

/// <summary>
/// 重新生成黄金基准（verify/baseline-*.json）。
///
/// 网页版 TS 解析器已删除，基准失去生成来源。本工具从当前（已确认正确的）
/// C# 解析结果导出同格式基准，供 Verify.Run 逐条对拍。
///
/// 用法: dotnet run -- baseline-gen &lt;记录.md&gt; &lt;输出目录&gt;
///
/// 警告: 只在人工确认「源数据的变化是对的」之后才运行；基准代表正确答案，
/// 不要为了让测试变绿而盲目重生。
/// </summary>
public static class BaselineGen
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static int Run(string markdownPath, string outDir)
    {
        var md = File.ReadAllText(markdownPath);
        var result = Importer.ParseMarkdown(md);
        var db = result.Db;
        var rep = result.Report;
        Directory.CreateDirectory(outDir);

        // ---- baseline-events.json: { count, rows: ["itemId|date|time|dose|slot"] } ----
        var rows = db.Events
            .Select(e => string.Join('|',
                e.ItemId, e.When.Date, e.When.Time ?? "",
                e.Dose?.ToString() ?? "", e.Slot ?? ""))
            .ToList();
        Write(Path.Combine(outDir, "baseline-events.json"), new
        {
            count = rows.Count,
            rows,
        });

        // ---- baseline-report.json ----
        Write(Path.Combine(outDir, "baseline-report.json"), new
        {
            weeks = rep.Weeks,
            mondayOK = rep.MondayOk,
            corrections = rep.Corrections
                .OrderBy(c => c.Line)
                .Select(c => new { line = c.Line, from = c.From, to = c.To }),
            events = rep.Events,
            unknownTokens = rep.UnknownTokens
                .Select(t => new { token = t.Token, count = t.Count }),
            noTimeCount = rep.NoTimeCount,
            skippedLines = rep.SkippedLines,
        });

        // ---- baseline-stats.json ----
        var s = Stats.Compute(db);
        Write(Path.Combine(outDir, "baseline-stats.json"), new
        {
            total = s.Total,
            dayCount = s.DayCount,
            first = s.First,
            last = s.Last,
            gapCount = s.Gaps.Count,
            itemCount = s.Items.Count,
            weekdayTotal = s.WeekdayTotal,
            slotTotal = s.SlotTotal,
            items = s.Items.Select(i => new
            {
                itemId = i.ItemId,
                count = i.Count,
                days = i.Days,
                coverage = i.Coverage,
            }),
        });

        // ---- baseline-history.json ----
        var h = HistoryCalc.Build(db);
        var weekSum = new int[h.Weeks.Count];
        foreach (var r in h.Rows)
            for (var i = 0; i < r.Weeks.Length; i++)
                weekSum[i] += r.Weeks[i];
        Write(Path.Combine(outDir, "baseline-history.json"), new
        {
            weeks = h.Weeks.Count,
            weekSum,
            rows = h.Rows.Select(r => new
            {
                itemId = r.ItemId,
                from = r.From,
                to = r.To,
                count = r.Count,
            }),
        });

        Console.WriteLine($"已生成基准到 {outDir}");
        Console.WriteLine($"  事件 {rows.Count} 条 / {rep.Weeks} 周 / 品类 {s.Items.Count} / 覆盖 {s.First} ~ {s.Last}");
        return 0;
    }

    private static void Write(string path, object obj) =>
        File.WriteAllText(path, JsonSerializer.Serialize(obj, Opt));
}
