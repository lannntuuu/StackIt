using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StackIt.Core;

/// <summary>品类配置的读写、校验与 ID 生成。</summary>
public static class ItemStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal) { "oral", "topical" };
    private static readonly HashSet<string> Groups = new(StringComparer.Ordinal)
        { "B族", "维C", "脂溶性", "矿物质", "脂肪酸", "外用", "其他" };

    public static string ItemsPath => Path.Combine(Store.DataDir, "items.json");

    public static List<Item> Load() => Load(ItemsPath);

    /// <summary>
    /// 首次运行时把内置品类字典落盘成 items.json。
    ///
    /// 历史问题：items.json 只在开发目录 .dch/data 里生成过，从未写进真实数据目录
    /// （%APPDATA%\StackIt），导致品类管理页读到空列表、看不到任何老品类。
    /// 解析器/统计虽有 Items.All 回退所以数据不受影响，但管理界面是空的。
    /// 这里在文件缺失时自动播种，保证用户第一次打开就有 17 个内置品类可管理。
    /// 深拷贝避免与全局 Items.All 共享可变的别名/窗口列表。
    /// </summary>
    public static List<Item> EnsureSeeded()
    {
        if (File.Exists(ItemsPath)) return Load(ItemsPath);
        var seed = Items.All
            .Select(i => JsonSerializer.Deserialize<Item>(JsonSerializer.Serialize(i, Opts), Opts)!)
            .ToList();
        try { Save(seed); }
        catch { /* 写盘失败不阻断：退回内存默认，下次启动再试 */ }
        return seed;
    }

    public static List<Item> Load(string path)
    {
        // 仅默认路径缺失时自动播种；显式传入的自定义路径（如自检的临时文件）保持原语义。
        if (!File.Exists(path))
            return path == ItemsPath ? EnsureSeeded() : new List<Item>();
        var json = File.ReadAllText(path, Encoding.UTF8);
        var items = JsonSerializer.Deserialize<List<Item>>(json, Opts)
            ?? throw new InvalidDataException("品类配置必须是 JSON 数组");
        foreach (var item in items)
        {
            item.Aliases ??= new List<string>();
            item.ActiveWindows ??= new List<ActiveWindow>();
        }
        Validate(items);
        return items;
    }

    public static void Save(IEnumerable<Item> items) => Save(ItemsPath, items);

    public static void Save(string path, IEnumerable<Item> items)
    {
        var list = items.ToList();
        Validate(list);
        var json = JsonSerializer.Serialize(list, Opts);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, Encoding.UTF8))
        {
            writer.Write(json);
            writer.Flush();
            fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    /// <summary>返回所有配置错误；ID 忽略大小写，别名保留大小写语义。</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<Item> items)
    {
        var list = items.ToList();
        var errors = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var label = string.IsNullOrWhiteSpace(item.Id) ? $"第 {i + 1} 项" : $"品类 {item.Id}";
            if (string.IsNullOrWhiteSpace(item.Id)) errors.Add($"{label}：id 不能为空");
            else if (!ids.TryAdd(item.Id.Trim(), i)) errors.Add($"{label}：id 重复");
            if (string.IsNullOrWhiteSpace(item.Name)) errors.Add($"{label}：name 不能为空");
            if (!Routes.Contains(item.Route)) errors.Add($"{label}：route 必须为 oral 或 topical");
            if (!Groups.Contains(item.Group)) errors.Add($"{label}：group 不是有效分组");
            if (string.IsNullOrWhiteSpace(item.DoseUnit)) errors.Add($"{label}：doseUnit 不能为空");
            if (item.Aliases is null || item.ActiveWindows is null)
                errors.Add($"{label}：aliases 和 activeWindows 不能为 null");

            if (item.Aliases is not null)
                foreach (var alias in item.Aliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        errors.Add($"{label}：别名不能为空");
                        continue;
                    }
                    var key = alias.Trim();
                    if (aliases.TryGetValue(key, out var other))
                        errors.Add($"{label}：别名“{key}”与品类 {list[other].Id} 重复");
                    else
                        aliases.Add(key, i);
                }
        }
        return errors;
    }

    public static void EnsureValid(IEnumerable<Item> items)
    {
        var errors = Validate(items);
        if (errors.Count > 0)
            throw new InvalidDataException("品类配置无效：\n" + string.Join("\n", errors));
    }

    /// <summary>优先取英文名；中文使用常见名称的拼音映射，无法映射时退回 item。</summary>
    public static string GenerateId(string name, IEnumerable<Item> existingItems)
        => GenerateId(name, existingItems.Select(i => i.Id));

    public static string GenerateId(string name, IEnumerable<string> existingIds)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空", nameof(name));
        var baseId = PinyinIds.GetValueOrDefault(name.Trim()) ?? Slugify(name);
        if (string.IsNullOrEmpty(baseId)) baseId = "item";

        var used = new HashSet<string>(existingIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var candidate = baseId;
        var suffix = 2;
        while (!used.Add(candidate)) candidate = baseId + suffix++;
        return candidate;
    }

    private static string Slugify(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        var pendingDash = false;
        foreach (var c in normalized)
        {
            if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                pendingDash = false;
            }
            else if (sb.Length > 0)
            {
                pendingDash = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    // 项目目前使用的中文品类；新增未收录名称时显式落入可编辑的 item/item2。
    private static readonly Dictionary<string, string> PinyinIds = new(StringComparer.Ordinal)
    {
        ["维生素C"] = "wei-sheng-su-c",
        ["维生素B1"] = "wei-sheng-su-b1",
        ["维生素B2"] = "wei-sheng-su-b2",
        ["维生素B6"] = "wei-sheng-su-b6",
        ["复合维生素B"] = "fu-he-wei-sheng-su-b",
        ["维生素D3"] = "wei-sheng-su-d3",
        ["维生素E"] = "wei-sheng-su-e",
        ["维生素A"] = "wei-sheng-su-a",
        ["锌"] = "xin",
        ["复合钙"] = "fu-he-gai",
        ["钙D3K2"] = "gai-d3k2",
        ["镁"] = "mei",
        ["鱼油"] = "yu-you",
        ["复合维生素"] = "fu-he-wei-sheng-su",
        ["米诺地尔"] = "mi-nuo-di-er",
        ["二硫化硒"] = "er-liu-hua-xi",
    };
}
