namespace StackIt.Core;

/// <summary>口服 | 外用</summary>
public sealed class Item
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string[] Aliases { get; init; }
    public required string Route { get; init; }
    public required string Group { get; init; }
    public required string Unit { get; init; }
}

/// <summary>
/// 品类清单 —— 来自 记录.md 实测的 token 闭集。
/// 逐条对照 src/lib/items.ts 移植，改动前先确认两边一致。
///
/// 归并依据（数据证明，勿随意合并）：
///  - 「复合」81 次中 74 次与 B2/B6 同格 → 是与它们并列的独立品，故全名「复合维生素B」
///  - 「复合钙」与 D3 同格，属钙类，不可并入 B 族
///  - 「复合维生素」与鱼油/PS 同格，属多维
/// </summary>
public static class Items
{
    public static readonly Item[] All =
    {
        new Item { Id = "vitC", Name = "维生素C", Aliases = new[] { "VC", "vitC", "维C" }, Route = "oral", Group = "维C", Unit = "片" },
        new Item { Id = "b1", Name = "维生素B1", Aliases = new[] { "B1", "b1" }, Route = "oral", Group = "B族", Unit = "片" },
        new Item { Id = "b2", Name = "维生素B2", Aliases = new[] { "B2", "b2" }, Route = "oral", Group = "B族", Unit = "片" },
        new Item { Id = "b6", Name = "维生素B6", Aliases = new[] { "B6", "b6" }, Route = "oral", Group = "B族", Unit = "片" },
        new Item
        {
            Id = "bComplex", Name = "复合维生素B",
            Aliases = new[] { "复合2", "复合", "复合维生素B", "B复合", "bComplex" },
            Route = "oral", Group = "B族", Unit = "片"
        },
        new Item { Id = "vitD3", Name = "维生素D3", Aliases = new[] { "D3", "d3", "维生素D3" }, Route = "oral", Group = "脂溶性", Unit = "片" },
        new Item { Id = "vitE", Name = "维生素E", Aliases = new[] { "E" }, Route = "oral", Group = "脂溶性", Unit = "片" },
        new Item { Id = "vitA", Name = "维生素A", Aliases = new[] { "A" }, Route = "oral", Group = "脂溶性", Unit = "片" },
        new Item { Id = "zinc", Name = "锌", Aliases = new[] { "锌" }, Route = "oral", Group = "矿物质", Unit = "片" },
        new Item { Id = "calciumComplex", Name = "复合钙", Aliases = new[] { "复合钙", "钙" }, Route = "oral", Group = "矿物质", Unit = "片" },
        new Item { Id = "calciumD3K2", Name = "钙D3K2", Aliases = new[] { "钙d3k2", "钙D3K2" }, Route = "oral", Group = "矿物质", Unit = "片" },
        new Item { Id = "magnesium", Name = "镁", Aliases = new[] { "镁" }, Route = "oral", Group = "矿物质", Unit = "片" },
        new Item { Id = "fishOil", Name = "鱼油", Aliases = new[] { "鱼油", "鱼油1" }, Route = "oral", Group = "脂肪酸", Unit = "粒" },
        new Item { Id = "multivitamin", Name = "复合维生素", Aliases = new[] { "复合维生素", "复合维生素2" }, Route = "oral", Group = "其他", Unit = "片" },
        new Item { Id = "ps", Name = "PS", Aliases = new[] { "ps", "PS" }, Route = "oral", Group = "其他", Unit = "片" },
        new Item { Id = "minoxidil", Name = "米诺地尔", Aliases = new[] { "米诺地尔" }, Route = "topical", Group = "外用", Unit = "次" },
        new Item { Id = "selenium", Name = "二硫化硒", Aliases = new[] { "二硫化硒" }, Route = "topical", Group = "外用", Unit = "次" },
    };

    private static readonly Dictionary<string, Item> ById =
        All.ToDictionary(i => i.Id, StringComparer.Ordinal);

    /// <summary>
    /// 匹配表：按 alias 长度降序，保证「复合维生素」优先于「复合」被匹配。
    /// 单字母 E / A 必须全词精确匹配，禁止 contains（否则 '鱼油1' 之类会被误判）。
    /// </summary>
    private static readonly (string Alias, Item Item)[] Lookup = All
        .SelectMany(it => it.Aliases.Select(a => (Alias: a, Item: it)))
        .OrderByDescending(x => x.Alias.Length)
        .ToArray();

    public static Item? Match(string token)
    {
        var t = token.Trim();
        foreach (var (alias, item) in Lookup)
            if (t == alias)
                return item;
        return null;
    }

    public static Item? Get(string id) => ById.TryGetValue(id, out var it) ? it : null;

    public static string NameOf(string id) => Get(id)?.Name ?? id;

    /// <summary>
    /// 按关键字筛品类。匹配品名与全部别名，所以「D3」「复合」「vc」都能搜到。
    /// 空关键字 = 不筛。
    ///
    /// 为什么搜别名而不是只搜品名：清单是照抄真实记录里的写法定的，
    /// 用户记得住的是自己写过的那个词（"VC"、"鱼油1"），
    /// 而不是界面上那个规范名。只搜规范名等于逼用户先知道内部叫法。
    /// </summary>
    public static List<Item> Search(string keyword, string? route = null)
    {
        IEnumerable<Item> src = All;
        if (route is not null) src = src.Where(i => i.Route == route);

        var k = keyword.Trim();
        if (k.Length == 0) return src.ToList();

        return src
            .Where(i => i.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
                     || i.Aliases.Any(a => a.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}