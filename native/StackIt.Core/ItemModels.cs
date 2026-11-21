using System.Text.Json.Serialization;

namespace StackIt.Core;

public sealed class Item
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
    /// <summary>oral | topical</summary>
    [JsonPropertyName("route")] public string Route { get; set; } = "";
    /// <summary>B族 | 维C | 脂溶性 | 矿物质 | 脂肪酸 | 外用 | 其他</summary>
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("defaultDose")] public double? DefaultDose { get; set; }
    [JsonPropertyName("doseUnit")] public string DoseUnit { get; set; } = "";
    [JsonPropertyName("activeWindows")] public List<ActiveWindow> ActiveWindows { get; set; } = new();
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }

    /// <summary>兼容现有界面代码；JSON 统一使用 doseUnit。</summary>
    [JsonIgnore]
    public string Unit
    {
        get => DoseUnit;
        set => DoseUnit = value;
    }
}

public sealed class ActiveWindow
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}
