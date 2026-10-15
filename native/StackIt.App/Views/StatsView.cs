using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // UniformGrid
using System.Windows.Media;                 // Brush（旧的 Card 还在用实例取色）
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 统计页 = 一本台账。默认页。
///
/// 结构（照 design/UI重设计-效果图.html 第 ① 屏）：
///   抬头（eyebrow + 宋体大标题 + 等宽元信息 + 三格摘要）
///   → 范围筛选 → **台账**（图例 + 一格一品类 + 轴说明 + 低频折叠）
///   → 节律（3×7 热力矩阵）→ 记录较少的时间。
///
/// 第 2 批 A 改掉的三件事：
///   1. 「品类榜」和「每周用量」原来是两块各自排序的重复列表，同一批品类读两遍 ——
///      现在一行读完一个品类：名字、次数、天数、格带、次/周、覆盖、区间。
///   2. 时段 / 星期原来是两条 2px 高、0.35 透明度 Line 色的进度条（等于没画），
///      现在是 3×7 热力矩阵，格内写实际次数。
///   3. 深浅原来没有任何说明，现在图例常驻。
///
/// 数字全部取自已有的 <see cref="StatsResult"/>，没有另算一遍；格带宽度由容器
/// 实测量出来（见 <see cref="GridView"/>）。
/// </summary>
public sealed class StatsView : ScrollViewer
{
    public StatsView(MainWindow host)
    {
        // 聚合数字用筛过的库（次数 / 天数 / 时段 / 星期 / 空档都该只看所选范围）。
        var dbF = DateFilter.Apply(host.Db);
        var s = Stats.Compute(dbF);
        var sp = new StackPanel { Margin = new Thickness(20, 18, 20, 24) };
        Content = sp;

        if (s.Total == 0)
        {
            sp.Children.Add(Card(new TextBlock
            {
                Text = DateFilter.IsAll
                    ? "还没有记录。\n\n点下方「记一笔」开始，或到「数据」导入历史记录。"
                    : $"{DateFilter.Label()} 没有记录。换一个时间范围看看。",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                FontSize = 13,
            }.Dyn(Theme.Ink2, TextBlock.ForegroundProperty)));
            return;
        }

        // 两份数据各司其职：**窗口**（每个品类从哪周记到哪周）必须用全量算，
        // 聚合数字用筛过的库。先筛再 Build 会把所有品类的首末周塌缩成筛选那一段，
        // 台账就会声称「锌 2026 年 7 月才开始记」——而它其实记了 488 次。
        var hist = DateFilter.IsAll
            ? HistoryCalc.Build(host.Db)
            : HistoryCalc.BuildRange(host.Db, DateFilter.Has);

        // —— 抬头 ——
        sp.Children.Add(Head(s, hist));
        sp.Children.Add(Summary(s));

        // 筛选条紧跟摘要。先给时间跨度再给筛选，是为了让"我看的是哪一段"第一眼就清楚。
        sp.Children.Add(Spacer(16));
        sp.Children.Add(DateFilter.Bar(host.Db.Events.Select(e => e.When.Date)));

        // —— 台账（品类榜 + 每周用量合并后的唯一一块）——
        sp.Children.Add(new GridView(host.Db, DateFilter.IsAll ? null : DateFilter.Has, s, hist)
        {
            Margin = new Thickness(0, 16, 0, 0),
        });

        // —— 节律：星期 × 时段 ——
        sp.Children.Add(Spacer(26));
        sp.Children.Add(Sect("节律", "星期 × 时段", $"{s.Total:N0} 次"));
        sp.Children.Add(Rhythm(dbF, s));

        // —— 记录较少的时间 ——
        sp.Children.Add(Spacer(26));
        sp.Children.Add(Sect("记录较少的时间", "不叫断档，也不叫已停", $"{s.Gaps.Count} 段"));
        sp.Children.Add(GapsCard(s.Gaps));
    }

    /* ---------- 抬头 ---------- */

    private static UIElement Head(StatsResult s, History hist)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        sp.Children.Add(new TextBlock
        {
            Text = Eyebrow("STACKIT · 个人服用记录"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty));

        sp.Children.Add(new TextBlock
        {
            Text = "这 3 年我在坚持",
            FontFamily = M.Serif,
            FontSize = 27,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 5, 0, 0),
        }.Dyn(Theme.Ink, TextBlock.ForegroundProperty));

        // 元信息一行读完：区间、跨度、周数、当前筛选。全等宽。
        var spanDays = s.First is not null && s.Last is not null
            ? Math.Max(1, Stats.DayDiff(s.First, s.Last) + 1)
            : 1;
        var months = (int)(spanDays / 30.4375);
        var span = months >= 12 ? $"{months / 12} 年 {months % 12} 个月" : $"{months} 个月";

        sp.Children.Add(new TextBlock
        {
            Text = $"{s.First} → {s.Last} · {span} · {hist.Weeks.Count} 周"
                 + (DateFilter.IsAll ? "" : $" · 只看{DateFilter.Label()}"),
            FontFamily = M.Mono,
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty));

        return sp;
    }

    /// <summary>
    /// 三格摘要：次数 / 天数 / 品类。数字全从现有 Stats 结果取。
    /// 上下各一条 hairline 把它和抬头、正文分开 —— 这是一栏，不是一句副标题。
    /// </summary>
    private static UIElement Summary(StatsResult s)
    {
        var cover = s.First is not null && s.Last is not null
            ? (int)Math.Round((double)s.DayCount /
                Math.Max(1, Stats.DayDiff(s.First, s.Last) + 1) * 100)
            : 0;

        var grid = new UniformGrid { Columns = 3 };
        var cells = new[]
        {
            (s.Total.ToString("N0"), "次服用"),
            (s.DayCount.ToString("N0"), "天有记录"),
            (s.Items.Count.ToString("N0"), $"个品类 · 窗口覆盖 {cover}%"),
        };

        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new StackPanel
            {
                Margin = new Thickness(i == 0 ? 0 : 12, 11, 0, 12),
            };
            if (i > 0)
            {
                // 格与格之间一根竖 hairline。第一格不留，左沿就贴齐标题。
                cell.Children.Add(new Border
                {
                    Width = 1,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(-12, 0, 0, 0),
                }.Dyn(Theme.LineSoft, Border.BackgroundProperty));
            }
            cell.Children.Add(new TextBlock
            {
                Text = cells[i].Item1,
                FontFamily = M.Mono,
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
            }.Dyn(Theme.Ink, TextBlock.ForegroundProperty));
            cell.Children.Add(new TextBlock
            {
                Text = cells[i].Item2,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
            }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty));
            Tone.SetTabular(cell, true);
            grid.Children.Add(cell);
        }

        return new Border
        {
            Child = grid,
            BorderThickness = new Thickness(0, 1, 0, 1),
        }.Dyn(Theme.Line, Border.BorderBrushProperty);
    }

    /* ---------- 节律：3×7 热力矩阵 ---------- */

    /// <summary>
    /// 星期 × 时段热力矩阵。替掉原来那两条 2px 高、0.35 透明度 Line 色的进度条 ——
    /// 那两条在暖纸底上等于没画，而且"早 820 / 中 806"这种差别根本读不出来。
    ///
    /// 行合计恰等于 <see cref="StatsResult.SlotTotal"/>、列合计恰等于
    /// <see cref="StatsResult.WeekdayTotal"/>：两边都是对同一批事件计数
    /// （Core 里那两个也是逐条 ++，不乘份数），所以矩阵和下面那行汇总不会互相打脸。
    /// 列按周一开头（周一…周日），与中国人读「星期」的顺序一致。
    /// </summary>
    private UIElement Rhythm(Db fdb, StatsResult s)
    {
        var m = new int[3, 7];
        var max = 1;
        foreach (var e in fdb.Events)
        {
            var si = Array.IndexOf(HistoryCalc.Slots, e.Slot);
            if (si < 0) continue;
            var dow = (int)Stats.DateOf(e.When.Date).DayOfWeek;   // 0=周日，同 Stats
            var col = (dow + 6) % 7;                              // 0=周一
            m[si, col]++;
            if (m[si, col] > max) max = m[si, col];
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Pixel) });
        for (var c = 0; c < 7; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < 4; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // —— 表头：周一…周日 ——
        for (var c = 0; c < 7; c++)
        {
            var dow = (c + 1) % 7;
            var h = T("周" + _wd[dow], 10, Theme.Ink3, FontWeights.SemiBold);
            h.HorizontalAlignment = HorizontalAlignment.Center;
            h.Margin = new Thickness(0, 0, 0, 7);
            Grid.SetColumn(h, c + 1);
            Grid.SetRow(h, 0);
            grid.Children.Add(h);
        }

        for (var r = 0; r < 3; r++)
        {
            var lab = T(HistoryCalc.Slots[r], 12, Theme.Ink2, FontWeights.SemiBold);
            lab.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lab, 0);
            Grid.SetRow(lab, r + 1);
            grid.Children.Add(lab);

            for (var c = 0; c < 7; c++)
            {
                var dow = (c + 1) % 7;
                var n = m[r, c];
                var key = GridScale.MatrixKey(n, max, out var textKey);

                var cell = new Border
                {
                    Height = 26,
                    Margin = new Thickness(1, 1, 1, 1),
                    CornerRadius = new CornerRadius(3),
                    ToolTip = $"{HistoryCalc.Slots[r]} · {_wdFull[dow]} · {n} 次",
                };
                // 0 次不填色，只在格子里写一个灰字 0：填了色就等于"有量"
                if (n > 0) cell.SetResourceReference(Border.BackgroundProperty, key);
                var num = T(n.ToString("N0"), 10.5,
                    n <= 0 ? Theme.Ink3 : textKey);
                num.HorizontalAlignment = HorizontalAlignment.Center;
                num.VerticalAlignment = VerticalAlignment.Center;
                Tone.SetTabular(num, true);
                cell.Child = num;
                Tone.SetTabular((UIElement)cell.Child, true);
                Grid.SetColumn(cell, c + 1);
                Grid.SetRow(cell, r + 1);
                grid.Children.Add(cell);
            }
        }

        // —— 汇总行：早 820 · 中 806 · 晚 71 ——（数字取自 Stats.SlotTotal，没另算）
        var foot = new StackPanel { Margin = new Thickness(0, 11, 0, 0) };
        foot.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) }
            .Dyn(Theme.LineSoft, Border.BackgroundProperty));

        var sum = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var k in HistoryCalc.Slots)
        {
            sum.Children.Add(T($"{k} ", 10.5, Theme.Ink3));
            sum.Children.Add(T($"{s.SlotTotal[k]:N0}", 11, Theme.Ink2, FontWeights.SemiBold));
            sum.Children.Add(T("   ", 10.5, Theme.Ink3));
        }
        sum.Children.Add(T($"合计 {s.Total:N0} 次 · 按记录条数（不乘份数）", 10.5, Theme.Ink3));
        foot.Children.Add(sum);

        var box = new StackPanel();
        box.Children.Add(grid);
        box.Children.Add(foot);
        return CardOf(box);
    }

    /* ---------- 记录较少的时间 ---------- */

    /// <summary>
    /// 「记录较少的时间」。措辞保持原样：这里只是**没有记录**，
    /// 不叫断档、不叫已停 —— 补记和停吃在数据里长得一模一样，不能替用户下判断。
    /// 只换视觉：等宽日期区间 + 右侧「N 天 没记」徽标，整行 hover 升 Surface2。
    /// </summary>
    private UIElement GapsCard(List<Gap> gaps)
    {
        var sp = new StackPanel();

        if (gaps.Count == 0)
        {
            sp.Children.Add(T("没有长间隔，记录很连续。", 13, Theme.Ink3));
            return CardOf(sp);
        }

        var shown = gaps.Take(6).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var g = shown[i];
            var row = new Border
            {
                Padding = new Thickness(0, 10, 0, 10),
                BorderThickness = new Thickness(0, 0, 0, i == shown.Count - 1 ? 0 : 1),
            };
            if (i != shown.Count - 1)
                row.SetResourceReference(Border.BorderBrushProperty, Theme.LineSoft);

            Hover.SetBaseKey(row, Theme.Surface);
            Hover.SetHoverKey(row, Theme.Surface2);
            Hover.SetShift(row, true);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(T($"{g.From} → {g.To}", 11.5, Theme.Ink,
                va: VerticalAlignment.Center));

            var badge = new Border
            {
                CornerRadius = M.RXs,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.SetResourceReference(Border.BackgroundProperty, Theme.AccentSoft);
            var bt = new StackPanel { Orientation = Orientation.Horizontal };
            Tone.SetTabular(bt, true);
            bt.Children.Add(T($"{g.Days} 天", 11, Theme.AccentDeep, FontWeights.SemiBold));
            bt.Children.Add(T(" 没记", 11, Theme.Ink3));
            badge.Child = bt;
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);

            row.Child = grid;
            sp.Children.Add(row);
        }

        sp.Children.Add(new TextBlock
        {
            Text = "这里只是「没有记录」，不等于「没有服用」。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 11, 0, 0),
        }.Dyn(Theme.Ink2, TextBlock.ForegroundProperty));

        return CardOf(sp);
    }

    /* ---------- 通用小件 ---------- */

    /// <summary>
    /// 节标题：宋体 17 + 一句斜体补语 + 右侧等宽数字。
    /// 原来的 H2 是无衬线 14 加粗，和正文只差粗细、不成层级。
    /// </summary>
    private static UIElement Sect(string title, string sub, string right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t = new TextBlock
        {
            Text = title,
            FontFamily = M.Serif,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Bottom,
        }.Dyn(Theme.Ink, TextBlock.ForegroundProperty);
        Grid.SetColumn(t, 0);
        g.Children.Add(t);

        var q = new TextBlock
        {
            Text = sub,
            FontFamily = M.Serif,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(9, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Bottom,
        }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty);
        Grid.SetColumn(q, 1);
        g.Children.Add(q);

        var r = T(right, 10.5, Theme.Ink3);
        r.HorizontalAlignment = HorizontalAlignment.Right;
        r.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(r, 3);
        g.Children.Add(r);

        return g;
    }

    /// <summary>
    /// 容器卡片：样式键走第 1 批的 StackItCard（底色 / 圆角 / 阴影都归它管），
    /// 但**关掉整卡抬起** —— 卡片不是可点的东西，抬起来会骗人去按它。
    /// 要响应指头的是卡片里的行（台账行、空档行各自开了 Hover.Shift）。
    /// </summary>
    private static Border CardOf(UIElement inner)
    {
        var b = new Border();
        Hover.SetShift(b, false);              // 本地值先设：样式里的 Shift=True 就被压掉
        Interactions.ApplyStyle(b, Interactions.Card);
        b.Child = inner;
        return b;
    }

    private static TextBlock T(string s, double size, string key,
        FontWeight? weight = null, VerticalAlignment va = VerticalAlignment.Top)
    {
        var t = new TextBlock
        {
            Text = s,
            FontFamily = M.Mono,
            FontSize = size,
            VerticalAlignment = va,
        };
        if (weight is { } w) t.FontWeight = w;
        // CJK 用等宽族会歪，纯汉字的短标签回退到无衬线
        if (!IsNumericish(s)) t.FontFamily = M.Sans;
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        if (IsNumericish(s)) Tone.SetTabular(t, true);
        return t;
    }

    /// <summary>这串文本主要是数字/符号吗（决定要不要走等宽族）。</summary>
    private static bool IsNumericish(string s)
    {
        var digits = 0;
        foreach (var c in s)
            if (char.IsAsciiDigit(c) || c is '.' or ',' or '%') digits++;
        return digits * 2 >= s.Length;
    }

    /// <summary>
    /// eyebrow 的字距。WPF 的 FontFamily / TextBlock 没有 letter-spacing，
    /// 而 0.2em 这档间距是设计稿里区分 eyebrow 和正文的唯一线索 ——
    /// 所以在 ASCII 字符之间插一个空格（CJK 本来就有 1em 的固有间距，不动）。
    /// </summary>
    private static string Eyebrow(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && c is not ' ' && s[i - 1] is not ' '
                && c < 0x80 && s[i - 1] < 0x80)
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string[] _wd = { "日", "一", "二", "三", "四", "五", "六" };
    private static string[] _wdFull = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    /* ---------- 给别的页面用的公共小件（第 2 批 B/C 再统一换成样式键） ---------- */

    internal static FrameworkElement Spacer(double h) => new HeightSpacer(h);

    internal sealed class HeightSpacer : FrameworkElement
    {
        public HeightSpacer(double h)
        {
            Height = h;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    /// <summary>
    /// 旧卡片。时间流页还在用（TimelinePage 多处），本批不动它的行为：
    /// 底色 / 描边仍按实例取，圆角仍用 M.R。等新台账的样式键铺满三页再删。
    /// </summary>
    internal static Border Card(UIElement inner) => new()
    {
        Background = (Brush)Application.Current.Resources[Theme.Surface],
        BorderBrush = (Brush)Application.Current.Resources[Theme.Line],
        BorderThickness = new Thickness(1),
        CornerRadius = M.R,
        Padding = new Thickness(12, 12, 12, 12),
        Child = inner,
    };
}

/// <summary>
/// 把颜色设成 DynamicResource 的小助手（代码里的等价物 = SetResourceReference）。
/// 为什么要它：切强调色时 ThemeRuntime 是整组替换画刷对象，
/// 用实例取到的那个 Brush 从此留在旧色上 —— 界面就出现"一半新一半旧"。
/// </summary>
internal static class El
{
    /// <remarks>
    /// 约束在 <see cref="FrameworkElement"/> 而不是 DependencyObject：
    /// SetResourceReference 只定义在 FrameworkElement / FrameworkContentElement 上。
    /// </remarks>
    public static T Dyn<T>(this T el, string key, DependencyProperty dp)
        where T : FrameworkElement
    {
        el.SetResourceReference(dp, key);
        return el;
    }
}
