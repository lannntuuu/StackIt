using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;   // DoubleAnimation / Storyboard（标用条的 scaleY 过渡）
using StackIt.App.UI;

namespace StackIt.App.Views;

/// <summary>
/// 格带的深浅标尺。颜色全部来自 <see cref="Tone.GridRamp"/>（ThemeRuntime 注册成 G1..G7），
/// 分档断点保持现状：0 / 1 / 2 / 3-4 / 5-6 / 7-9 / 11+，一个都没改。
///
/// 为什么切到 G1..G7 而不是继续用 Grid0..6：旧那组的明度上下界踩过两个坑 ——
/// 上界 0.88 太贴近卡片底（小格上等于没画），下界 0.27 之后开始失彩。
/// G1..G7 的 0.850→0.200 是同时避开这两头的定版标尺。
///
/// 这里只给**资源键**、不给 Brush 实例：切强调色时 ThemeRuntime 是整组替换画刷对象，
/// 只有 DynamicResource（代码里的等价物 = SetResourceReference）会跟着重新解析。
/// </summary>
internal static class GridScale
{
    /// <summary>连续色带，浅 → 深。图例上把这七档一次铺出来给人当标尺。</summary>
    public static readonly string[] Ramp =
    {
        Theme.G1, Theme.G2, Theme.G3, Theme.G4, Theme.G5, Theme.G6, Theme.G7,
    };

    /// <summary>标尺上写的次数锚点（六档实心；0 次是空心，另外单独说明）。</summary>
    public static readonly string[] Labels = { "1", "2", "3–4", "5–6", "7–9", "11+" };

    /// <summary>
    /// 每周次数 → 填充色资源键。<c>null</c> = **不填充**，画空心描边。
    ///
    /// 0 单独一档且画成空心：它是"记了但那周没吃"，和"那时还没开始记"（完全留白）
    /// 必须一眼分得开；实心浅色块会被读成"吃得很少"，那是撒谎。
    ///
    /// 档位 → 色块照效果图第 ① 屏：<c>cellColor()</c> 用的是 1→G1 2→G2 3–4→G3
    /// 5–6→G4 7–9→G5 11+→G7（G6 留在标尺上补中间那一档，最深的那格给最多的那档）。
    /// </summary>
    public static string? FillKey(int c) => c switch
    {
        <= 0 => null,
        1 => Theme.G1,
        2 => Theme.G2,
        3 or 4 => Theme.G3,
        5 or 6 => Theme.G4,
        <= 9 => Theme.G5,
        _ => Theme.G7,
    };

    /// <summary>
    /// 节律矩阵的档位：0 → 不填色（写灰字 0），其余按占最大值的比例分五档 G3..G7。
    /// 断点照效果图 <c>lvl()</c>：<c>.18 / .36 / .55 / .75</c>。
    /// </summary>
    /// <param name="textKey">格内数字该用哪支墨水，见 <see cref="TextKeyFor"/>。</param>
    public static string MatrixKey(int n, int max, out string textKey)
    {
        var r = max > 0 ? (double)n / max : 0;
        var lvl = r switch { < 0.18 => 0, < 0.36 => 1, < 0.55 => 2, < 0.75 => 3, _ => 4 };
        var fill = lvl switch
        {
            0 => Theme.G3,
            1 => Theme.G4,
            2 => Theme.G5,
            3 => Theme.G6,
            _ => Theme.G7,
        };
        textKey = TextKeyFor(fill);
        return fill;
    }
    /// <summary>
    /// 格内数字取色：量「底色 vs 两支候选墨水」的实际对比度，取高的那支。
    ///
    /// 为什么不再写死「第几档以上算深底」：六套强调色 × 两种纸底一共 12 条色阶，
    /// 深浅的分界点每条都不一样 —— 换成靛这种本身就偏暗的强调色时 G3 已经是深底，
    /// 照档位猜就会在那一格上给出读不清的字。量一次就自洽，且跟着配色实时变。
    /// </summary>
    public static string TextKeyFor(string fillKey)
    {
        var bg = CellColor(fillKey);
        var paper = Theme.CurrentPaper;
        var pal = Theme.Current;
        // 两支墨水随纸底翻转：浅纸上的「亮字」是近白 OnAccent，深纸上的「亮字」是米白 Ink。
        var lightInk = paper.IsDark ? paper.Ink : pal.OnAccent;
        var darkInk = paper.IsDark ? pal.OnAccent : paper.Ink;
        return PaperTheme.Contrast(lightInk, bg) >= PaperTheme.Contrast(darkInk, bg)
            ? Theme.GTextOnDark : Theme.GTextOnLight;
    }
    /// <summary>资源键 → 该键此刻的实际颜色。取的是生成器同一份输出，不另算一套。</summary>
    private static Color CellColor(string key)
    {
        var i = System.Array.IndexOf(Ramp, key);
        var ramp = Tone.GridRamp(Theme.Current.Accent, Theme.CurrentPaper.RampInvert);
        return ramp[i < 0 ? ramp.Length - 1 : i];
    }
}

/// <summary>
/// 台账行 —— 统计页的主结构。一行读完一个品类，不再要人在「品类榜」和
/// 「每周用量」两块列表之间来回找同一个名字：
///
///   第 1 行  品名（M.Serif 15 SemiBold）+ 右侧等宽数字块（次数大字 + 天数）
///   第 2 行  **整行宽度**的格带，列位置由 <see cref="GridView"/> 统一给（全表同一批周）
///   第 3 行  X.X 次/周 + 覆盖率细条 + 窗口覆盖 % + 记录区间
///
/// hover：底色 Surface→Surface2、上移 1px、阴影加深，全部由第 1 批的 <see cref="Hover"/>
/// 附加属性驱动（这里不自己写一套）；左侧 2px Accent 标用 <c>scaleY</c> 长出来 ——
/// 动 Transform 不动 Width，一次 hover 不会把整张台账重排一遍。
/// </summary>
internal sealed class LedgerRow : Border
{
    /// <summary>格带高度（效果图 <c>.cells</c> 11px）。</summary>
    public const double BandH = 11;

    /// <summary>左侧标用条宽度（效果图 <c>.led::before</c> 2px）。</summary>
    public const double MarkerW = 2;

    private readonly StackPanel _band = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 10, 0, 0),
    };

    private readonly IReadOnlyList<string> _allWeeks;
    private readonly int[] _weekly;
    private readonly int _from;
    private readonly int _to;

    /// <summary>品名。叫 ItemName 不叫 Name —— 遮住 FrameworkElement.Name 会被当成
    /// XAML 运行时名字（中文标识符还可能在赋值时直接抛）。</summary>
    public string ItemName { get; }
    public string Route { get; }

    /// <summary>记录条数（与 Stats.ItemStat.Count 同源，次数按条数、不乘份数）。</summary>
    public int Count { get; }

    /// <summary>有记录的天数 —— 折叠阈值按它算。</summary>
    public int Days { get; }

    public LedgerRow(string name, string route, int count, int days,
        string first, string last, int from, int to, int[] weekly,
        IReadOnlyList<string> allWeeks, double perWeek, double cover)
    {
        ItemName = name;
        Route = route;
        Count = count;
        Days = days;
        _from = from;
        _to = to;
        _weekly = weekly;
        _allWeeks = allWeeks;

        Padding = new Thickness(0);

        // hover 整套交给交互层：底色阶跃 + 抬 1px + 阴影（它内部只动 Transform / Opacity）
        Hover.SetBaseKey(this, Theme.Surface);
        Hover.SetHoverKey(this, Theme.Surface2);
        Hover.SetShift(this, true);

        var grid = new Grid();
        grid.Children.Add(Marker());
        var content = new StackPanel
        {
            Margin = new Thickness(GridView.RowPadX, 12, GridView.RowPadX, 13),
        };
        grid.Children.Add(content);
        Child = grid;

        content.Children.Add(Line1(route, count, days));
        content.Children.Add(_band);
        content.Children.Add(Line3(first, last, perWeek, cover));
    }
    /// <summary>
    /// 行间分隔线。最后一条不画 —— 卡片自己收尾，多一条就是双线。
    /// 描边颜色走 DynamicResource，粗细设 0 时颜色无所谓。
    /// </summary>
    public void Hairline(bool on)
    {
        BorderThickness = new Thickness(0, 0, 0, on ? 1 : 0);
        if (on) SetResourceReference(Border.BorderBrushProperty, Theme.LineSoft);
    }


    /* ---------- 第 1 行：品名 + 数字块 ---------- */

    private UIElement Line1(string route, int count, int days)
    {
        var g = new Grid();
        // 品名占星列（太长就省略号），右边两块 Auto：数字永远完整
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = ItemName,
            FontFamily = M.Serif,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);
        g.Children.Add(name);

        var col = 1;

        // 外用是真正不同的语义（不是"吃得少"），值得一个小徽标；口服不打标（噪音）
        if (route == "topical")
        {
            var badge = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = M.RXs,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            badge.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            badge.Child = Text("外用", M.Sans, 9.5, Theme.Ink3, FontWeights.SemiBold);
            Grid.SetColumn(badge, col);
            g.Children.Add(badge);
            col++;
        }

        // 数字块：大字次数 + 天数。等宽 + tabular，位数变了整行不跳
        var nums = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Tone.SetTabular(nums, true);
        nums.Children.Add(Text(count.ToString("N0"), M.Mono, 15, Theme.Ink,
            FontWeights.SemiBold, VerticalAlignment.Bottom));
        nums.Children.Add(Text($" 次 · {days} 天", M.Mono, 11, Theme.Ink3,
            FontWeights.Normal, VerticalAlignment.Bottom, new Thickness(1, 0, 0, 1)));
        Grid.SetColumn(nums, col);
        g.Children.Add(nums);

        return g;
    }

    /* ---------- 第 3 行：次/周 + 覆盖细条 + 覆盖 % + 记录区间 ---------- */

    private UIElement Line3(string first, string last, double perWeek, double cover)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < 3; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

        var c = 0;

        var pw = Text($"{perWeek:0.0} 次/周", M.Mono, 10.5, Theme.Ink3);
        Grid.SetColumn(pw, c++);
        g.Children.Add(pw);

        // 覆盖率细条：底槽 LineSoft + 填充 Accent。宽度建一次就不动了、不是动画目标，
        // 所以这里用 Width 不违反"只动 Transform / Opacity"。
        const double covW = 72;
        var track = new Border
        {
            Height = 3,
            Width = covW,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.SetResourceReference(Border.BackgroundProperty, Theme.LineSoft);
        var fill = new Border
        {
            Width = Math.Max(1, covW * Math.Min(1, cover)),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        fill.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
        track.Child = fill;
        Grid.SetColumn(track, c++);
        g.Children.Add(track);

        var cov = Text($"窗口覆盖 {Math.Round(cover * 100):0}%", M.Mono, 10.5, Theme.Ink3);
        cov.ToolTip = "窗口覆盖 = 有记录的天数 ÷ 这一项自己窗口（第一次记到最后一次）的长度。\n"
            + "不是「占全部时间的比例」—— 那个数字会惩罚刚开始记的品类。";
        Grid.SetColumn(cov, c++);
        g.Children.Add(cov);

        var range = Text($"{first} → {last}", M.Mono, 10.5, Theme.Ink3);
        range.HorizontalAlignment = HorizontalAlignment.Right;
        range.TextTrimming = TextTrimming.CharacterEllipsis;
        range.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(range, c);
        g.Children.Add(range);

        return g;
    }

    /* ---------- 第 2 行：格带。宽度只能由容器算，见 GridView.Layout ---------- */

    /// <summary>
    /// 重排整条格带。<b>只在容器宽度真的变了、或行数变了的时候调用</b> ——
    /// 一次要重建 cols 个 Border，每次 SizeChanged 都做会卡（README 记着这条）。
    /// </summary>
    /// <param name="axisFrom">共享时间轴第一列在全局周表里的下标。</param>
    /// <param name="cols">列数（全表同一批周，所有行一样）。</param>
    /// <param name="widths">每列宽度，由 GridView 从容器实测宽算出来；<b>所有行共用同一份</b>，
    /// 这样第 k 列在任何一行里都一样宽、位置也一样（同列对齐才能横向比深浅）。</param>
    /// <param name="gap">格间缝。</param>
    public void RebuildBand(int axisFrom, int cols, double[] widths, double gap)
    {
        _band.Children.Clear();

        var last = cols - 1;
        for (var k = 0; k < cols; k++)
        {
            var i = axisFrom + k;
            var cw = k < widths.Length ? widths[k] : 1;
            var week = i < _allWeeks.Count ? _allWeeks[i] : "";

            var box = new Border
            {
                Width = cw,
                Height = BandH,
                CornerRadius = new CornerRadius(cw >= 5 ? 1 : 0.5),
                Margin = k == last ? new Thickness(0) : new Thickness(0, 0, gap, 0),
                // Transparent（不是 null）才有命中测试，tooltip 才飘得出来
                Background = Brushes.Transparent,
            };

            // 可记窗口之外：**完全留白**，不画任何框。
            // 那时记录表里还没有这一列 —— 不是"停了"，也不是"那周没吃"。
            if (i < _from || i > _to)
            {
                box.ToolTip = $"{week} 起 · {ItemName} · 那时还没开始记";
                _band.Children.Add(box);
                continue;
            }

            var n = _weekly[i];
            var key = GridScale.FillKey(n);

            if (key is null)
            {
                // 窗口内但为 0：空心描边。用 LineStrong 而不是 Line ——
                // Line 在 6px 的小格上跟卡片底差不到一档，等于没画（README 记过）。
                box.BorderThickness = new Thickness(1);
                box.SetResourceReference(Border.BorderBrushProperty, Theme.LineStrong);
                box.ToolTip = $"{week} 起 · {ItemName} · 记了但那周没吃";
            }
            else
            {
                box.SetResourceReference(Border.BackgroundProperty, key);
                box.ToolTip = $"{week} 起 · {ItemName} · {n} 次";
            }

            _band.Children.Add(box);
        }
    }

    /* ---------- 小件 ---------- */

    /// <summary>
    /// 左侧 2px Accent 标用。
    ///
    /// 触发条件是**行**（父 Border）的 IsMouseOver，不是标自己的：标只有 2px 宽，
    /// 指头基本不会正落在它上面。用 DataTrigger + FindAncestor 把状态绑过来；
    /// 动画放在 Trigger 的 Enter/ExitActions 里 —— 没有 TargetName 的动画，
    /// 默认目标就是被套样式的那个元素（标自己），所以不依赖 namescope，
    /// Style 被 seal 掉也照样跑得动。
    ///
    /// 为什么不动 Width：Width 是布局属性，一改整行重排；scaleY 只是合成。
    /// 为什么标用条的颜色设在 Background 上而不是 Setter 里：Setter 只能塞
    /// 某一个时刻取到的画刷实例，切强调色会留在旧色上；SetResourceReference
    /// 才是 DynamicResource，跟着配色换。可见性交给动画，颜色交给资源。
    /// </summary>
    private static Border Marker()
    {
        var ty = new ScaleTransform(1, 0);
        var m = new Border
        {
            Width = MarkerW,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = ty,
            RenderTransformOrigin = new Point(0.5, 0),   // 从上往下长
            IsHitTestVisible = false,
        };
        m.SetResourceReference(Border.BackgroundProperty, Theme.Accent);

        var style = new Style(typeof(Border));
        var over = new DataTrigger
        {
            Value = true,
            Binding = new Binding(nameof(UIElement.IsMouseOver))
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.FindAncestor, typeof(Border), 1),
                Mode = BindingMode.OneWay,
            },
        };
        over.EnterActions.Add(Grow(1));
        over.ExitActions.Add(Grow(0));
        style.Triggers.Add(over);
        m.Style = style;

        return m;
    }

    /// <summary>
    /// scaleY 过渡，220ms + Motion.EaseSlow（和第 1 批指示条那一档同一组参数）。
    /// 离屏截图不模拟指头，所以截图里标用条停在 scaleY 0 = 不可见，
    /// 这本身就是"没被 hover"的正确样子；要拍到 hover 态得真用鼠标。
    /// </summary>
    private static BeginStoryboard Grow(double to)
    {
        // Motion.Enabled = false（离屏渲染 / 自检）时把时长压到 0：照样是这个属性、
        // 这个终值，只是不占时间线 —— 和第 1 批 Motion.Animate 的处理方式一致。
        var ms = Motion.Enabled ? Motion.MsSlow : 0;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = Motion.EaseSlow,
        };
        // 只给属性、不给名字：目标是被套样式的元素自己（就是这 2px 标用条）
        Storyboard.SetTargetProperty(a, new PropertyPath("RenderTransform.ScaleY"));
        return new BeginStoryboard { Storyboard = new Storyboard { Children = { a } } };
    }

    /// <summary>文本小件。颜色一律 SetResourceReference（= DynamicResource）。</summary>
    private static TextBlock Text(string s, FontFamily family, double size, string key,
        FontWeight? weight = null, VerticalAlignment va = VerticalAlignment.Center,
        Thickness margin = default)
    {
        var t = new TextBlock
        {
            Text = s,
            FontFamily = family,
            FontSize = size,
            VerticalAlignment = va,
            Margin = margin,
        };
        if (weight is { } w) t.FontWeight = w;
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }
}
