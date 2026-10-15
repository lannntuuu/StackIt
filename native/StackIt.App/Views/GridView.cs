using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 台账 —— 统计页的主结构（第 2 批 A：原「品类榜」+「每周用量」两块重复列表合并成一块）。
/// 每行一个品类，格带 = 每格一周。两种空白必须长得不一样，这是产品诚信底线：
///
///   那时还没开始记 → **完全不画**（留白）
///   记了但那周空着 → **空心描边**（LineStrong，不是 Line）
///
/// 三条规则是这张图成立的前提，重构不能把它们糊掉：
///
/// 1. **共享时间轴**：所有行的格带用同一套列位置（同一批周）。不同行同列必须
///    对齐，深浅才可比 —— 各画各的窗口就退化成 17 条互不相干的条码。
/// 2. **窗口数据用全量**：可记窗口是 <see cref="HistoryCalc.Build"/> 从全量推的。
///    先筛再 Build 会让所有品类的窗口塌缩成筛选那一段，于是图上声称
///    「锌 2026 年 7 月才开始记」—— 而它其实记了 488 次。见 HistoryCalc.BuildRange。
/// 3. **格子宽度由容器实际宽度算出来**，不能拿常量。这里踩过两次（README 记着）：
///    固定 11px → 142 周要 1562px，4/5 的品类在滚动条外面；
///    改成常量 420 再算 → 忘了减左侧 84px 品名列，每行实际 719px。
///    两次都是"画了但看不见"。所以宽度从 <c>SizeChanged</c> 量，
///    而且**只在宽度真变了（差 &gt; 1px）时重排** —— 一次要重建 cols × 行数 个 Border。
///
/// 现在品名不在格带左边占一列了（格带是整行宽度），所以列位置只按行内边距算。
/// </summary>
public sealed class GridView : StackPanel
{
    /// <summary>格带默认只画最近这么多周（效果图第 ① 屏 AW=52，460 宽下约 6.5px/格）。</summary>
    public const int DefaultAxisWeeks = 52;

    /// <summary>折叠阈值：记录天数 ≥ 这么多才直接列出（照效果图）。</summary>
    public const int ShowMinDays = 44;

    /// <summary>首屏最多列出的行数，其余折成一行。</summary>
    public const int ShowRows = 6;

    /// <summary>格与格之间的缝（效果图 <c>.cells{gap:1px}</c>）。</summary>
    public const double CellGap = 1;

    /// <summary>
    /// 单元格宽上限。周数少的时候（选到月只剩 5 周）不能让格子铺满整行 ——
    /// 那会变成"一排等宽色块"，疏密信息归零（带视图就是死在这个毛病上，README 记着）。
    /// </summary>
    public const double MaxCell = 26;

    /// <summary>单元格宽下限：至少 1 个整数像素，再窄就并成一条实带，读不出疏密。</summary>
    public const double MinCell = 1;

    /// <summary>行左右内边距。格带的可用宽度 = 容器宽 - 2×这个值。</summary>
    public const double RowPadX = 14;

    /// <summary>
    /// 首次排版用的假定宽度（默认窗口 460 减掉页面左右 20+20）。
    /// 真实宽度由 <c>SizeChanged</c> 覆盖；两者一样时差 &lt; 1px，不会重排。
    /// </summary>
    private const double SeedListWidth = 420;

    private readonly History _h;
    private readonly List<LedgerRow> _head = new();
    private readonly List<LedgerRow> _rest = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _moreCount = new();
    private readonly TextBlock _moreCond = new();
    private readonly TextBlock _moreCar = new();
    private Button? _more;
    private int _axisFrom;
    private int _axisTo;
    private double _bandW = -1;
    private bool _expanded;

    /// <summary>画出来的列数。</summary>
    public int Cols => _axisTo - _axisFrom + 1;

    /// <summary>最后一轮排版算出的基准单元格宽（整数像素；零头摊到若干列各 +1px）。</summary>
    public double CellW { get; private set; }

    /// <summary>
    /// 这一轮排版定下来的**列宽数组**（长度 = Cols）。所有行共用同一份，
    /// 所以第 k 列在任何一行里都一样宽、位置也一样 —— 共享时间轴就是它。
    /// 像素自检读它来核对"最右一格有没有被裁掉"。
    /// </summary>
    public double[] Widths { get; private set; } = System.Array.Empty<double>();

    /// <summary>是否截掉了更早的周（截了才需要"左端被截掉不代表停用"那句话）。</summary>
    public bool Truncated => Cols < _h.Weeks.Count;

    public GridView(Db db, Func<string, bool>? inRange = null,
        StatsResult? stats = null, History? history = null)
    {
        // 窗口（可记区间）必须从全量算；inRange 只把范围外的周清零。
        _h = history ?? (inRange is null
            ? HistoryCalc.Build(db)
            : HistoryCalc.BuildRange(db, inRange));
        stats ??= Stats.Compute(DateFilter.Apply(db));

        if (_h.Weeks.Count == 0 || _h.Rows.Count == 0)
        {
            Children.Add(Note("还没有记录。"));
            return;
        }

        ComputeAxis();
        BuildRows(stats);

        // —— 图例（新增：以前这张图完全没有深浅说明，用户只能猜，而且通常猜反）——
        Children.Add(Legend());

        // —— 卡片：台账行的容器 ——
        var card = new Border
        {
            Padding = new Thickness(0),          // 行自带 padding，卡片不能再套一层
            Child = _list,
        };
        Hover.SetShift(card, false);             // 本地值先设：样式里的 Shift=True 就被压掉
        Interactions.ApplyStyle(card, Interactions.Card);
        Children.Add(card);

        // —— 轴说明 + 折叠行 ——
        Children.Add(AxisCaption());
        if (_rest.Count > 0 && _more is not null) Children.Add(_more);

        // 宽度从容器量。SizeChanged 在布局时可能连着触发好几次，所以只认"真的变了"。
        _list.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _bandW) < 1.0) return;
            Layout(e.NewSize.Width);
        };

        Layout(SeedListWidth);   // 先按假定宽度排一次，量到真实宽度会自动再排
    }

    /* ---------- 共享时间轴 ---------- */

    /// <summary>
    /// 定出这一轮要画哪些周（全表共用的一组列位置）。
    ///
    /// 分两件事：**窗口数据**用全量（可记窗口不能塌缩），**画哪些格子**按可见范围，
    /// 可见范围再截到最近 <see cref="DefaultAxisWeeks"/> 周。范围外 / 窗口外都留白，
    /// 留白不等于"停了"，所以下面必须配一行说明。
    /// </summary>
    private void ComputeAxis()
    {
        var total = _h.Weeks.Count;
        var from = _h.HasRange ? Math.Max(0, _h.InRangeFrom) : 0;
        var to = _h.HasRange ? Math.Min(total - 1, _h.InRangeTo) : total - 1;
        if (to < from) { from = 0; to = total - 1; }

        if (to - from + 1 > DefaultAxisWeeks)
            from = to - DefaultAxisWeeks + 1;

        _axisFrom = from;
        _axisTo = to;
    }

    /* ---------- 行 ---------- */

    private void BuildRows(StatsResult stats)
    {
        var byId = new Dictionary<string, HistoryRow>();
        foreach (var r in _h.Rows) byId[r.ItemId] = r;

        // stats.Items 已按次数降序 —— 这就是原来「品类榜」的顺序，行序沿用。
        foreach (var it in stats.Items)
        {
            if (!byId.TryGetValue(it.ItemId, out var hr)) continue;

            var win = Math.Max(1, hr.To - hr.From + 1);
            var perWeek = (double)it.Count / win;
            var cover = Math.Min(1, it.Days / (win * 7.0));

            var row = new LedgerRow(it.Name, it.Route, it.Count, it.Days,
                it.First, it.Last, hr.From, hr.To, hr.Weeks, _h.Weeks, perWeek, cover);

            if (it.Days >= ShowMinDays && _head.Count < ShowRows) _head.Add(row);
            else _rest.Add(row);
        }

        // 筛到某个短范围（一个月 / 一周）时可能**一项都不够 44 天**，
        // 全折起来就等于给用户一本空台账 —— 那种情况退回"前 6 行"。
        if (_head.Count == 0)
            while (_head.Count < ShowRows && _rest.Count > 0)
            {
                _head.Add(_rest[0]);
                _rest.RemoveAt(0);
            }

        foreach (var r in _head) _list.Children.Add(r);

        _more = More();
        Restyle();
    }

    private IEnumerable<LedgerRow> Visible =>
        _expanded ? _head.Concat(_rest) : _head;

    /// <summary>行间 hairline：最后一条不画（卡片自己收尾）。</summary>
    private void Restyle()
    {
        var list = Visible.ToList();
        for (var i = 0; i < list.Count; i++)
            list[i].Hairline(i < list.Count - 1);
    }

    /* ---------- 排版：格带宽度全部从容器算 ---------- */
    /// <summary>
    /// 重排所有可见行的格带。<b>只在容器宽度真的变了、或者行数变了时调用。</b>
    ///
    /// 宽度算法（三件事同时成立才算对）：
    ///
    /// 1. **从容器实测宽算**：usable = 容器宽 - 左右行内边距 - 1px 余量。
    ///    不能拿常量 —— 老代码拿 420 算，忘了减左侧 84px 品名列，每行实际 719px，
    ///    多出来的被滚动条吃掉，低频品类整行看着像空白（README 记着：画了但看不见）。
    /// 2. **整数像素 + 零头摊开**：格子宽取整像素（floor 到 ≤ usable），剩下的零头
    ///    均匀摊给若干列各 +1px。为什么这样：WPF 开了 UseLayoutRounding，
    ///    小数宽会逐格四舍五入 —— 6.6px 会变成 7px，52 格就多出一截，
    ///    最右一格被容器裁掉，又是一次"画了但看不见"。摊成整数之后
    ///    cols×cell + (cols-1)×gap + extra == usable，最后一格的右沿正好贴到
    ///    行内边距内沿：既不裁，也不在右边留一条无意义的空。
    /// 3. **一份宽度数组，所有行共用**：第 k 列在任何一行里都一样宽 ——
    ///    这是"共享时间轴 / 同列对齐"的实现方式，深浅才能横向比。
    /// </summary>
    private void Layout(double listWidth)
    {
        _bandW = listWidth;

        var cols = Cols;
        // 可用宽 = 容器实测宽 - 左右行内边距 - 1px 余量。
        // 之前是 -84（品名列）-4；现在品名挪到格带上面，不再吃横向空间。
        var usable = Math.Max(40, listWidth - RowPadX * 2 - 1);

        // 基准格宽：整数像素，保证 cols 格 + (cols-1) 缝 ≤ usable
        var cell = Math.Floor((usable - (cols - 1) * CellGap) / cols);
        if (cell > MaxCell) cell = MaxCell;   // 周数少的时候不铺满（见 MaxCell 注释）
        if (cell < MinCell) cell = MinCell;
        CellW = cell;

        var used = cols * cell + (cols - 1) * CellGap;
        var extra = cell >= MaxCell ? 0 : (int)Math.Round(usable - used);
        if (extra < 0) extra = 0;
        if (extra > cols) extra = cols;

        // 零头均匀撒：第 i 份落在 (i+0.5)/extra 那一列上，两边对称不偏一侧
        var w = new double[cols];
        for (var k = 0; k < cols; k++) w[k] = cell;
        for (var i = 0; i < extra; i++)
        {
            var k = (int)Math.Round((i + 0.5) * cols / (double)extra) - 1;
            if (k < 0) k = 0;
            if (k >= cols) k = cols - 1;
            w[k] += 1;
        }
        Widths = w;

        foreach (var r in Visible) r.RebuildBand(_axisFrom, cols, w, CellGap);
    }

    /// <summary>
    /// 深浅标尺 + 两种空白的图例。
    ///
    /// 为什么必须常驻而不是 tooltip：没有它，"深色是吃得多还是更重要"只能靠猜，
    /// 而猜错的方向通常是**反的**（以为越浅越多）。tooltip 又要点到才看得见，
    /// 一张图的读法不能藏在一个动作后面。
    /// </summary>
    public static UIElement Legend()
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var row = new WrapPanel { Orientation = Orientation.Horizontal };

        row.Children.Add(Cap("每周次数"));

        // 连续色带：七档铺成一排，不逐格标数字 —— 460 宽放不下 7 组标签
        var ramp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var key in GridScale.Ramp)
        {
            var sw = new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            sw.SetResourceReference(Border.BackgroundProperty, key);
            ramp.Children.Add(sw);
        }
        row.Children.Add(ramp);

        row.Children.Add(Cap(string.Join(" · ", GridScale.Labels)));

        // 空心 = 记了但那周没吃。图例里画成和格子里一模一样：描边、不填。
        var hollow = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(1.5),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        hollow.SetResourceReference(Border.BorderBrushProperty, Theme.LineStrong);
        row.Children.Add(hollow);
        row.Children.Add(Cap("空 = 那周没吃"));

        // 留白 = 那时还没有这一列。用 Surface 底、不描边：它就该和行底融为一体。
        var blank = new Border
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        blank.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        row.Children.Add(blank);
        row.Children.Add(Cap("留白 = 还没开始记"));

        box.Children.Add(row);

        box.Children.Add(new TextBlock
        {
            // 这句是整张图最要紧的一句：深浅的方向没说清，读者就会把
            // "颜色重"读成"更重要"或者"更严重"。
            Text = "颜色越深，那一周吃得越多。每格一周，所有行同列对齐（可以直接横向比）。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty));

        return box;
    }

    private static TextBlock Cap(string s) => new TextBlock
    {
        Text = s,
        FontFamily = M.Mono,
        FontSize = 10.5,
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty);

    /* ---------- 轴说明 ---------- */

    private UIElement AxisCaption()
    {
        var sp = new StackPanel { Margin = new Thickness(2, 8, 0, 0) };

        var total = _h.Weeks.Count;
        sp.Children.Add(new TextBlock
        {
            Text = Truncated
                ? $"横轴 = 最近 {Cols} 周（全库 {total} 周）· 左端被截掉不代表停用"
                : $"横轴 = {DateFilter.Label()}的 {Cols} 周（全库 {total} 周）· 两端被截掉不代表停用",
            FontFamily = M.Mono,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
        }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty));

        var end = Stats.Iso(Stats.DateOf(_h.Weeks[_axisTo]).AddDays(6));
        sp.Children.Add(new TextBlock
        {
            Text = $"{_h.Weeks[_axisFrom]} → {end} · 每格一周",
            FontFamily = M.Mono,
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
        }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty));

        return sp;
    }

    /* ---------- 低频品类折叠 ---------- */

    /// <summary>
    /// 折叠行。展开是**行内替换**（把那几行插进同一张卡片里），不弹窗 ——
    /// 弹窗会把这页唯一能横向比较的东西（同列对齐的格带）搬到另一个窗口里去。
    /// </summary>
    private Button More()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _moreCount.FontFamily = M.Sans;
        _moreCount.FontSize = 12;
        _moreCount.FontWeight = FontWeights.SemiBold;
        _moreCount.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);
        Grid.SetColumn(_moreCount, 0);
        g.Children.Add(_moreCount);

        _moreCond.FontFamily = M.Mono;
        _moreCond.FontSize = 11;
        _moreCond.Margin = new Thickness(6, 0, 0, 0);
        _moreCond.VerticalAlignment = VerticalAlignment.Center;
        _moreCond.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        Grid.SetColumn(_moreCond, 1);
        g.Children.Add(_moreCond);

        _moreCar.FontFamily = M.Mono;
        _moreCar.FontSize = 11;
        _moreCar.HorizontalAlignment = HorizontalAlignment.Right;
        _moreCar.VerticalAlignment = VerticalAlignment.Center;
        _moreCar.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        Grid.SetColumn(_moreCar, 3);
        g.Children.Add(_moreCar);

        var b = new Button
        {
            Content = g,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(14, 11, 14, 11),
            // 键盘可达：Tab 进得来，焦点环由样式给（FocusVisualStyle = StackItFocusRing）
            Focusable = true,
        };
        Interactions.ApplyStyle(b, Interactions.ButtonSecondary);
        b.Click += (_, _) => Toggle();

        Label();
        return b;
    }

    private void Label()
    {
        var max = _rest.Count > 0 ? _rest.Max(r => r.Days) : 0;
        _moreCount.Text = _expanded
            ? $"收起这 {_rest.Count} 个品类"
            : $"其余 {_rest.Count} 个品类";
        _moreCond.Text = _expanded
            ? $"（第 {ShowMinDays} 位之后折起来的那些）"
            : $"· 记录天数 ≤ {max} 天";
        _moreCar.Text = _expanded ? "收起 ⌃" : "展开 ⌄";
    }

    private void Toggle()
    {
        _expanded = !_expanded;

        if (_expanded)
            foreach (var r in _rest) _list.Children.Add(r);
        else
            foreach (var r in _rest) _list.Children.Remove(r);

        Label();
        Restyle();

        // 行数变了：刚插进来的那些行还没有格带，必须按当前宽度排一次。
        // 宽度没变时这是唯一一次重排 —— 不要顺手在别处也调 Layout。
        if (_bandW > 0) Layout(_bandW);
    }

    /* ---------- 空态 ---------- */

    /// <summary>只有图例那几行没数据时的兜底文案（正常进不到，页面先判过 Total）。</summary>
    public static TextBlock Note(string s) => new TextBlock
    {
        Text = s,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    }.Dyn(Theme.Ink3, TextBlock.ForegroundProperty);
}
