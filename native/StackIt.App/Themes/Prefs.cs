using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using StackIt.Core;

namespace StackIt.App;

/// <summary>
/// 偏好读写 —— 目前只有一个键：<c>paper</c>（纸底）。
///
/// 为什么不复用 Core 那套：<see cref="Store.SavePalette"/> 写的是
/// <c>{ palette }</c> 整个对象，它会把文件**整体覆盖**。纸底是这一批新加的维度，
/// 而 Core/ 不在本批可改范围内，所以这里做「读回来 → 合并 → 原子写回」，
/// 保证两个键互不踩踏：
///   换配色：Store.SavePalette(id) 先覆盖，紧接着 MergeWrite 把 paper 补回去；
///   换纸底：只动 paper，palette 原样留着。
/// 认不出的值一律回浅色（默认必须是浅）。写失败不抛：偏好丢了只是回到默认主题，
/// 不该因此让应用崩 —— 跟 Core 里 SavePalette 的判断保持一致。
/// </summary>
internal static class Prefs
{
    public const string PaperKey = "paper";

    /// <summary>读纸底；没有 prefs.json / 没有这个键 / 读坏了都算浅色。</summary>
    public static string? LoadPaperId()
    {
        try
        {
            var path = Store.PrefsPath;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(
                File.ReadAllText(path, System.Text.Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(PaperKey, out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 paper 合并写回 prefs.json（保留 palette 以及任何别人写进去的键）。
    /// 与 Core 同一条落盘路子：先写 .tmp 再 Move，避免半个文件被读走。
    /// </summary>
    public static void SavePaper(PaperKind kind) =>
        MergeWrite(PaperKey, PaperTheme.Get(kind).PrefId);

    private static void MergeWrite(string key, string value)
    {
        try
        {
            var path = Store.PrefsPath;
            JsonObject root;
            if (File.Exists(path))
            {
                var parsed = JsonNode.Parse(
                    File.ReadAllText(path, System.Text.Encoding.UTF8)) as JsonObject;
                root = parsed ?? new JsonObject();
            }
            else root = new JsonObject();

            root[key] = value;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }),
                System.Text.Encoding.UTF8);
            File.Move(tmp, path, true);
        }
        catch
        {
            // 目录只读 / 被占着：偏好这次没记住而已，不该影响正在显示的界面。
        }
    }
}
