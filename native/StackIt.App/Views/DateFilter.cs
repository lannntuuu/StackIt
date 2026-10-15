using System.Windows;
using System.Windows.Controls;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 时间范围筛选。统计页和时间流共用同一份状态：
/// 用户在统计页选了「2026 年」，切到时间流应该还是 2026 年，反之亦然。
///
/// 为什么做筛选而不是分页：分页解决的是"一次加载太多"，而这个问题已经由
/// 列表虚拟化解决了（543 天全量布局要 9.4 秒，虚��化后 0.1 秒）。
/// 剩下的痛点是"找不到那段记录"，直接筛掉比一页页翻短得多。
///
/// 为什么不做"近 30 天"这类相对范围：用户是**补记**数据的，
/// 库里最新一条未必是今天（实测今天 2026-10-12，数据止于 2026-09-07），
/// 按墙上时钟算出来的"近 30 天"会一条都筛不到，等于给用户一个死按钮。
/// 年 / 月的绝对范围没有这个坑。
/// </summary>
public static class DateFilter
{
    /// <summary>取值：all | y:2026 | m:2026-07 | w:2026-W28</summary>
    public static string Value { get; private set; } = "all";

    /// <summary>筛选有变化时触发，宿主用它重画。</summary>
    public static event Action? Changed;

    public static bool IsAll => Value == "all";

    /// <summary>是否选到了某一周（周视图要用它决定标题）。</summary>
    public static bool IsWeek => Value.StartsWith("w:", StringComparison.Ordinal);

    /// <summary>选中的年份；未选年份（全部）时为 null。</summary>
    public static string? SelectedYear
    {
        get
        {
            if (Value.Length < 6 || (Value[0] is not ('y' or 'm' or 'w'))) return null;
            return Value.Substring(2, 4);
        }
    }

    /// <summary>
    /// 选中周的周一（YYYY-MM-DD）；未选周时为 null。
    /// 值里存的就是周一日期本身（"w:2026-07-13"），不另外编码周序号 ——
    /// 存日期省一次解析，也不会和 ISO 周在跨年时对不上。
    /// </summary>
    public static string? SelectedWeekMonday =>
        IsWeek && DateTime.TryParseExact(Value.Substring(2), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
            ? Stats.Iso(d)
            : null;

    /// <summary>选中周的 ISO 编号（如 28），未选周时为 0。</summary>
    public static int SelectedWeekNumber
    {
        get
        {
            var mon = SelectedWeekMonday;
            return mon == null ? 0 : IsoWeekNumber(mon);
        }
    }

    public static void Set(string f)
    {
        if (f == Value) return;
        Value = f;
        Changed?.Invoke();
    }

    /// <summary>筛选有变化时触发，宿主用它重画。</summary>
    public static void Toggle(string f) => Set(f);

    /// <summary>这个日期是否落在筛选范围内。</summary>
    public static bool Has(string date)
    {
        switch (Value)
        {
            case "all":
                return true;
            // 日期是定长零填充的，"2026" / "2026-07" 前缀匹配即年 / 月匹配
            case var v when v.StartsWith("y:", StringComparison.Ordinal)
                      || v.StartsWith("m:", StringComparison.Ordinal):
                return date.StartsWith(v.Substring(2), StringComparison.Ordinal);
            case var v when v.StartsWith("w:", StringComparison.Ordinal):
                {
                    var mon = SelectedWeekMonday;
                    // 非法值（比如手输的、或者旧版本残留）不能把数据全滤没。
                    // 判不出周就当"不过滤"：宁可多显示，也不要静默清空。
                    if (mon == null) return true;
                    return Stats.Iso(Stats.DateOf(HistoryCalc.MondayOf(date))) == mon;
                }
            default:
                return true;
        }
    }

    /// <summary>
    /// 筛出一个只含范围内事件的库，给统计数字（次数、天数、时段、星期）用。
    ///
    /// 只用于**聚合数字**。格视图不能用它 —— 它依赖每个品类
    /// 的可记窗口，窗口必须从全量数据推，见 HistoryCalc.BuildRange。
    /// </summary>
    public static StackIt.Core.Db Apply(StackIt.Core.Db db)
    {
        if (IsAll) return db;

        return new StackIt.Core.Db
        {
            Version = db.Version,
            Events = db.Events.Where(e => Has(e.When.Date)).ToList(),
            Observations = db.Observations.Where(o => Has(o.When.Date)).ToList(),
        };
    }

    /// <summary>范围的人话描述，用在空态和说明文字里。</summary>
    public static string Label() => Value switch
    {
        "all" => "全部时间",
        _ when Value.StartsWith("y:", StringComparison.Ordinal) => Value.Substring(2) + " 年",
        _ when Value.StartsWith("m:", StringComparison.Ordinal) => Value.Substring(2),
        _ when IsWeek => SelectedWeekMonday is { } mon
            ? $"{mon} 起那一周（第 {SelectedWeekNumber} 周）"
            : "那一周",
        _ => "全部时间",
    };

    /// <summary>
    /// 筛选条 UI —— **统计页与时间流共用这一份渲染器**。
    ///（上一批在 TimelinePage 里另画了一份 FilterRail / Chip / ChipRow / IsoWeek，
    /// 起因是这里的芯片用写死的画刷实例，套不上样式键。那份重复实现已删，
    /// 这里改成颜色一律走样式键 / DynamicResource，根因没了，两份也就没必要存在。）
    ///
    /// 460 宽度放不下 12 个「月」按钮，换行用 WrapPanel。
    /// 只列出**库里真有数据**的年月，且带条数：空的不该占按钮，
    /// 条数则让人不用点进去就知道那段时间记了多少。
    ///
    /// 点击一律走 <see cref="Toggle"/>：MainWindow 订阅了 <see cref="Changed"/>，
    /// 无论在哪儿点都会重画当前页，所以宿主不用再插自己的回调。
    /// </summary>
    public static UIElement Bar(IEnumerable<string> allDates)
    {
        var box = new StackPanel();
        var dates = allDates.ToList();

        void Add(UIElement e) => box.Children.Add(e);

        static Button Chip(string text, bool on, int count, string value)
        {
            var b = new Button
            {
                Content = count > 0 ? $"{text} {count}" : text,
                Margin = new Thickness(0, 0, 5, 5),
            };
            // 代码只说「是哪一颗」：底色 / 字色 / 描边 / 圆角 / hover 全在样式里。
            // 在这里写本地画刷会盖掉样式里的 IsMouseOver 触发器，而且取到的
            // 画刷**实例**从此不跟着配色换（ThemeRuntime 是整组替换资源的）。
            Interactions.ApplyStyle(b, on ? Interactions.ChipOn : Interactions.Chip);
            b.Click += (_, _) => Toggle(value);
            return b;
        }

        static WrapPanel Row() => new() { Margin = new Thickness(0, 0, 0, 4) };

        // —— 年份 ——
        var years = dates.Select(d => d.Substring(0, 4))
                         .Distinct()
                         .OrderByDescending(y => y, StringComparer.Ordinal)
                         .ToList();

        // 只有一年就不给年份行，多一行反而占地方
        if (years.Count > 1)
        {
            var wrap = Row();
            wrap.Children.Add(Chip("全部", IsAll, dates.Count, "all"));
            foreach (var y in years)
            {
                var yy = y;
                var n = dates.Count(d => d.StartsWith(yy, StringComparison.Ordinal));
                wrap.Children.Add(Chip(yy, Value == "y:" + yy, n, "y:" + yy));
            }
            Add(wrap);
        }

        // —— 月份：选中某一年时出现 ——
        // 只有一年时也要给月份，否则年份行被藏掉，月份就永远点不出来。
        var year = SelectedYear ?? (years.Count == 1 ? years[0] : null);
        if (year is not null)
        {
            var wrap = Row();
            var months = dates.Where(d => d.StartsWith(year, StringComparison.Ordinal))
                              .Select(d => d.Substring(0, 7))
                              .Distinct()
                              .OrderByDescending(m => m, StringComparer.Ordinal)
                              .ToList();
            foreach (var m in months)
            {
                var mm = m;
                var n = dates.Count(d => d.StartsWith(mm, StringComparison.Ordinal));
                // 显示 "7月" 而不是 "2026-07"：年份已在上面的行里选定，再重复是噪音
                wrap.Children.Add(Chip(mm.Substring(5).TrimStart('0') + "月",
                                       Value == "m:" + mm, n, "m:" + mm));
            }
            Add(wrap);
        }

        // —— 周：选中某月时出现 ——
        // 为什么按周筛：月的分辨率只有 4~5 周，选到月之后一格只剩几列，
        // 看不出「哪一周断了」。而这份数据的原生刻度就是周（每格一周），
        // 按周筛才和它对齐。
        if (DateFilterMonth() is { } mk)
        {
            var wrap = Row();
            var weeks = dates.Where(d => d.StartsWith(mk, StringComparison.Ordinal))
                              .Select(d => HistoryCalc.MondayOf(d))
                              .Distinct()
                              .OrderBy(x => x, StringComparer.Ordinal)
                              .ToList();
            foreach (var wk in weeks)
            {
                var num = IsoWeekNumber(wk);
                var n = dates.Count(d => HistoryCalc.MondayOf(d) == wk);
                wrap.Children.Add(Chip($"{num}周", Value == "w:" + wk, n, "w:" + wk));
            }
            if (weeks.Count > 0) Add(wrap);
        }

        // 筛选中时总给一行说明。这不只是装饰：用户在别处补记了一条，
        // 回到本页看不到，会以为没存上 —— 这行负责解释为什么。
        if (!IsAll)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var note = new TextBlock
            {
                Text = $"只看 {Label()} · {dates.Count(Has)} 条（全部 {dates.Count} 条）",
                FontSize = 12,
                Margin = new Thickness(2, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
            line.Children.Add(note);

            // 只在「没有年份行」时补这颗清除键：年份行里的「全部」芯片本来就是清除入口，
            // 库里只有一年时那一行被藏掉，没这颗键就出不来了（合流前时间流有，留着）。
            if (years.Count <= 1)
            {
                var clear = new Button
                {
                    Content = "清除筛选",
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Interactions.ApplyStyle(clear, Interactions.ButtonText);
                clear.Click += (_, _) => Set("all");
                line.Children.Add(clear);
            }
            Add(line);
        }

        return box;
    }

    /// <summary>当前选中月（"2026-07"）；未选月、或选的是年/全部时为 null。</summary>
    private static string? DateFilterMonth() =>
        Value.StartsWith("m:", StringComparison.Ordinal) ? Value.Substring(2) : null;

    /// <summary>某个周一属于当年第几周（ISO 周）。</summary>
    private static int IsoWeekNumber(string monday)
    {
        var d = Stats.DateOf(monday);
        var thu = d.AddDays(3);                      // ISO：以周四所在年为准
        var jan1 = new DateTime(thu.Year, 1, 1);
        var offset = ((int)jan1.DayOfWeek + 6) % 7;
        return ((thu - jan1).Days + offset) / 7 + 1;
    }

    /// <summary>
    /// 筛选逻辑自检。日期前缀匹配看着显然，但它是"少一条 / 多一条"那类
    /// 悄悄出错的逻辑：年 vs 月只差 3 个字符，写错会静默漏数据。
    /// 这里真的调 Has()，不在测试里重抄一遍判定 ——
    /// 重抄的测试只证明"我抄的这遍和我想的一样"，原函数改坏了它照样绿。
    /// </summary>
    public static int SelfTest()
    {
        var keep = Value;
        var fails = new List<string>();
        var checks = 0;

        void Check(string filter, string date, bool expected)
        {
            checks++;
            Value = filter;
            var actual = Has(date);
            if (actual != expected)
                fails.Add($"{filter} 对 {date}：期望 {expected}，实际 {actual}");
        }

        Check("all", "2026-07-15", true);
        Check("y:2026", "2026-07-15", true);
        Check("y:2026", "2025-07-15", false);
        Check("m:2026-07", "2026-07-15", true);
        Check("m:2026-07", "2026-08-15", false);
        // 边界：7 月不能把其他月份的"7"字头也吃进来
        Check("m:2026-07", "2026-07-01", true);
        Check("m:2026-07", "2026-11-15", false);
        // 年份不许跨年误配
        Check("y:2026", "2026-01-01", true);
        Check("m:2026-12", "2025-12-31", false);

        // —— 按周 ——
        // 周按周一归属。2026-07-13 是周一，那一周应含 13~19 日。
        Check("w:2026-07-13", "2026-07-13", true);
        Check("w:2026-07-13", "2026-07-15", true);
        Check("w:2026-07-13", "2026-07-19", true);
        Check("w:2026-07-13", "2026-07-12", false);   // 周日属于上一周
        Check("w:2026-07-13", "2026-07-20", false);
        // 跨月：周的周一是 2026-08-03，8-02 是周日属于上一周
        Check("w:2026-08-03", "2026-08-03", true);
        Check("w:2026-08-03", "2026-08-09", true);
        Check("w:2026-08-03", "2026-08-02", false);
        Check("w:2026-08-03", "2026-08-10", false);
        // 周可以横跨两个月份：2026-07-27 那周含 8-02
        Check("w:2026-07-27", "2026-08-02", true);
        // 跨年：上一年 12 月最后一个周一不属于下一年的第一周
        Check("w:2027-01-04", "2027-01-04", true);
        Check("w:2027-01-04", "2027-01-10", true);
        Check("w:2027-01-04", "2026-12-28", false);   // 12-28 是周一，属它自己那周
        Check("w:2027-01-04", "2027-01-11", false);
        // 非法值不该把数据全滤掉
        Check("w:不是日期", "2026-07-15", true);

        Value = keep;

        foreach (var f in fails) Console.WriteLine("  FAIL " + f);
        Console.WriteLine(fails.Count == 0
            ? $"筛选自检通过（{checks} 项）"
            : $"筛选自检失败 {fails.Count} 项");
        return fails.Count == 0 ? 0 : 1;
    }
}