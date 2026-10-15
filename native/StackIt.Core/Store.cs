using System.Text;
using System.Text.Json;

namespace StackIt.Core;

/// <summary>
/// 数据存取。取代网页版的 localStorage。
///
/// 为什么这是原生版最大的实际收益：数据落在
/// %APPDATA%\StackIt\db.json，是你自己控制的普通文件 ——
/// 不会被系统清理、不会被浏览器隔离、能被别的工具读取、可以随手备份。
///
/// 写入策略：先写临时文件再替换，避免写到一半断电导致数据损坏。
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 数据目录。解析一次就缓存。
    ///
    /// 两条硬性质：
    /// ① 不能抛。DataView 构造、配色保存、每次落盘都要摸这个属性，
    ///    而 Lazy 默认模式会把工厂的异常**永久缓存**，一个坏路径就能让
    ///    整个应用反复卡死。
    /// ② 不能只有一条路。%APPDATA% 可能因企业策略、受控文件夹访问、
    ///    重定向配置等原因写不了。写不了就依次退到别的可写位置，
    ///    而不是让应用变成只能看不能用。
    /// </summary>
    private static readonly Lazy<string> _dataDir = new(ResolveDir, isThreadSafe: true);

    public static string DataDir => _dataDir.Value;

    /// <summary>所有候选位置都写不了时的原因，界面上要告诉用户。</summary>
    public static string? DataDirProblem { get; private set; }

    /// <summary>实际用了哪个候选位置（诊断用）。</summary>
    public static string? DataDirChoice { get; private set; }

    /// <summary>按优先级列出候选数据目录。用户设了环境变量就只用那一个。</summary>
    private static IEnumerable<(string Path, string Why)> Candidates()
    {
        var custom = Environment.GetEnvironmentVariable("STACKIT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            yield return (custom, "环境变量 STACKIT_DATA_DIR");
            yield break;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            yield return (Path.Combine(appData, "StackIt"), "%APPDATA%");

        // 便携模式：放在 exe 旁边。绿色软件常见做法，也便于整包备份。
        yield return (Path.Combine(AppContext.BaseDirectory, "data"), "程序所在目录");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return (Path.Combine(localAppData, "StackIt"), "%LOCALAPPDATA%");

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs))
            yield return (Path.Combine(docs, "StackIt"), "文档目录");

        var temp = Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(temp))
            yield return (Path.Combine(temp, "StackIt"), "临时目录");
    }

    private static string ResolveDir()
    {
        var failures = new List<string>();

        foreach (var (dir, why) in Candidates())
        {
            // CreateDirectory 对已存在的目录不抛，正好用来探可写性。
            // 但"目录在"不等于"能写"，所以再真写一个文件确认一次 ——
            // 有些位置目录能建、文件写不进去。
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                DataDirChoice = why;
                DataDirProblem = null;
                return dir;
            }
            catch (Exception ex)
            {
                failures.Add($"· {why}：{dir}\n  {ex.Message}");
            }
        }

        // 全军覆没。返回第一个候选（写文件会各自失败并记录），
        // 但把每个位置失败的原因都留给界面显示，用户才知道该改什么。
        DataDirChoice = null;
        DataDirProblem = string.Join("\n", failures);
        var first = Candidates().First();
        return first.Path;
    }

    public static string DbPath => Path.Combine(DataDir, "db.json");
    public static string PrefsPath => Path.Combine(DataDir, "prefs.json");
    public static string BackupDir => Path.Combine(DataDir, "backups");

    /// <summary>自动备份最多留几份。</summary>
    public const int BackupKeep = 10;

    /// <summary>坏文件备份（db.json.broken-*）最多留几份。</summary>
    public const int BrokenKeep = 5;

    /// <summary>
    /// 落盘失败。消息是给用户看的，所以直接写中文、说清是哪一次写入出的问题。
    /// 不用 IOException 的英文消息：那是给开发者看的，用户只知道"没存上"。
    /// </summary>
    public sealed class SaveFailedException : Exception
    {
        public SaveFailedException(string message, Exception inner) : base(message, inner) { }
    }

    public static Db Load()
    {
        try
        {
            if (!File.Exists(DbPath)) return new Db();
            var json = File.ReadAllText(DbPath, Encoding.UTF8);
            var db = JsonSerializer.Deserialize<Db>(json, Opts);
            if (db is null) return new Db();
            db.Events ??= new List<IntakeEvent>();
            db.Observations ??= new List<Observation>();
            return db;
        }
        catch (Exception ex)
        {
            // 数据文件坏了不能静默清空——那等于删掉用户的真实记录。
            // 备份一份再返回空库，让用户能在备份里找回。
            try
            {
                var bak = DbPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(DbPath, bak, true);
                Console.Error.WriteLine($"数据文件无法解析，已备份到：{bak}");
                Console.Error.WriteLine(ex.Message);
                // 顺手清掉历史遗留：坏文件每次损坏都会留一份，不及时清理会越攒越多。
                // 注意必须在「本次备份已成功」之后才清，避免把刚留底的那份也卷走。
                PruneBroken();
            }
            catch
            {
                Console.Error.WriteLine("数据文件无法解析且备份失败：" + ex.Message);
            }

            return new Db();
        }
    }

    /// <summary>清理坏文件备份：DataDir 下 db.json.broken-* 只留最近 BrokenKeep 份。</summary>
    /// 为什么存在：Load 解析失败时把坏文件备份成 db.json.broken-<时间戳> 放在 DataDir 根，
    /// 文件坏一次留一份，长期会攒下越来越多的几十 KB 文件，干扰用户在根目录里找 db.json。
    ///
    /// 硬约束：删除失败绝不能抛异常。Load 本身已经在 catch 里了，这里再抛就等于把
    /// 「能读出空库让用户找回」变成崩溃——绝对不允许。所以整段包 try/catch，失败只写一行。
    private static void PruneBroken(int keep = BrokenKeep)
    {
        try
        {
            // 文件名是 yyyyMMdd-HHmmss 时间戳，字典序==时间序。
            // 升序排列后，前面的就是更旧的，超出 keep 的部分删掉最旧的即可。
            var files = new DirectoryInfo(DataDir)
                .GetFiles("db.json.broken-*")
                .OrderBy(f => f.Name)
                .ToArray();
            for (var i = 0; i < files.Length - keep; i++)
                files[i].Delete();
        }
        catch (Exception ex)
        {
            // 清理失败不影响主流程（用户照样能拿到空库找回），最多记一行
            Console.Error.WriteLine("清理坏文件备份失败（已忽略）：" + ex.Message);
        }
    }

    /// <summary>导入/恢复前的自动快照：把当前 db.json 复制一份带时间戳的备份。</summary>
    /// 只在 ReplaceAll（整库替换）时调用；普通增删改绝不调用，否则每记一笔就多一个文件。
    /// 容错：首次运行没有 db.json 直接返回；备份目录建不了或复制失败都不阻断导入，静默跳过。
    private static void Snapshot()
    {
        try
        {
            if (!File.Exists(DbPath)) return;
            Directory.CreateDirectory(BackupDir);
            // 时间戳只到秒。同一秒内两次导入（或快速连续两次 ReplaceAll）会生成
            // 同名文件互相覆盖，少留一份备份——而覆盖掉的恰恰是倒数第二份重要的底。
            // 所以在秒级时间戳后面再拼一个短随机后缀（Guid 前 8 位 hex），保证同秒不冲突。
            // 前缀时间戳不变 → 按文件名升序排序的语义（旧在前）依然正确；
            // 后缀是随机 hex 不影响排序先后，只是让同名不再撞车。
            // 文件名仍以 "db-" 开头，GetFiles("db-*.json") 的匹配模式原样生效。
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var dest = Path.Combine(BackupDir, "db-" + stamp + "-" + suffix + ".json");
            File.Copy(DbPath, dest, true);

            // 只保留最近 BackupKeep 份：按文件名（时间戳序）升序，超出部分删最旧的。
            var files = new DirectoryInfo(BackupDir)
                .GetFiles("db-*.json")
                .OrderBy(f => f.Name)
                .ToArray();
            for (var i = 0; i < files.Length - BackupKeep; i++)
                files[i].Delete();
        }
        catch (Exception ex)
        {
            // 快照失败绝不能阻断导入，最多记一行
            Console.Error.WriteLine("导入前快照失败（已忽略，不影响导入）：" + ex.Message);
        }
    }

    /// <summary>原子写：先写 .tmp，强制刷盘后再替换正式文件。</summary>
    public static void Save(Db db)
    {
        try
        {
            var json = JsonSerializer.Serialize(db, Opts);
            var tmp = DbPath + ".tmp";
            // FileShare.None：写入期间独占，避免被别的进程读到半截。
            //
            // 必须用显式块而不是 using var：using 的 Dispose 在整个方法
            // 作用域结束时才跑，那时 File.Move 早就执行过了，而文件仍被
            // 自己开着 —— Move 直接抛「另一个程序正在使用此文件」。
            // 这是个不崩但彻底不能用的坑，所以单独写清楚。
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
            {
                sw.Write(json);
                sw.Flush();
                // flushToDisk=true：连 OS/NVMe 写缓存一起刷掉，否则掉电仍可能丢这一笔
                fs.Flush(true);
            }
            // 到这里流已关闭，Move 才能换掉目标文件。
            // File.Move(overwrite) 在同卷内是原子的：要么旧文件，要么完整新文件，不会是半截
            File.Move(tmp, DbPath, true);
        }
        catch (Exception ex)
        {
            // 不吞：一路抛到界面层去弹窗。消息要能直接给用户看——
            // 说清是哪一笔没存上、可能原因、以及数据文件路径。
            throw new SaveFailedException(
                $"这一笔没能存上：{DbPath}\n\n可能原因：磁盘空间不足、目录没有写权限、或文件被其它程序占用。\n原始错误：{ex.Message}",
                ex);
        }
    }

    /* ---------- 增删改 ---------- */

    // 落盘前的"后悔药"：深拷贝一份当前内存状态。
    // 为什么用 JSON 往返而不是 MemberwiseClone：
    // MemberwiseClone 只做浅拷贝，新对象的 Events/Observations 字段仍指向
    // 与原对象同一份 List 实例。一旦我们用克隆体做"撤销"，改的是同一块内存，
    // 原件也跟着变，备份等于白做——甚至更糟，因为你会以为有后悔药其实没有。
    // 纯 POCO 经 JSON 序列化再反序列化，得到的是完全独立的一套对象图，
    // 撤销它绝不会动到原件，也最不容易漏字段。
    private static Db Clone(Db db)
    {
        var json = JsonSerializer.Serialize(db, Opts);
        var copy = JsonSerializer.Deserialize<Db>(json, Opts);
        // 来源就是合法 Db，反序列化不该为 null；但防御性补一下，
        // 免得后续 Restore 拿到 null 反而抛更怪的错，把真问题盖掉。
        copy ??= new Db();
        copy.Events ??= new List<IntakeEvent>();
        copy.Observations ??= new List<Observation>();
        return copy;
    }

    // 把 db 还原成 undo 的样子（就地改，不换引用）。
    // 必须就地改：调用方（MainWindow.Db）持有的是同一个 Db 引用。
    // 如果只在 Store 内部 `db = undo` 再 return，调用方手里的还是那个被改坏的旧引用，
    // 界面重画后用户会"看到记上了其实没存上"。所以要把 undo 的内容原样搬回 db 这个对象本身。
    // 整段不准抛：一旦抛了会盖掉原本要上抛的 SaveFailedException，反而把真问题藏起来；
    // 哪怕还原途中出意外，也静默吞掉，让外层继续把原始异常抛给界面。
    private static void Restore(Db db, Db undo)
    {
        try
        {
            // 顶层标量属性：Db 目前只有 Version 一个，逐个搬回，别漏也别编造不存在的。
            db.Version = undo.Version;

            // 列表：先清空再整段搬回 undo 的副本，保证数量、顺序、内容都一模一样。
            // 搬的是 undo 里的元素引用——它们来自 Clone，和 db 无任何共享，搬过来不会影响 undo。
            db.Events.Clear();
            if (undo.Events is not null)
                db.Events.AddRange(undo.Events);

            db.Observations.Clear();
            if (undo.Observations is not null)
                db.Observations.AddRange(undo.Observations);
        }
        catch
        {
            // 还原失败绝不能把原始异常吞掉或替换。直接静默丢，
            // 外层仍会把 SaveFailedException 原样抛给界面。
        }
    }

    public static Db AddEvent(Db db, IntakeEvent e)
    {
        // Save 失败抛 SaveFailedException 时，内存里已经多了这条、磁盘上没有。
        // 先拍一份快照，万一 Save 挂了就把内存回滚到"没加之前"，杜绝静默不一致。
        var undo = Clone(db);
        try
        {
            db.Events.Add(e);
            Save(db);
        }
        catch
        {
            // 内存回滚到改动前，再原样把 Save 异常抛出去让界面弹窗。
            Restore(db, undo);
            throw;
        }
        return db;
    }

    public static Db UpdateEvent(Db db, string id, string? itemId, When? when, int? dose, string? slot)
    {
        var i = db.Events.FindIndex(x => x.Id == id);
        if (i < 0) return db;
        var undo = Clone(db);
        try
        {
            var e = db.Events[i];
            if (itemId is not null) e.ItemId = itemId;
            if (when is not null) e.When = when;
            e.Dose = dose;
            e.Slot = slot;
            Save(db);
        }
        catch
        {
            Restore(db, undo);
            throw;
        }
        return db;
    }

    public static Db RemoveEvent(Db db, string id)
    {
        var undo = Clone(db);
        try
        {
            db.Events.RemoveAll(x => x.Id == id);
            Save(db);
        }
        catch
        {
            Restore(db, undo);
            throw;
        }
        return db;
    }

    public static Db AddObservation(Db db, Observation o)
    {
        var undo = Clone(db);
        try
        {
            db.Observations.Add(o);
            Save(db);
        }
        catch
        {
            Restore(db, undo);
            throw;
        }
        return db;
    }

    public static Db UpdateObservation(Db db, string id, string? kind, string? subject, string? text, bool clearText = false)
    {
        var o = db.Observations.Find(x => x.Id == id);
        if (o is null) return db;
        var undo = Clone(db);
        try
        {
            if (kind is not null) o.Kind = kind;
            if (subject is not null) o.Subject = subject;
            // text 是可选字段：显式传 null 表示「清空」，与「不改」要能区分开
            if (clearText) o.Text = null;
            else if (text is not null) o.Text = text;
            Save(db);
        }
        catch
        {
            Restore(db, undo);
            throw;
        }
        return db;
    }

    public static Db RemoveObservation(Db db, string id)
    {
        var undo = Clone(db);
        try
        {
            db.Observations.RemoveAll(x => x.Id == id);
            Save(db);
        }
        catch
        {
            Restore(db, undo);
            throw;
        }
        return db;
    }

    // 整库替换走快照：导入/恢复前先留底，万一导入错了还能找回旧数据。
    // 普通增删改不在此列（见 Snapshot 注释），所以这里单独包一层而不是在 Save 里加。
    //
    // 关于回滚：ReplaceAll 不需要就地回滚逻辑，理由如下——
    // 入参 next 是导入/恢复流程**新构造**的 Db 对象，并不是 MainWindow 当前持有的那份内存 Db。
    // 它只在 Save 时被读取序列化，不会去改 MainWindow 手里的旧 Db。
    // 一旦 Save 失败，异常一路抛到 ReplaceDb，被它 catch 后**不会执行** `Db = next`，
    // 于是界面里那份旧内存数据原封未动、用户看到的仍是导入前的正确内容，本就没有不一致可修。
    // 所以：快照失败不阻断导入（保持现状，静默跳过），Save 失败原样抛出（保持现状，交给界面弹窗）。
    public static void ReplaceAll(Db db)
    {
        Snapshot();
        Save(db);
    }

    /* ---------- 偏好（配色） ---------- */

    public static string LoadPalette()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return "amber";
            using var doc = JsonDocument.Parse(File.ReadAllText(PrefsPath, Encoding.UTF8));
            return doc.RootElement.TryGetProperty("palette", out var p) ? p.GetString() ?? "amber" : "amber";
        }
        catch
        {
            return "amber";
        }
    }

    public static void SavePalette(string id)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { palette = id }, Opts);
            var tmp = PrefsPath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, PrefsPath, true);
        }
        catch
        {
            // 偏好丢了只是回到默认配色，不该因此让应用崩
        }
    }

    /* ---------- 导入导出 ---------- */

    public static string ToJson(Db db) => JsonSerializer.Serialize(db, Opts);

    public static Db FromJson(string text)
    {
        var db = JsonSerializer.Deserialize<Db>(text, Opts);
        if (db is null) throw new InvalidDataException("不是有效的 StackIt 数据");
        db.Events ??= new List<IntakeEvent>();
        db.Observations ??= new List<Observation>();
        return db;
    }

    /// <summary>导出成 Markdown，格式对齐 记录.md 的表格习惯，便于人工查阅与再导入。</summary>
    public static string ToMarkdown(Db db)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 服用记录导出");
        sb.AppendLine();
        sb.AppendLine($"共 {db.Events.Count} 条记录，导出于 {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        var byDay = db.Events
            .GroupBy(e => e.When.Date)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal);

        foreach (var day in byDay)
        {
            var d = Stats.DateOf(day.Key);
            sb.AppendLine($"## {day.Key} 星期{"日一二三四五六"[d.DayOfWeek == DayOfWeek.Sunday ? 0 : (int)d.DayOfWeek]}");
            sb.AppendLine();
            sb.AppendLine("| 时段 | 品类 | 份数 | 时间 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in day.OrderBy(x => x.When.Time ?? "99:99"))
            {
                var dose = e.Dose is > 1 ? $"×{e.Dose}" : "";
                sb.AppendLine($"| {e.Slot ?? ""} | {Items.NameOf(e.ItemId)} | {dose} | {e.When.Time ?? ""} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}