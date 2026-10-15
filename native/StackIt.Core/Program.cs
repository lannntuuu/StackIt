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