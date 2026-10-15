namespace StackIt.Core;

public sealed class ItemStat
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required string Route { get; init; }
    /// <summary>有记录的天数</summary>
    public required int Days { get; init; }
    /// <summary>记录总条数</summary>
    public required int Count { get; init; }
    public required string First { get; init; }
    public required string Last { get; init; }
    /// <summary>平均每天几条</summary>
    public required double PerDay { get; init; }
    public required Dictionary<string, int> Slots { get; init; }
    /// <summary>覆盖率 = 有记录天数 / 整个跨度天数</summary>
    public required double Coverage { get; init; }
}

public sealed class Gap
{
    public required string From { get; init; }
    public required string To { get; init; }
    /// <summary>中间空了多少天</summary>
    public required int Days { get; init; }
}

public sealed class StatsResult
{
    public int Total { get; set; }
    public int DayCount { get; set; }
    public string? First { get; set; }
    public string? Last { get; set; }
    public List<ItemStat> Items { get; set; } = new();
    public Dictionary<string, int> SlotTotal { get; set; } = new();
    /// <summary>0=周日 .. 6=周六（对应 JS Date.getDay()）</summary>
    public int[] WeekdayTotal { get; set; } = new int[7];
    public List<Gap> Gaps { get; set; } = new();
}

/// <summary>统计算法。对应 src/lib/stats.ts。</summary>
public static class Stats
{
    public static int DayDiff(string a, string b)
    {
        var da = DateOf(a);
        var db = DateOf(b);
        return (int)(db - da).TotalDays;
    }

    public static DateTime DateOf(string iso)
    {
        var p = iso.Split('-');
        return new DateTime(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
    }

    public static string Iso(DateTime d) => $"{d.Year:0000}-{d.Month:00}-{d.Day:00}";

    public static StatsResult Compute(Db db)
    {
        var evs = db.Events;
        var r = new StatsResult
        {
            SlotTotal = new Dictionary<string, int> { ["早"] = 0, ["中"] = 0, ["晚"] = 0 },
            WeekdayTotal = new int[7],
        };

        if (evs.Count == 0) return r;

        var dates = evs.Select(e => e.When.Date).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var first = dates[0];
        var last = dates[^1];
        var span = DayDiff(first, last) + 1;

        foreach (var e in evs)
        {
            if (e.Slot is not null && r.SlotTotal.ContainsKey(e.Slot))
                r.SlotTotal[e.Slot]++;
            // 注意：这里用 DateTime.DayOfWeek 映射到 0=周日，与 TS 的 getDay() 一致
            var dow = (int)DateOf(e.When.Date).DayOfWeek;
            r.WeekdayTotal[dow]++;
        }

        var items = new List<ItemStat>();
        foreach (var group in evs.GroupBy(e => e.ItemId))
        {
            var def = Items.Get(group.Key);
            var ds = group.Select(e => e.When.Date).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var slots = new Dictionary<string, int> { ["早"] = 0, ["中"] = 0, ["晚"] = 0 };
            foreach (var e in group)
                if (e.Slot is not null && slots.ContainsKey(e.Slot))
                    slots[e.Slot]++;

            items.Add(new ItemStat
            {
                ItemId = group.Key,
                Name = def?.Name ?? group.Key,
                Group = def?.Group ?? "其他",
                Route = def?.Route ?? "oral",
                Days = ds.Count,
                Count = group.Count(),
                First = ds[0],
                Last = ds[^1],
                PerDay = ds.Count > 0 ? (double)group.Count() / ds.Count : 0,
                Slots = slots,
                Coverage = span > 0 ? (double)ds.Count / span : 0,
            });
        }

        items.Sort((a, b) => b.Count.CompareTo(a.Count));
        r.Items = items;

        // 断档：相邻「有记录日」间隔 > 1 天
        var gaps = new List<Gap>();
        for (var i = 1; i < dates.Count; i++)
        {
            var g = DayDiff(dates[i - 1], dates[i]);
            if (g > 1)
                gaps.Add(new Gap { From = dates[i - 1], To = dates[i], Days = g - 1 });
        }

        gaps.Sort((a, b) => b.Days.CompareTo(a.Days));
        r.Gaps = gaps;

        r.Total = evs.Count;
        r.DayCount = dates.Count;
        r.First = first;
        r.Last = last;
        return r;
    }

    /// <summary>
    /// 近期常用品类：按最近 N 天内、同时段的出现频次排序。
    /// 回答"此刻我要吃什么"。
    /// </summary>
    public static List<string> RecentItems(Db db, int withinDays = 14, string? slot = null, int limit = 8)
    {
        var orals = Items.All.Where(i => i.Route == "oral").Select(i => i.Id).ToList();

        if (db.Events.Count == 0)
            return orals.Take(limit).ToList();

        var maxDate = db.Events.Max(e => e.When.Date)!;
        var cutoff = Iso(DateOf(maxDate).AddDays(-withinDays));

        var freq = new Dictionary<string, int>();
        foreach (var e in db.Events)
        {
            if (string.CompareOrdinal(e.When.Date, cutoff) < 0) continue;
            if (slot is not null && e.Slot != slot) continue;
            freq[e.ItemId] = freq.TryGetValue(e.ItemId, out var c) ? c + 1 : 1;
        }

        var ranked = freq
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        if (ranked.Count >= limit) return ranked.Take(limit).ToList();

        var seen = ranked.ToHashSet();
        foreach (var id in orals)
        {
            if (ranked.Count >= limit) break;
            if (seen.Add(id)) ranked.Add(id);
        }

        return ranked.Take(limit).ToList();
    }
}