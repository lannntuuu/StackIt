// StackIt.Core 的命令行入口。两种用法：
//
//   dotnet run -- <记录.md> <events.json> <report.json> [stats.json] [history.json]
//       对拍：拿 TS 版跑同一份文件的结果当标准答案，逐条比。
//       解析器与统计是从 TypeScript 1:1 移植的，"看起来对"不等于"对"。
//
//   dotnet run -- seed <记录.md>
//       把 记录.md 解析后直接写进 %APPDATA%\StackIt\db.json，
//       免去首次启动时在界面里手动选文件导入。

using StackIt.Core;

if (args.Length == 1 && args[0] == "migration-test")
{
    var configured = ItemStore.Load();
    var expectedCount = configured.Count > 0 ? configured.Count : Items.All.Length;
    var oldDb = Store.FromJson("""{"version":1,"events":[],"observations":[]}""", out var migrated);
    if (!migrated || oldDb.Items?.Count != expectedCount) return 1;

    var exported = Store.ToJson(oldDb);
    var restored = Store.FromJson(exported, out migrated);
    if (migrated || restored.Items?.Count != expectedCount) return 1;

    oldDb.Items![0].Aliases[0] = "changed";
    if (configured.Count == 0 && Items.All[0].Aliases[0] == "changed") return 1;

    Console.WriteLine("PASS 旧数据补全、完整品类导出、新版导入及默认字典隔离");
    return 0;
}

if (args.Length >= 1 && args[0] == "verify-items")
{
    var source = args.Length > 1 ? args[1] : ItemStore.ItemsPath;
    var loaded = ItemStore.Load(source);
    if (loaded.Count != Items.All.Length) throw new Exception("默认品类数量不一致");
    if (loaded.Select(i => i.Id).Except(Items.All.Select(i => i.Id)).Any())
        throw new Exception("默认品类 ID 不一致");
    if (ItemStore.GenerateId("锌", loaded) != "xin") throw new Exception("拼音 ID 生成失败");
    if (ItemStore.GenerateId("Fish Oil", loaded) != "fish-oil") throw new Exception("英文 ID 生成失败");
    if (ItemStore.GenerateId("锌", new[] { "xin", "xin2" }) != "xin3") throw new Exception("ID 冲突序号失败");
    if (ItemStore.GenerateId("未知名称", Array.Empty<Item>()) != "item") throw new Exception("ID 回退失败");
    var duplicate = new Item { Id = "zinc", Name = "锌", Route = "oral", Group = "矿物质", DoseUnit = "片" };
    duplicate.Aliases.Add("锌");
    if (ItemStore.Validate(loaded.Append(duplicate)).Count < 2) throw new Exception("重复校验失败");
    var tmp = source + ".smoke.json";
    ItemStore.Save(tmp, loaded);
    if (ItemStore.Load(tmp).Count != loaded.Count) throw new Exception("读写往返失败");
    File.Delete(tmp);
    Console.WriteLine($"PASS 品类配置：{loaded.Count} 项，默认迁移、校验、ID 生成及读写往返均通过");
    return 0;
}

if (args.Length >= 1 && args[0] == "seed")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("用法: dotnet run -- seed <记录.md>");
        return 2;
    }

    var md = File.ReadAllText(args[1]);
    var result = Importer.ParseMarkdown(md);
    Store.Save(result.Db);

    Console.WriteLine($"已写入 {Store.DbPath}");
    Console.WriteLine($"  事件 {result.Report.Events} 条，覆盖 {result.Report.Weeks} 周");
    Console.WriteLine($"  数据区间 {result.Db.Events.Min(e => e.When.Date)} → {result.Db.Events.Max(e => e.When.Date)}");
    return 0;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("用法:");
    Console.Error.WriteLine("  dotnet run -- <记录.md> <baseline-events.json> <baseline-report.json> [stats.json] [history.json]");
    Console.Error.WriteLine("  dotnet run -- seed <记录.md>");
    return 2;
}

return Verify.Run(
    args[0], args[1], args[2],
    args.Length > 3 ? args[3] : null,
    args.Length > 4 ? args[4] : null);