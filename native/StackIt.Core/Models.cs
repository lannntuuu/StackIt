using System.Text.Json.Serialization;

namespace StackIt.Core;

/// <summary>
/// 数据模型。对应 src/lib/types.ts 的 Db / IntakeEvent / Observation。
/// JSON 字段名与 TypeScript 版保持一致，这样导出的 JSON 两边能互换。
/// </summary>
public sealed class Db
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("events")] public List<IntakeEvent> Events { get; set; } = new();
    [JsonPropertyName("observations")] public List<Observation> Observations { get; set; } = new();
}

public sealed class When
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("time")] public string? Time { get; set; }
    /// <summary>minute | day</summary>
    [JsonPropertyName("precision")] public string Precision { get; set; } = "minute";
}

/// <summary>服用事件。Slot 为 null 表示原表未标注时段。</summary>
public sealed class IntakeEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("when")] public When When { get; set; } = new();
    [JsonPropertyName("dose")] public int? Dose { get; set; }
    /// <summary>早 | 中 | 晚 | null</summary>
    [JsonPropertyName("slot")] public string? Slot { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("source")] public Source? Source { get; set; }
}

/// <summary>导入溯源。保住"这条记录是从原文哪一格来的"，方便日后核对。</summary>
public sealed class Source
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("weekStart")] public string WeekStart { get; set; } = "";
    [JsonPropertyName("colHeader")] public string ColHeader { get; set; } = "";
    [JsonPropertyName("raw")] public string Raw { get; set; } = "";
}

public sealed class Observation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>symptom | benefit | sideEffect | note</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "note";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("severity")] public int? Severity { get; set; }
    [JsonPropertyName("when")] public When When { get; set; } = new();
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}