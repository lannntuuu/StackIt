using System.Text;
using System.Text.Json;

namespace StackIt.Core;

/// <summary>
/// 对拍：把 C# 解析结果与 TS 版导出的基准逐条比对。
/// 基准由 verify/make-baseline.mjs 生成，代表「已人工核验过的正确答案」。
/// </summary>
public static class Verify
{
    public static int Run(
        string markdownPath, string baselinePath, string reportPath,
        string? statsBaselinePath, string? historyBaselinePath)
    {
        var md = File.ReadAllText(markdownPath);
        var result = Importer.ParseMarkdown(md);
        var fails = new List<string>();

        JsonElement? statsBaseline = null, historyBaseline = null;
        if (statsBaselinePath is not null && File.Exists(statsBaselinePath))
            statsBaseline = JsonDocument.Parse(File.ReadAllText(statsBaselinePath)).RootElement.Clone();
        if (historyBaselinePath is not null && File.Exists(historyBaselinePath))
            historyBaseline = JsonDocument.Parse(File.ReadAllText(historyBaselinePath)).RootElement.Clone();

        // ---- 1. 报告数字 ----
        var baselineReport = JsonDocument.Parse(File.ReadAllText(reportPath)).RootElement;

        void Expect(string label, int actual, string baselineProp)
        {
            var expected = baselineReport.GetProperty(baselineProp).GetInt32();
            var ok = actual == expected;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}  C#={actual}  TS={expected}");
            if (!ok) fails.Add($"{label}: C#={actual} TS={expected}");
        }

        Console.WriteLine("报告数字：");
        Expect("周数", result.Report.Weeks, "weeks");
        Expect("周一校验通过数", result.Report.MondayOk, "mondayOK");
        Expect("事件数", result.Report.Events, "events");
        Expect("无时间条数", result.Report.NoTimeCount, "noTimeCount");

        // 修正行号必须完全一致（这三处是原文笔误，改错就全盘皆错）
        var csFix = result.Report.Corrections
            .Select(c => (c.Line, c.From, c.To))
            .OrderBy(c => c.Line).ToList();
        var tsFix = baselineReport.GetProperty("corrections").EnumerateArray()
            .Select(e => (
                Line: e.GetProperty("line").GetInt32(),
                From: e.GetProperty("from").GetString()!,
                To: e.GetProperty("to").GetString()!))
            .OrderBy(c => c.Line).ToList();
        var fixOk = csFix.Count == tsFix.Count &&
                    csFix.Zip(tsFix).All(p => p.First == p.Second);
        Console.WriteLine($"  {(fixOk ? "PASS" : "FAIL")}  笔误修正 {csFix.Count} 处");
        if (!fixOk)
        {
            fails.Add("笔误修正不一致");
            foreach (var c in csFix) Console.WriteLine($"        C# 第{c.Line}行 {c.From} -> {c.To}");
            foreach (var c in tsFix) Console.WriteLine($"        TS 第{c.Line}行 {c.From} -> {c.To}");
        }

        // 未知 token 必须一致（多了说明匹配漏了，少了说明误吞了）
        var csUnknown = result.Report.UnknownTokens
            .Select(t => $"{t.Token}x{t.Count}").OrderBy(s => s).ToList();
        var tsUnknown = baselineReport.GetProperty("unknownTokens").EnumerateArray()
            .Select(e => $"{e.GetProperty("token").GetString()}x{e.GetProperty("count").GetInt32()}")
            .OrderBy(s => s).ToList();
        var unkOk = csUnknown.SequenceEqual(tsUnknown);
        Console.WriteLine($"  {(unkOk ? "PASS" : "FAIL")}  未知 token：{string.Join(" ", csUnknown)}");
        if (!unkOk)
        {
            fails.Add("未知 token 不一致");
            Console.WriteLine($"        TS: {string.Join(" ", tsUnknown)}");
        }

        // ---- 2. 逐条事件比对 ----
        Console.WriteLine("\n逐条事件：");
        var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath)).RootElement;
        var tsRows = baseline.GetProperty("rows").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

        var csRows = result.Db.Events
            .Select(e => string.Join('|',
                e.ItemId, e.When.Date, e.When.Time ?? "",
                e.Dose?.ToString() ?? "", e.Slot ?? ""))
            .ToList();

        var csSet = csRows.ToHashSet(StringComparer.Ordinal);

        var missing = tsRows.Except(csSet, StringComparer.Ordinal).ToList();
        var extra = csSet.Except(tsRows, StringComparer.Ordinal).ToList();

        var sameCount = tsRows.Count == csRows.Count;
        Console.WriteLine($"  {(sameCount ? "PASS" : "FAIL")}  事件条数  C#={csRows.Count}  TS={tsRows.Count}");

        if (missing.Count == 0)
        {
            Console.WriteLine("  PASS  无遗漏（TS 有而 C# 没有）");
        }
        else
        {
            Console.WriteLine($"  FAIL  遗漏 {missing.Count} 条，前 10：");
            foreach (var m in missing.Take(10)) Console.WriteLine($"        {m}");
            fails.Add($"遗漏 {missing.Count} 条");
        }

        if (extra.Count == 0)
        {
            Console.WriteLine("  PASS  无多余（C# 有而 TS 没有）");
        }
        else
        {
            Console.WriteLine($"  FAIL  多出 {extra.Count} 条，前 10：");
            foreach (var m in extra.Take(10)) Console.WriteLine($"        {m}");
            fails.Add($"多出 {extra.Count} 条");
        }

        // ---- 3. 统计算法对拍 ----
        Console.WriteLine("\n统计算法：");
        CheckStats(result.Db, statsBaseline, fails);

        // ---- 4. 历史分桶对拍 ----
        Console.WriteLine("\n历史分桶：");
        CheckHistory(result.Db, historyBaseline, fails);

        // ---- 5. 筛选不塌缩窗口 ----
        Console.WriteLine("\n时间范围筛选：");
        CheckRange(result.Db, fails);

        Console.WriteLine();
        if (fails.Count == 0)
        {
            Console.WriteLine($"结论: C# 与 TS 版完全一致（解析 {csRows.Count} 条，统计与分桶亦一致）");
            return 0;
        }

        Console.WriteLine($"结论: {fails.Count} 项不一致");
        foreach (var f in fails) Console.WriteLine("  - " + f);
        return 1;
    }

    /// <summary>
    /// 筛选必须**不塌缩可记窗口**。
    ///
    /// 这是最容易悄悄做错的一处：如果先筛数据再 Build，每个品类的窗口都会变成
    /// "所选范围内首次出现 → 末次出现"，带视图就会声称「锌 2026 年 7 月才开始记」——
    /// 而它其实记了 488 次、跨 142 周。那是本产品明确禁止的谎：
    /// 空白分两种（那时记录表还没有这一列 / 记了但那周空着），塌缩会把两者混成「已停」。
    ///
    /// 所以断言：筛到任何一段，每一行的 From/To/Count 都和全量完全一致，
    /// 只有 Weeks 数组里范围外的格子被清零。
    /// </summary>
    private static void CheckRange(Db db, List<string> fails)
    {
        var full = HistoryCalc.Build(db);
        if (full.Rows.Count == 0)
        {
            Console.WriteLine("  SKIP  没有数据");
            return;
        }

        // 取中间那一年，故意不取边界：边界那年可能整年都是空的，测不出塌缩
        var year = full.Weeks[full.Weeks.Count / 2].Substring(0, 4);
        var sel = HistoryCalc.BuildRange(db, w => w.StartsWith(year, StringComparison.Ordinal));

        void Assert(string label, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}  {detail}");
            if (!ok) fails.Add($"{label}: {detail}");
        }

        Assert("筛选后周轴不变", sel.Weeks.Count == full.Weeks.Count,
            $"{sel.Weeks.Count} vs {full.Weeks.Count}");

        Assert("筛选后标出范围", sel.HasRange, $"InRange {sel.InRangeFrom}..{sel.InRangeTo}");

        // 窗口与总次数必须一模一样
        var badWindow = sel.Rows
            .Select((r, i) => (r, i))
            .Where(t => t.r.From != full.Rows[t.i].From || t.r.To != full.Rows[t.i].To)
            .Select(t => $"{t.r.Name} {t.r.From}..{t.r.To} vs 全量 {full.Rows[t.i].From}..{full.Rows[t.i].To}")
            .ToList();
        Assert("筛选后窗口不塌缩", badWindow.Count == 0,
            badWindow.Count == 0 ? $"{sel.Rows.Count} 个品类全一致"
                                 : string.Join("; ", badWindow.Take(3)));

        var badCount = sel.Rows
            .Select((r, i) => (r, i))
            .Where(t => t.r.Count != full.Rows[t.i].Count)
            .Select(t => $"{t.r.Name} {t.r.Count} vs {full.Rows[t.i].Count}")
            .ToList();
        Assert("筛选后总次数不变", badCount.Count == 0,
            badCount.Count == 0 ? $"{sel.Rows.Count} 个品类全一致"
                                : string.Join("; ", badCount.Take(3)));

        // 范围外的格子必须清零，且不能出现负数或越界
        var outside = 0;
        for (var i = 0; i < sel.Weeks.Count; i++)
        {
            if (i >= sel.InRangeFrom && i <= sel.InRangeTo) continue;
            foreach (var r in sel.Rows)
                if (r.Weeks[i] != 0) outside++;
        }
        Assert("范围外清零", outside == 0, outside == 0 ? "无残留" : $"{outside} 个格子残留");

        // 范围内至少要有数据，否则这次筛选等于什么都没查到
        var inside = 0;
        for (var i = sel.InRangeFrom; i <= sel.InRangeTo; i++)
            foreach (var r in sel.Rows)
                inside += r.Weeks[i];
        Assert("范围内有数据", inside > 0, $"{year} 年内 {inside} 次");
    }

    /// <summary>统计结果对拍：总数、天数、每品类次数与覆盖率、周/时段分布、断档。</summary>
    private static void CheckStats(Db db, JsonElement? baseline, List<string> fails)
    {
        if (baseline is null)
        {
            Console.WriteLine("  跳过（无基准文件）");
            return;
        }

        var r = Stats.Compute(db);
        var b = baseline.Value;

        void Cmp(string label, int actual, string prop)
        {
            var expected = b.GetProperty(prop).GetInt32();
            var ok = actual == expected;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}  C#={actual}  TS={expected}");
            if (!ok) fails.Add($"统计.{label}: C#={actual} TS={expected}");
        }

        void CmpStr(string label, string? actual, string prop)
        {
            var expected = b.GetProperty(prop).GetString();
            var ok = actual == expected;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}  C#={actual}  TS={expected}");
            if (!ok) fails.Add($"统计.{label}: C#={actual} TS={expected}");
        }

        Cmp("总条数", r.Total, "total");
        Cmp("有记录天数", r.DayCount, "dayCount");
        CmpStr("最早日期", r.First, "first");
        CmpStr("最晚日期", r.Last, "last");
        Cmp("断档数", r.Gaps.Count, "gapCount");
        Cmp("品类数", r.Items.Count, "itemCount");

        // 每品类的次数与覆盖率必须逐一相等
        var tsItems = b.GetProperty("items").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("itemId").GetString()!,
                e => (Count: e.GetProperty("count").GetInt32(),
                      Days: e.GetProperty("days").GetInt32(),
                      Cov: e.GetProperty("coverage").GetDouble()))
            .ToDictionary(k => k.Key, v => v.Value);

        var itemMismatch = new List<string>();
        foreach (var it in r.Items)
        {
            if (!tsItems.TryGetValue(it.ItemId, out var t))
            {
                itemMismatch.Add($"{it.ItemId}: TS 无此项");
                continue;
            }

            if (t.Count != it.Count || t.Days != it.Days || Math.Abs(t.Cov - it.Coverage) > 1e-9)
                itemMismatch.Add(
                    $"{it.ItemId}: C#(count={it.Count},days={it.Days},cov={it.Coverage:0.0000}) " +
                    $"TS(count={t.Count},days={t.Days},cov={t.Cov:0.0000})");
        }

        if (itemMismatch.Count == 0)
        {
            Console.WriteLine($"  PASS  {r.Items.Count} 个品类的次数/天数/覆盖率全等");
        }
        else
        {
            Console.WriteLine($"  FAIL  {itemMismatch.Count} 个品类不一致，前 5：");
            foreach (var m in itemMismatch.Take(5)) Console.WriteLine("        " + m);
            fails.Add($"统计品类不一致 {itemMismatch.Count} 个");
        }

        // 星期分布
        var tsWeek = b.GetProperty("weekdayTotal").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var weekOk = tsWeek.Length == 7 && !tsWeek.Where((t, i) => t != r.WeekdayTotal[i]).Any();
        Console.WriteLine($"  {(weekOk ? "PASS" : "FAIL")}  星期分布  C#=[{string.Join(",", r.WeekdayTotal)}]");
        if (!weekOk)
        {
            fails.Add("星期分布不一致");
            Console.WriteLine($"        TS=[{string.Join(",", tsWeek)}]");
        }

        // 时段分布
        var tsSlot = b.GetProperty("slotTotal").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var slotOk = HistoryCalc.Slots.All(s => r.SlotTotal[s] == tsSlot.GetValueOrDefault(s, -1));
        Console.WriteLine($"  {(slotOk ? "PASS" : "FAIL")}  时段分布  " +
                          $"早{r.SlotTotal["早"]} 中{r.SlotTotal["中"]} 晚{r.SlotTotal["晚"]}");
        if (!slotOk) fails.Add("时段分布不一致");
    }

    /// <summary>历史分桶对拍：周表长度、每品类可记窗口、每周次数。</summary>
    private static void CheckHistory(Db db, JsonElement? baseline, List<string> fails)
    {
        if (baseline is null)
        {
            Console.WriteLine("  跳过（无基准文件）");
            return;
        }

        var h = HistoryCalc.Build(db);
        var b = baseline.Value;

        var tsWeeks = b.GetProperty("weeks").GetInt32();
        var weeksOk = h.Weeks.Count == tsWeeks;
        Console.WriteLine($"  {(weeksOk ? "PASS" : "FAIL")}  全局周数  C#={h.Weeks.Count}  TS={tsWeeks}");
        if (!weeksOk) fails.Add($"周表长度 C#={h.Weeks.Count} TS={tsWeeks}");

        // 每周总额必须一致（防止 dose 累加或窗口边界算错）
        var csWeekSum = new int[h.Weeks.Count];
        foreach (var r in h.Rows)
            for (var i = 0; i < r.Weeks.Length; i++)
                csWeekSum[i] += r.Weeks[i];

        var tsWeekSum = b.GetProperty("weekSum").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var sumOk = tsWeekSum.Length == csWeekSum.Length &&
                    !tsWeekSum.Where((t, i) => t != csWeekSum[i]).Any();
        Console.WriteLine($"  {(sumOk ? "PASS" : "FAIL")}  每周累计次数全等（{csWeekSum.Length} 周）");
        if (!sumOk)
        {
            fails.Add("每周累计不一致");
            for (var i = 0; i < Math.Min(tsWeekSum.Length, csWeekSum.Length); i++)
                if (tsWeekSum[i] != csWeekSum[i])
                {
                    Console.WriteLine($"        第{i}周({h.Weeks[i]}) C#={csWeekSum[i]} TS={tsWeekSum[i]}");
                    break;
                }
        }

        // 每个品类的可记窗口
        var tsRows = b.GetProperty("rows").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("itemId").GetString()!,
                e => (From: e.GetProperty("from").GetInt32(),
                      To: e.GetProperty("to").GetInt32(),
                      Count: e.GetProperty("count").GetInt32()))
            .ToDictionary(k => k.Key, v => v.Value);

        var rowMismatch = new List<string>();
        foreach (var r in h.Rows)
        {
            if (!tsRows.TryGetValue(r.ItemId, out var t))
            {
                rowMismatch.Add($"{r.ItemId}: TS 无此行");
                continue;
            }

            if (t.From != r.From || t.To != r.To || t.Count != r.Count)
                rowMismatch.Add(
                    $"{r.ItemId}: C#(from={r.From},to={r.To},count={r.Count}) " +
                    $"TS(from={t.From},to={t.To},count={t.Count})");
        }

        if (rowMismatch.Count == 0)
        {
            Console.WriteLine($"  PASS  {h.Rows.Count} 个品类的可记窗口与总数全等");
        }
        else
        {
            Console.WriteLine($"  FAIL  {rowMismatch.Count} 个品类窗口不一致，前 5：");
            foreach (var m in rowMismatch.Take(5)) Console.WriteLine("        " + m);
            fails.Add($"可记窗口不一致 {rowMismatch.Count} 个");
        }
    }
}