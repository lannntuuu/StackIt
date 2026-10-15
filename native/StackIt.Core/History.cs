namespace StackIt.Core;

public sealed class HistoryRow
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Route { get; init; }
    /// <summary>可记窗口（周一）在全局周表中的下标，闭区间</summary>
    public required int From { get; init; }
    public required int To { get; init; }
    /// <summary>每格次数，长度 = 全局周数；窗口外恒为 0 且应视为「无意义」</summary>
    public required int[] Weeks { get; init; }
    /// <summary>窗口内的有记录周数</summary>
    public required int ActiveWeeks { get; init; }
    public required int Count { get; init; }
    public required string First { get; init; }
    public required string Last { get; init; }
}

public sealed class History
{
    /// <summary>全局周表（每周一）</summary>
    public List<string> Weeks { get; init; } = new();
    public List<HistoryRow> Rows { get; init; } = new();

    /// <summary>
    /// 筛选命中的周下标闭区间；未筛选时为 -1（表示没有范围）。
    /// 视图据此裁掉范围外的部分：范围外**留白**，而不是画成 0 厚度发丝 ——
    /// 发丝会被读成"当时在吃、只是吃少了"，那是另一种谎。
    /// </summary>
    public int InRangeFrom { get; set; } = -1;

    public int InRangeTo { get; set; } = -1;

    /// <summary>是否有生效的范围。</summary>
    public bool HasRange => InRangeFrom >= 0;
}

public static class HistoryCalc
{
    public static readonly string[] Slots = { "早", "中", "晚" };

    /// <summary>该日期所在周的周一</summary>
    public static string MondayOf(string date)
    {
        var d = Stats.DateOf(date);
        var back = ((int)d.DayOfWeek + 6) % 7; // 周一=0
        return Stats.Iso(d.AddDays(-back));
    }

    public static string AddDays(string iso, int n) => Stats.Iso(Stats.DateOf(iso).AddDays(n));

    /// <summary>
    /// 格视图用的时间分桶。
    ///
    /// 关键规则（数据证明，勿改）：记录表的列是随时间**增加**的（3 → 6 列）。
    /// 所以某品类在某段时间为空白，可能是「那时还没有这一列」，不是「停了」。
    /// 因此每个品类只统计自己的**可记窗口**，窗口外一律留白，
    /// 绝不画成 0，也绝不标注「已停」。
    /// </summary>
    public static History Build(Db db)
    {
        var evs = db.Events;
        if (evs.Count == 0) return new History();

        var gStart = MondayOf(evs.Min(e => e.When.Date)!);
        var gEnd = MondayOf(evs.Max(e => e.When.Date)!);

        var weeks = new List<string>();
        var index = new Dictionary<string, int>();
        for (var d = gStart; d.CompareTo(gEnd) <= 0; d = AddDays(d, 7))
        {
            index[d] = weeks.Count;
            weeks.Add(d);
        }

        var n = weeks.Count;

        // 每个品类一个桶：可记窗口 + 每周次数
        var byItem = new Dictionary<string, (string Min, string Max, int[] Wk, int Count)>();
        foreach (var e in evs)
        {
            var dose = e.Dose ?? 1;
            if (!byItem.TryGetValue(e.ItemId, out var r))
                r = (e.When.Date, e.When.Date, new int[n], 0);

            var min = string.CompareOrdinal(e.When.Date, r.Min) < 0 ? e.When.Date : r.Min;
            var max = string.CompareOrdinal(e.When.Date, r.Max) > 0 ? e.When.Date : r.Max;
            if (index.TryGetValue(MondayOf(e.When.Date), out var wi))
                r.Wk[wi] += dose;
            r.Count += dose;

            byItem[e.ItemId] = (min, max, r.Wk, r.Count);
        }

        var rows = new List<HistoryRow>();
        foreach (var (itemId, r) in byItem)
        {
            var from = index[MondayOf(r.Min)];
            var to = index[MondayOf(r.Max)];
            var def = Items.Get(itemId);

            var activeWeeks = 0;
            for (var i = from; i <= to; i++)
                if (r.Wk[i] > 0)
                    activeWeeks++;

            rows.Add(new HistoryRow
            {
                ItemId = itemId,
                Name = def?.Name ?? itemId,
                Route = def?.Route ?? "oral",
                From = from,
                To = to,
                Weeks = r.Wk,
                ActiveWeeks = activeWeeks,
                Count = r.Count,
                First = r.Min,
                Last = r.Max,
            });
        }

        rows.Sort((a, b) => b.Count.CompareTo(a.Count));
        return new History { Weeks = weeks, Rows = rows };
    }

    /// <summary>
    /// 按时间范围收窄的格视图数据。
    ///
    /// 关键：**时间轴不收窄**，只有每周的次数被清零。
    ///
    /// 为什么不能"先筛数据再 Build"：可记窗口是 Build 从传入数据推出来的
    /// （每个品类的首末周）。只喂 2026-07 的数据，所有品类的窗口都会塌缩成
    /// 那一个月，格视图会声称「锌 2026 年 7 月才开始记」——而它其实记了 488 次。
    /// 那是这个产品明确禁止的谎：空白分两种（那时还没这一列 / 记了但那周空着），
    /// 塌缩窗口会把两者混成「已停」。
    ///
    /// 所以这里用全量数据算出真实窗口，再把范围外的周清零。
    /// 清零后的空白是"这段时间没有记录"，语义仍然是诚实的。
    /// </summary>
    public static History BuildRange(Db db, Func<string, bool> inRange)
    {
        var h = Build(db);

        if (h.Weeks.Count == 0) return h;

        // 记下范围内第一 / 最后一列，供视图裁掉范围外的部分。
        // 视图不画范围外，而不是画成 0 厚度发丝 —— 那条发丝会被读成
        // "当时在吃、只是吃少了"，那是另一种谎。留白只表示"不在这次看的范围里"。
        var first = -1;
        var last = -1;
        for (var i = 0; i < h.Weeks.Count; i++)
        {
            if (!inRange(h.Weeks[i])) continue;
            if (first < 0) first = i;
            last = i;
        }

        h.InRangeFrom = first;
        h.InRangeTo = last;

        // 范围外的周清零，避免聚合数字被范围外的数据撑大。
        for (var i = 0; i < h.Weeks.Count; i++)
            if (!inRange(h.Weeks[i]))
                foreach (var row in h.Rows)
                    row.Weeks[i] = 0;

        return h;
    }

    /// <summary>某天某时段各品类的已记剂量：key = $"{itemId}|{slot}"</summary>
    public static Dictionary<string, int> DosesOfDay(Db db, string date)
    {
        var outp = new Dictionary<string, int>();
        foreach (var e in db.Events)
        {
            if (e.When.Date != date) continue;
            var k = $"{e.ItemId}|{e.Slot}";
            outp[k] = outp.TryGetValue(k, out var c) ? c + (e.Dose ?? 1) : (e.Dose ?? 1);
        }

        return outp;
    }

    /// <summary>面板里要列出的品类：近期常用优先，可记窗口内有无记录次之</summary>
    public static List<string> SheetItems(Db db, int limit)
    {
        var h = Build(db);
        if (h.Rows.Count == 0)
            return Items.All.Where(i => i.Route == "oral").Select(i => i.Id).Take(limit).ToList();

        var lastWeek = h.Weeks.Count - 1;
        var scored = h.Rows
            .Where(r => r.Route == "oral")
            .Select(r =>
            {
                var recent = 0;
                for (var i = Math.Max(r.From, lastWeek - 3); i <= r.To; i++)
                    recent += r.Weeks[i];
                return (Id: r.ItemId, Recent: recent, Total: r.Count);
            })
            .OrderByDescending(s => s.Recent).ThenByDescending(s => s.Total)
            .ToList();

        return scored.Take(limit).Select(s => s.Id).ToList();
    }
}