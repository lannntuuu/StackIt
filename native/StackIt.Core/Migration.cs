using System.Text.Json;

namespace StackIt.Core;

/// <summary>旧版 JSON 兼容：缺失品类配置时补一份独立的默认字典。</summary>
public static class Migration
{
    public const string Notice = "已自动补全品类信息，请检查";

    public static bool CompleteCategories(Db db)
    {
        if (db.Items is { Count: > 0 }) return false;

        // 优先嵌入当前 items.json，未创建过配置时才使用内置默认字典。
        var configured = ItemStore.Load();
        if (configured.Count > 0)
        {
            db.Items = configured;
        }
        else
        {
            // 深拷贝，避免迁移结果与全局默认字典共享可变别名列表。
            db.Items = Items.All.Select(item => JsonSerializer.Deserialize<Item>(
                JsonSerializer.Serialize(item))!).ToList();
        }
        return true;
    }
}
