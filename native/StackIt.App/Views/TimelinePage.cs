using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 时间流（第 2 批 B，照效果图第 ② 屏重排）。服用记录与体感按时间对齐排在一起，
/// 是否相关由用户自己判断。
///
/// 这一批改了三件事，每件都有理由：
///
/// 1. **日期分组换成左侧「日脊」。** 原来每天一行三个 chip 当表头，扫读时眼睛要在
///    表头与行之间来回跳；现在竖线一直在左边、每天一个空心节点，星期（宋体）与
///    日期（等宽）挂在线的左侧，右侧只剩当天的记录卡 —— 视线只沿一条线走。
/// 2. **行内容做减法。** 默认只有 `时间 · 品名 · ×份数 · 时段`。
///    「口」这个标签删了：这一页装的全是口服记录，标签是噪音；唯一的外用（米诺地尔）
///    在展开的编辑器里用「口服 / 外用」小标区分。
///    时段从描边 chip 降级成**一个小色点 + 文字**（早=G6 / 中=G4 / 晚=LineStrong：
///    同一族里取明度位置，不是三个不同色相）。文字始终在场 —— 色点是辅助，
///    不能让人靠辨色才知道是早还是晚。
///    「改 / 删」平时不可见也不参与命中测试，行 hover 或键盘焦点落进这一行才常驻。
/// 3. **空白日显式说出来。** 相邻两天之间缺 1 天以上就插一条虚线 +
///    「09-06 · 这天没记」/「09-06 ~ 09-08 · 这 3 天没记」。
///    措辞是硬要求：不说「断档」、不说「已停」、不写「0 次」。这一页只讲
///    「记了但空着」，而「那时还没开始记」不归这里说 —— 「没记」是唯一诚实的说法。
///
/// 点任意一条就地编辑（不弹窗）：左 2px Accent 标 + 2×2 分组字段（品类 / 日期时间 /
/// 时段 / 份数）。**不设草稿状态**（README「改完立刻落盘并重算统计」是产品规则）：
/// 改哪一项就写哪一项，写完立刻重画本页 —— 所以动作区只有 `取消`(文字，含义=收起编辑器)
/// 和 `删除这条`(危险，行内二次确认)，没有也不需要 `保存`。
/// 落库走 <c>Store.UpdateEvent</c>，而它对 <c>Dose</c> / <c>Slot</c> 是
/// **无条件覆写**（Store.cs:338/339）—— 传 null 就等于把已有的时段/份数洗掉，
/// 所以每次写入都把没改的那两项按记录现值一起交出去。
///
/// 虚拟化一个字没动（ListBox + VirtualizingStackPanel + IsVirtualizing +
/// Recycling + ScrollUnit=Pixel）：1697 条 / 543 天全量实例化时布局要 9.4 秒。
/// 骨架只盖在列表上走**一帧**，下一帧就从树上摘掉，不参与虚拟化。
/// </summary>
public sealed class TimelineView : ScrollViewer
{
    private readonly MainWindow _host;

    /* ---------- 就地编辑状态 ---------- */

    private string? _editing;
    private string? _confirming;

    // 服用记录这一栏没有"编辑器里的现值"要留：改一项就当场落盘，
    // 而落盘之后记录本身就是最新值，编辑器直接从记录读。
    // （体感那边留了三个框的缓冲，见下面 _obsEdit*——那里的框是 multiline，
    //   中途重画会把正在打的 caret 打掉，所以那几个字段还有用。）

    // 体感编辑器同理：类型芯片直接落盘，两个框改完就走。_obsEditText 为 null
    // 表示「这次要把补充文字清空」，和「没动过」分得开（Store 用 clearText 表达）。
    private string? _obsEditFor;
    private string? _obsEditKind;
    private string? _obsEditSubject;
    private string? _obsEditText;

    /// <summary>已经排了一次本页重画，别在同一个输入序列里排第二次。</summary>
    private bool _rebuildQueued;

    /* ---------- 新增体感 ---------- */

    private bool _obsOpen;
    private string _obsKind = "benefit";
    private string _obsSubject = "";
    private string _obsText = "";
    /// <summary>「先写一下部位」这种校验改成行内提示，不再 MessageBox。</summary>
    private bool _obsHint;

    /// <summary>
    /// 骨架只在本会话第一次构建时间流时出现。之后每帧都是真内容 ——
    /// 让一个假列表常驻，比没有骨架更糟（用户会以为数据丢了）。
    /// </summary>
    private static bool _skeletonDone;

    private static readonly string[] Kinds = { "benefit", "sideEffect", "symptom", "note" };

    private static readonly Dictionary<string, string> KindNames = new()
    {
        ["benefit"] = "好转",
        ["sideEffect"] = "不适",
        ["symptom"] = "症状",
        ["note"] = "备注",
    };

    /// <summary>
    /// 延迟到第一次测量才建可视树的容器。
    ///
    /// 为什么需要：ListBox 的虚拟化只免掉离屏项的测量与排版，免不了「先把每个
    /// 项的 UIElement 都 new 出来」。543 天 × 每天十几个元素，构造就吃掉约 955ms，
    /// 而其中真正会显示在视口里的只有十来天。
    ///
    /// 用 Decorator 而不是 DataTemplate：现有的 DayBlock 构建代码一行都不用重写，
    /// 只是把「现在就建」推迟成「要量了才建」。工厂取完即丢，不会重复建。
    /// </summary>
    private sealed class LazyDay : Decorator
    {
        private Func<UIElement>? _build;
        public LazyDay(Func<UIElement> build) => _build = build;
        private void Ensure()
        {
            if (_build is null) return;
            var f = _build;
            _build = null;
            Child = f();
        }
        protected override Size MeasureOverride(Size c) { Ensure(); return base.MeasureOverride(c); }
        protected override Size ArrangeOverride(Size c) { Ensure(); return base.ArrangeOverride(c); }
    }
    /* ---------- 日脊的度量（不要散落魔法数字） ---------- */

    /// <summary>左侧脊槽宽度：标签 44 + 竖线 + 节点余量（效果图 .rail{padding-left:52px}）。</summary>
    private const double RailGutter = 52;

    /// <summary>竖线在脊槽里的 x（效果图 ::before left:43px）。</summary>
    private const double RailLineX = 43;

    /// <summary>空心节点直径（效果图 .dot 7px）。</summary>
    private const double NodeD = 7;

    /// <summary>节点离本块顶端的距离。</summary>
    private const double NodeTop = 7;

    /// <summary>空白日说明块的高度。节点要跟着它下移，竖线才不断 —— 所以是定高。</summary>
    private const double GapBlockH = 26;

    /// <summary>一天结束后的留白。画在块**内**，竖线才能连着下一天。</summary>
    private const double DayTail = 14;

    /// <summary>当天卡靠脊那一侧略方（效果图 border-radius: r-sm r-md r-md r-sm）。</summary>
    private static readonly CornerRadius DayCardRadius = new(4, 8, 8, 4);

    /* ---------- 筛选：状态与渲染器都在 DateFilter，这一页只调用 ---------- */

    /// <summary>
    /// 筛选状态、取值格式（all / y: / m: / w: 周一日期）与那条筛选条的渲染
    /// 全都只有一份，在 <see cref="DateFilter"/> 里；统计页与本页各自调
    /// <see cref="DateFilter.Bar"/>，点芯片走 DateFilter.Toggle →
    /// MainWindow 订阅的 Changed → 重画当前页，所以这里不再有自己的 SetFilter。
    ///（上一批在本页另画的 FilterRail / Chip / ChipRow / IsoWeek 已删 ——
    ///  两份实现必然分叉；根因是当时 Bar 的芯片取的是写死的画刷实例，
    ///  现在颜色一律走样式键 StackItChip / StackItChipOn，两份就合成一份了。）
    /// </summary>

    public TimelineView(MainWindow host)
    {
        _host = host;
        Build();
    }

    /// <summary>
    /// 就地重画本页（还是同一个实例，编辑状态留着）。
    ///
    /// 为什么不走 _host.Refresh()：ShowPage 会 **new TimelineView**，实例字段
    /// （正在编辑哪一条、确认条开没开、编辑器里的当前值）全被丢掉 ——
    /// 结果是点「改」看着没反应、改一项编辑器就自己关掉。数据其实已经落盘了
    /// （Store 写盘），其它页面下次 ShowPage 时按新数据重算，所以这里只重画自己。
    ///
    /// 为什么必须推迟到当前事件派发结束：改动发生在某个按钮 / 文本框自己的回调里，
    /// 而重画会把那个控件连同整棵可视树拆掉 —— 在事件派发中途失去源元素，
    /// WPF 的后续行为不可预期（MainWindow.Refresh 的注释记的就是这一条）。
    /// </summary>
    private void Rebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(RebuildNow),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RebuildNow()
    {
        _rebuildQueued = false;
        Build();
    }

    /// <summary>整页内容。首次构造与就地重画走同一份代码。</summary>
    private void Build()
    {
        // 头部固定、列表占满剩余高度：Grid 两行（Auto + Star）。
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new(1, GridUnitType.Star) });

        var sp = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };

        var title = Tb("时间流", M.Serif, 21, Theme.Ink, FontWeights.Bold);
        sp.Children.Add(title);

        var sub = Tb("只把服用和体感按时间对齐，是否相关由你判断。点任意一条就能改。",
            M.Sans, 12.5, Theme.Ink3,
            wrap: true, lineHeight: 19, margin: new Thickness(0, 5, 0, 12));
        sp.Children.Add(sub);

        var obsBtn = Action(_obsOpen ? "收起" : "记一条体感", Interactions.ButtonSecondary);
        obsBtn.Click += (_, _) =>
        {
            _obsOpen = !_obsOpen;
            _obsHint = false;
            Rebuild();
        };
        obsBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        obsBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
        sp.Children.Add(obsBtn);

        if (_obsOpen)
        {
            sp.Children.Add(Gap(10));
            sp.Children.Add(ObsEditor());
        }

        var allRows = BuildRows();
        if (allRows.Count == 0)
        {
            sp.Children.Add(Gap(14));
            sp.Children.Add(PlainCard(TextBlock1(
                "还没有记录。点下方「记一笔」开始。", Theme.Ink3)));
            Grid.SetRow(sp, 0);
            root.Children.Add(sp);
            Content = root;
            return;
        }

        // 筛选条跟着头部一起固定，滚动时不走 —— 翻到一半想换范围不用先滚回顶部。
        sp.Children.Add(Gap(10));
        sp.Children.Add(DateFilter.Bar(allRows.Select(r => r.Date)));   // 与统计页同一份渲染器

        var rows = allRows.Where(r => DateFilter.Has(r.Date)).ToList();
        if (rows.Count == 0)
        {
            sp.Children.Add(Gap(12));
            sp.Children.Add(PlainCard(TextBlock1(
                $"{DateFilter.Label()} 没有记录。换个时间范围看看。", Theme.Ink3)));
            Grid.SetRow(sp, 0);
            root.Children.Add(sp);
            Content = root;
            return;
        }

        // 按天分组，一天一项，交给虚拟化列表按需实例化。
        //
        // 为什么必须虚拟化：1697 条 / 543 天全量建控件时，实测构造只要 279ms，
        // 但布局要 9.4 秒（97% 时间在布局），界面等于卡死。
        // 竖向 StackPanel 会把所有子项都测量一遍，没有任何裁剪，
        // 条目数一多就是超线性增长（60 天 1.4s → 120 天 4.8s）。
        // 用 VirtualizingStackPanel 后只实例化视口内的几天。
        //
        // 这几个参数是当初把 9.4 秒降到 ~0.1 秒的那几行，本次重排一个字没动。
        var days = rows
            .GroupBy(r => r.Date)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemContainerStyle = new Style(typeof(ListBoxItem))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(Control.MarginProperty, new Thickness(0)),
                    new Setter(Control.BackgroundProperty, Brushes.Transparent),
                    new Setter(Control.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    new Setter(Control.FocusableProperty, false),
                },
            },
        };

        // 这几个是附加属性，不能写在对象初始化器里。
        // 缺了 ScrollUnit=Pixel，虚拟化仍按"项"预取，长列表照样撑满。
        list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        list.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

        // 等宽数字（tabular）只在列表上设一次：Typography 的两个附加属性是**继承**属性，
        // 里面的日期 / 时间 / 份数全都能拿到。原先每行设三次，1697 行就是 5000 多次
        // 属性写入，白白摊进构造耗时。tnum 只影响数字，不影响中文与标点。
        Tone.SetTabular(list, true);

        // 只把「懒容器」放进 Items，日块等第一次测量时再建。
        //
        // 虚拟化免掉的是离屏项的**测量与排版**，免不掉「你把 543 个日块全 new 出来」。
        // 原先这里直接调 DayBlock，构造就要 ~955ms —— 那正是时间流打开慢的主因；
        // 改成懒建之后，一次只有视口内那十来天真的长出可视树。
        for (var i = 0; i < days.Count; i++)
        {
            var dayRows = days[i];
            var dayNewer = i == 0 ? null : days[i - 1];
            var dayLatest = i == 0;
            list.Items.Add(new LazyDay(() => DayBlock(dayRows, dayNewer, dayLatest)));
        }

        // 外层不再滚动，滚动交给 ListBox —— 否则 ListBox 拿到无限高度，
        // 虚拟化会失效（它要知道视口多高，才知道该实例化几项）。
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        Grid.SetRow(sp, 0);
        root.Children.Add(sp);
        Grid.SetRow(list, 1);
        root.Children.Add(list);

        // —— 首帧骨架 ——
        // 时间流首帧有 ~100ms 布局，虚拟化之后仍可能白一下。给的是**和真实行同形状**
        // 的灰条（不是转圈）：形状本身就在预告"这里马上会长出日脊和记录卡"。
        //
        // 只在真实交互路径、且本会话第一次构建时盖一帧，下一帧（ContextIdle，
        // 排在布局与渲染之后）立刻从树上摘掉 —— 骨架常驻或跟着虚拟化走都不行。
        //
        // 自检/离屏渲染（Motion.Enabled=false，没有消息循环推进动画）不盖：
        // 那条路径上 BeginInvoke 的拆除回调可能永远不跑，截图会拍到一张假列表。
        if (Motion.Enabled && !_skeletonDone)
        {
            _skeletonDone = true;
            var sk = Skeleton();
            Grid.SetRow(sk, 1);
            root.Children.Add(sk);
            Dispatcher.BeginInvoke(new Action(() => root.Children.Remove(sk)),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        Content = root;
    }

    /* ---------- 日脊：一天 = 一段竖线 + 一个节点 + 当天的卡 ---------- */

    /// <param name="newer">列表里上一项（更新的那几天）；用来算中间缺了几天。</param>
    /// <param name="isLatest">是不是库里最新的一天。</param>
    private UIElement DayBlock(List<Row> list, List<Row>? newer, bool isLatest)
    {
        var day = list[0].Date;
        var dow = "日一二三四五六"[(int)Stats.DateOf(day).DayOfWeek];
        var gap = newer is null
            ? 0
            : (Stats.DateOf(newer[0].Date) - Stats.DateOf(day)).Days - 1;

        // 空白日说明块在卡上面，节点要跟着下移，否则它就贴在虚线上。
        var top = gap > 0 ? GapBlockH : 0;

        var block = new Grid();
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new(RailGutter, GridUnitType.Pixel) });
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

        // —— 左：竖线 + 空心节点 + 星期 / 日期 ——
        var rail = new Grid();
        Grid.SetColumn(rail, 0);

        var line = new Border
        {
            Width = 1,
            Margin = new Thickness(RailLineX, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        line.SetResourceReference(Border.BackgroundProperty, Theme.Line);
        rail.Children.Add(line);

        var node = new Border
        {
            Width = NodeD,
            Height = NodeD,
            CornerRadius = new CornerRadius(NodeD / 2),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(RailLineX + 0.5 - NodeD / 2, top + NodeTop, 0, 0),
        };
        node.SetResourceReference(Border.BackgroundProperty, Theme.Paper);   // 空心：底是纸色
        node.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        rail.Children.Add(node);

        var labels = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, top + 2, RailGutter - (RailLineX - NodeD / 2 - 6), 0),
        };
        var wd = Tb("周" + dow, M.Serif, 12, Theme.Ink,
            FontWeights.SemiBold, align: TextAlignment.Right);
        labels.Children.Add(wd);

        var dd = Tb(day.Substring(5), M.Mono, 9.5, Theme.Ink3,
            margin: new Thickness(0, 2, 0, 0), align: TextAlignment.Right);
        labels.Children.Add(dd);
        rail.Children.Add(labels);
        block.Children.Add(rail);

        // —— 右：空白日说明（虚线）+ 当天的卡 + 块内留白 ——
        var col = new StackPanel();
        Grid.SetColumn(col, 1);
        if (gap > 0) col.Children.Add(GapBlock(newer![0].Date, day, gap));

        var inner = new StackPanel();
        inner.Children.Add(DayHeader(day, list.Count, isLatest));
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) inner.Children.Add(Hairline());
            inner.Children.Add(list[i].Kind == RowKind.Event
                ? EventRow((IntakeEvent)list[i].Data!)
                : ObsRow((Observation)list[i].Data!));
        }

        var card = new Border
        {
            CornerRadius = DayCardRadius,
            Padding = new Thickness(0),
            Child = inner,
            // 卡片阴影常驻。这里不用样式键 StackItCard：那个样式带 Hover.Shift，
            // 会把整张卡抬起来 1px，而这一页的 hover 单位是**行**
            //（效果图 .rw.hov{background:var(--surface-2)}），不是卡。
            Effect = M.CardShadow(),
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        col.Children.Add(card);

        // 留白画在块内：竖线才能连到下一天（块自带 Margin 会在线上留出空洞）。
        col.Children.Add(new FrameworkElement { Height = DayTail });
        block.Children.Add(col);

        return block;
    }

    /// <summary>卡内表头：完整日期（带年份，脊上只有 MM-DD）+ 条数 + 「今天 / 库中最新」。</summary>
    private UIElement DayHeader(string day, int count, bool isLatest)
    {
        var g = new Grid { Margin = new Thickness(11, 9, 11, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var d = Tb(day, M.Mono, 10.5, Theme.Ink3);

        left.Children.Add(d);

        var today = day == DateTime.Now.ToString("yyyy-MM-dd");
        if (today) left.Children.Add(Badge("今天", Theme.AccentSoft, Theme.AccentDeep, 7));
        else if (isLatest) left.Children.Add(Badge("库中最新", Theme.AccentSoft, Theme.AccentDeep, 7));
        g.Children.Add(left);

        var cnt = Tb($"{count} 条", M.Mono, 10.5, Theme.Ink3, va: VerticalAlignment.Center);

        Grid.SetColumn(cnt, 1);
        g.Children.Add(cnt);
        return g;
    }

    /// <summary>
    /// 空白日说明：虚线 + 一句人话。
    ///
    /// 措辞是产品底线，不许换：不说「断档」、不说「已停」、不写「0 次」。
    /// 这一页只看得见「记了但空着」的时间，所以说的就是「没记」。
    /// </summary>
    private UIElement GapBlock(string newerDate, string olderDate, int gap)
    {
        var gs = HistoryCalc.AddDays(olderDate, 1);
        var ge = HistoryCalc.AddDays(newerDate, -1);
        var txt = gap == 1
            ? $"{gs.Substring(5)} · 这天没记"
            : $"{gs.Substring(5)} ~ {ge.Substring(5)} · 这 {gap} 天没记";

        var g = new Grid { Height = GapBlockH };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

        var t = Tb(txt, M.Serif, 11, Theme.Ink3, va: VerticalAlignment.Center,
            margin: new Thickness(0, 0, 9, 0), italic: true);
        g.Children.Add(t);

        // 虚线：WPF 的 Border 画不出虚线，用 Rectangle + StrokeDashArray。
        // 3px 实 / 4px 空，对效果图 repeating-linear-gradient(90deg, line-strong 0 3, transparent 3 7)。
        var ln = new Rectangle
        {
            Height = 1,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 4 },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        ln.SetResourceReference(Shape.StrokeProperty, Theme.LineStrong);
        Grid.SetColumn(ln, 1);
        g.Children.Add(ln);

        return g;
    }

    /* ---------- 服用记录行 ---------- */

    private UIElement EventRow(IntakeEvent e)
    {
        if (_editing == e.Id) return EventEditor(e);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(42, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 时间（等宽；tabular 由列表那层继承下来，见构造里的 Tone.SetTabular）。
        // Slot 可空的 4 条没有时间，显示 "—" 而不是留白。
        grid.Children.Add(Tb(e.When.Time ?? "—", M.Mono, 11.5, Theme.Ink3));

        // 品名，后面才是 ×份数。份数只有大于 1 才写：×1 是噪音。
        // 没有份数时不套那层 Grid —— 一天几十行、每行省两个 Grid + 两个
        // ColumnDefinition，实测能把构造压回去一截。
        var name = Tb(Items.NameOf(e.ItemId), M.Sans, 13.5, Theme.Ink, FontWeights.Medium,
            va: VerticalAlignment.Center, margin: new Thickness(6, 0, 6, 0),
            trim: TextTrimming.CharacterEllipsis);
        if (e.Dose is not > 1)
        {
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
        }
        else
        {
            var midCell = new Grid();
            midCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
            midCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            midCell.Children.Add(name);
            var dose = Tb($"×{e.Dose}", M.Mono, 11, Theme.AccentDeep,
                va: VerticalAlignment.Center, margin: new Thickness(0, 0, 8, 0));
            Grid.SetColumn(dose, 1);
            midCell.Children.Add(dose);
            Grid.SetColumn(midCell, 1);
            grid.Children.Add(midCell);
        }

        // 右端同一格里叠两块：时段（静止可见）与 改/删（hover / 键盘焦点才显形）。
        // 两块的宽度都参与测量，所以交换不会让行宽跳一下。
        // 时段（静止可见）与 改/删（hover / 键盘焦点才显形）叠在第三列同一格里。
        // 同格重叠就够了，不必再套一层容器：每行省一个 Grid，
        // 1697 行就是省 1697 个元素；两块都参与测量，交换时行宽也不会跳。
        var slotMark = SlotMark(e);
        Grid.SetColumn(slotMark, 2);
        grid.Children.Add(slotMark);
        var ops = OpsPanel("改", "删", () => Open(e.Id), () => Confirm(e.Id),
            pinned: _confirming == e.Id);
        Grid.SetColumn(ops, 2);
        grid.Children.Add(ops);

        // 只有真在删除确认里才多套一层 StackPanel：平时行的内容直接就是那三列。
        if (_confirming == e.Id)
        {
            var body = new StackPanel();
            body.Children.Add(grid);
            body.Children.Add(ConfirmBand("删掉这条记录？统计里的次数会跟着少一次。",
                () => DeleteEvent(e.Id), CancelConfirm));
            return RowShell(body, () => Open(e.Id));
        }
        return RowShell(grid, () => Open(e.Id));
    }

    /// <summary>
    /// 时段：一个小色点 + 文字。
    ///
    /// 颜色只取同一族里的明度位置（早=G6 / 中=G4 / 晚=LineStrong），
    /// 不是三个不同色相 —— 三个色相会被读成三个语义，而它只是"一天里的三个位置"。
    /// 文字永远在场，色点不承担唯一编码责任。
    ///
    /// e.Slot 为空时用 <see cref="SlotOfTime"/> 兜底显示（历史导入的表里没标时段）。
    /// 兜底阈值一个字没改：改了会让已入库记录的高亮跳位。
    /// </summary>
    private UIElement SlotMark(IntakeEvent e)
    {
        var inferred = e.Slot is null;
        var slot = e.Slot ?? SlotOfTime(e.When.Time);

        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = SlotRevealStyle,
        };

        // 正在确认删除这一条时，「改 / 确认删除」要常驻在同一格里 —— 时段就得让位，
        // 否则两块叠在一起。本地值优先级高于样式触发器，设上去就不再随 hover 回来。
        if (_confirming == e.Id)
        {
            sp.SetCurrentValue(UIElement.OpacityProperty, 0.0);
            sp.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
        }

        var dot = new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        dot.SetResourceReference(Border.BackgroundProperty, SlotDotKey(slot));
        sp.Children.Add(dot);

        var t = Tb(slot, M.Sans, 10.5, Theme.Ink3);
        sp.Children.Add(t);

        if (inferred)
            sp.ToolTip = "原表没标时段，这个位置是按时间推的（编辑里可以明确改掉）";
        return sp;
    }

    /// <summary>早 / 中 / 晚 → 色点的资源键。</summary>
    private static string SlotDotKey(string slot) => slot switch
    {
        "早" => Theme.G6,
        "中" => Theme.G4,
        _ => Theme.LineStrong,
    };

    /* ---------- 体感行 ---------- */

    private UIElement ObsRow(Observation o)
    {
        if (_editing == o.Id) return ObsEditor2(o);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(42, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tm = Tb(o.When.Time ?? "—", M.Mono, 11.5, Theme.Ink3);

        grid.Children.Add(tm);

        // 体感用衬线承载（效果图 .rw.feel .nm{font-family:var(--serif)}）：
        // 它和"吃了什么"是两类东西，同一行里字体就该分出来。
        var mid = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        mid.Children.Add(Badge(KindNames.TryGetValue(o.Kind, out var kn) ? kn : o.Kind,
            Theme.AccentSoft, Theme.AccentDeep, 6));
        mid.Children.Add(Tb(o.Subject, M.Serif, 13.5, Theme.Ink, FontWeights.SemiBold,
            va: VerticalAlignment.Center, margin: new Thickness(7, 0, 0, 0),
            trim: TextTrimming.CharacterEllipsis));
        if (!string.IsNullOrWhiteSpace(o.Text))
            mid.Children.Add(Tb("· " + o.Text, M.Serif, 11.5, Theme.Ink2,
                va: VerticalAlignment.Center, margin: new Thickness(7, 0, 0, 0),
                italic: true, trim: TextTrimming.CharacterEllipsis));
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var ops = OpsPanel("改", "删", () => Open(o.Id), () => Confirm(o.Id),
            pinned: _confirming == o.Id);
        Grid.SetColumn(ops, 2);
        grid.Children.Add(ops);

        if (_confirming == o.Id)
        {
            var body = new StackPanel();
            body.Children.Add(grid);
            body.Children.Add(ConfirmBand("删掉这条体感？",
                () => DeleteObs(o.Id), CancelConfirm));
            return RowShell(body, () => Open(o.Id));
        }
        return RowShell(grid, () => Open(o.Id));
    }

    /* ---------- 行的外壳：hover 抬起 + 改/删显形 ---------- */

    /// <summary>
    /// 一行 = 一个 Border：底色 Surface→Surface2、hover 抬起、阴影加深全部交给
    /// 第 1 批的 <see cref="Hover"/> 附加属性（这里不自己写一套 hover 逻辑）。
    /// </summary>
    private UIElement RowShell(UIElement content, Action onClick)
    {
        var row = new Border
        {
            Padding = new Thickness(11, 8, 11, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content,
        };
        Hover.SetBaseKey(row, Theme.Surface);
        Hover.SetHoverKey(row, Theme.Surface2);
        Hover.SetShift(row, true);
        row.MouseLeftButtonUp += (_, _) => onClick();
        return row;
    }

    /// <summary>
    /// 「改 / 删」的显隐，以及时段让位给它的交换。
    ///
    /// 判定条件仍然来自元素自己的状态（行的 IsMouseOver / IsKeyboardFocusWithin），
    /// 用 DataTrigger + FindAncestor 绑上来 —— 和第 1 批 LedgerRow 那 2px 标用条
    /// 完全同一套机制，不新写 hover 分支。
    /// 键盘这条必须存在：Tab 进到「改」按钮时它得常驻，否则看不见又点不动的东西
    /// 是键盘用户的死结（Focus.Ring 给的就是这层的可见焦点）。
    ///
    /// 只切 Opacity / IsHitTestVisible —— 两者都不是布局属性，一次 hover
    /// 不会把整页重排。静止态顺手关掉命中测试：不让人点到一团看不见的按钮。
    ///
    /// 两个样式各只造一次、全页共用：原先每行新建两份（1697 行 × 2 份 Style，
    /// 每份两个 DataTrigger + 两个 Binding），时间流构造耗时因此从 ~290ms 涨到
    /// ~700ms。Style 本身可共享（首次使用即 seal），没必要一行造一份。
    /// </summary>
    private static readonly Style OpsRevealStyle = MakeReveal(0.0, 1.0, false);
    private static readonly Style SlotRevealStyle = MakeReveal(1.0, 0.0, true);

    private static Style MakeReveal(double rest, double active, bool restHit)
    {
        var s = new Style(typeof(FrameworkElement));
        s.Setters.Add(new Setter(UIElement.OpacityProperty, rest));
        s.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, restHit));
        foreach (var prop in new[] { nameof(UIElement.IsMouseOver), nameof(UIElement.IsKeyboardFocusWithin) })
        {
            var dt = new DataTrigger
            {
                Value = true,
                Binding = new Binding(prop)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1),
                    Mode = BindingMode.OneWay,
                },
            };
            dt.Setters.Add(new Setter(UIElement.OpacityProperty, active));
            dt.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, active > rest));
            s.Triggers.Add(dt);
        }
        return s;
    }

    /// <summary>
    /// 行右端的两个小块。静止时整块不可见（Opacity 0 + 不参与命中测试），
    /// hover / 键盘焦点落到这一行才显形。
    /// pinned = 这一条正处在删除确认里：那它必须**常驻**，不能等人挪开鼠标才看见。
    /// </summary>
    private UIElement OpsPanel(string editLabel, string delLabel,
        Action onEdit, Action onDelete, bool pinned)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // 钉住态直接把 Opacity 设成本地值：本地值优先级高于样式里的触发器，
        // 于是确认中的那一行不会因人挪开鼠标又把操作收回去。
        if (!pinned) sp.Style = OpsRevealStyle;
        else sp.SetCurrentValue(UIElement.OpacityProperty, 1.0);

        var edit = new Button { Content = editLabel, Margin = new Thickness(0, 0, 4, 0) };
        Interactions.ApplyStyle(edit, Interactions.Chip);
        edit.Click += (_, _) => onEdit();
        sp.Children.Add(edit);

        var del = new Button { Content = delLabel };
        Interactions.ApplyStyle(del, Interactions.Chip);
        del.Click += (_, _) => onDelete();
        sp.Children.Add(del);

        return sp;
    }

    /// <summary>确认条里的「先不删」：只收回确认，不动数据。</summary>
    private void CancelConfirm()
    {
        _confirming = null;
        Rebuild();
    }

    /* ---------- 服用记录的就地编辑器（改哪一项就存哪一项，没有草稿） ---------- */

    /// <summary>
    /// 落盘的唯一出口。四个入口（品类 / 日期时间 / 时段 / 份数）都从这里走，
    /// 因为 <c>Store.UpdateEvent</c> 对 <c>Dose</c> / <c>Slot</c> 是**无条件覆写**
    /// （Store.cs:338/339）—— 没改的那一项必须按记录现值交出去，
    /// 随手传 null 就会把已有的时段或份数洗掉。所以那里写成两个 keep 开关，
    /// 让「这次改的是哪个字段」在调用处就看得见。
    /// </summary>
    private void WriteEvent(IntakeEvent e,
        string? itemId = null, When? when = null,
        int? dose = null, bool keepDose = true,
        string? slot = null, bool keepSlot = true)
    {
        _host.UpdateEvent(e.Id, itemId, when,
            keepDose ? e.Dose : dose,
            keepSlot ? e.Slot : slot);
        Rebuild();
    }

    /// <summary>改了日期或时间要整块换掉 When：精度跟着「有没有时间」走。</summary>
    private static When NewWhen(IntakeEvent e, string date, string? time) => new()
    {
        Date = date,
        Time = time,
        Precision = time is null ? (e.When.Time is null ? e.When.Precision : "day") : "minute",
    };

    private UIElement EventEditor(IntakeEvent e)
    {
        var item = Items.Get(e.ItemId);

        var body = new StackPanel();

        // —— 2×2 分组字段 ——
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 品类：点一下按常用度轮换，换完立刻落盘。这一页的行上不写「口 / 外用」，
        // 路由（口服 / 外用）只在这里区分 —— 全库唯一的外用就是米诺地尔。
        var itemField = new Grid();
        itemField.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        itemField.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        itemField.Children.Add(Tb(item?.Name ?? e.ItemId, M.Serif, 13,
            Theme.Ink, FontWeights.SemiBold,
            va: VerticalAlignment.Center, trim: TextTrimming.CharacterEllipsis));
        var route = Tb(item?.Route == "topical" ? "外用" : "口服", M.Sans, 9.5,
            item?.Route == "topical" ? Theme.AccentDeep : Theme.Ink3, FontWeights.SemiBold,
            va: VerticalAlignment.Center);
        Grid.SetColumn(route, 1);
        itemField.Children.Add(route);

        var itemBtn = new Button
        {
            Content = itemField,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = "点一下换下一个品类（按常用度轮换，换完立刻存）",
        };
        FieldBtn(itemBtn, current: true);
        itemBtn.Click += (_, _) => CycleItem(e);
        g.Children.Add(Field("品类", itemBtn, new Thickness(0, 0, 5, 9)));

        // 日期 / 时间：定长零填充的两个等宽框，中间一个间隔点。
        // 日期默认还是文本框（460 宽的半列里常驻日历放不下，系统日历的蓝灰也和暖纸打架），
        // 但**点它就换成 DatePicker**，选完 / 失焦再收回文本框 —— 见 DatePickField。
        var when = new StackPanel { Orientation = Orientation.Horizontal };
        var dateBox = new DatePickField(e.When.Date, 84);
        dateBox.Commit = iso => CommitPickedDate(e, iso);
        dateBox.ToolTip = "点一下出日历（也可按 F4），或直接打字 2026-09-04"
                        + "（不能改到未来：记的是已经发生过的服用）；改完离开这一格就存";
        var sep = Tb("·", M.Mono, 11, Theme.Ink3,
            va: VerticalAlignment.Center, margin: new Thickness(4, 0, 4, 0));
        var timeBox = Box(e.When.Time ?? "", 46);
        timeBox.ToolTip = "时间 HH:mm；清空就是「只记到天」";
        // 日历比文本框宽，展开时先把同格的「·」与时间框收起，那一格才放得下
        dateBox.With(sep, timeBox);
        when.Children.Add(dateBox);
        when.Children.Add(sep);
        when.Children.Add(timeBox);
        var whenCell = Field("日期 / 时间", when, new Thickness(0, 0, 0, 9));
        Grid.SetRow(whenCell, 0);
        Grid.SetColumn(whenCell, 1);
        g.Children.Add(whenCell);

        // 时段：三段。点已经亮着的那段就是取消标注（回到 null）——都是立刻生效的写。
        var slots = new UniformGrid { Columns = 3 };
        foreach (var s in HistoryCalc.Slots)
        {
            var on = e.Slot == s;
            var b = new Button
            {
                Content = s,
                Margin = new Thickness(on ? 0 : 2, 0, on ? 0 : 2, 0),
                ToolTip = on ? "再点一次取消时段标注（立刻存）" : $"标成「{s}」（立刻存）",
            };
            Interactions.ApplyStyle(b, on ? Interactions.ChipOn : Interactions.Chip);
            var ss = s;
            b.Click += (_, _) => WriteEvent(e, slot: e.Slot == ss ? null : ss, keepSlot: false);
            slots.Children.Add(b);
        }
        var slotWrap = new StackPanel();
        slotWrap.Children.Add(slots);
        if (e.Slot is null)
            slotWrap.Children.Add(Tb("原表没标时段", M.Sans, 10, Theme.Ink3,
                margin: new Thickness(1, 5, 0, 0)));
        var slotCell = Field("时段", slotWrap, new Thickness(0, 0, 5, 9));
        Grid.SetRow(slotCell, 1);
        Grid.SetColumn(slotCell, 0);
        g.Children.Add(slotCell);

        // 份数：数字框 + 单位 + 加减
        var unit = item?.Unit ?? "份";
        var dose = new StackPanel { Orientation = Orientation.Horizontal };
        var doseBox = Box((e.Dose ?? 1).ToString(), 34);
        doseBox.ToolTip = $"份数（{unit}）";
        Tone.SetTabular(doseBox, true);
        dose.Children.Add(doseBox);
        dose.Children.Add(Tb(unit, M.Sans, 11, Theme.Ink3,
            va: VerticalAlignment.Center, margin: new Thickness(4, 0, 6, 0)));

        void Stepper(string label, int delta)
        {
            var b = new Button
            {
                Content = label,
                Width = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 2, 0),
                FontFamily = M.Mono,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            Interactions.ApplyStyle(b, Interactions.Chip);
            b.Click += (_, _) =>
            {
                var n = Math.Max(1, (e.Dose ?? 1) + delta);
                if (n == (e.Dose ?? 1)) return;          // 已经到底了，不必写一次盘
                doseBox.Text = n.ToString();             // 先给人看到，重画只是确认它
                // 1 份就是"没标份数"：交 null 让库里保持"未标注"而不是硬写 1
                WriteEvent(e, dose: n == 1 ? null : n, keepDose: false);
            };
            dose.Children.Add(b);
        }
        Stepper("−", -1);
        Stepper("＋", 1);
        var doseCell = Field($"份数（{unit}）", dose, new Thickness(0, 0, 0, 9));
        Grid.SetRow(doseCell, 1);
        Grid.SetColumn(doseCell, 1);
        g.Children.Add(doseCell);

        body.Children.Add(g);

        // 三个框：打字时用框里的内容，离开这一格（或回车）就写盘。
        // 非法值一律还原成记录现值，不默默改成别的日期。
        // 日期的文本态是 DatePickField 里那个 TextBox（点它才换日历），提交口径完全没变。
        dateBox.Text.LostFocus += (_, _) => CommitDate(e, dateBox.Text);
        dateBox.Text.KeyDown += (_, ev) =>
        {
            if (ev.Key != System.Windows.Input.Key.Enter) return;
            CommitDate(e, dateBox.Text);
            ev.Handled = true;
        };
        timeBox.LostFocus += (_, _) => CommitTime(e, timeBox);
        timeBox.KeyDown += (_, ev) =>
        {
            if (ev.Key != System.Windows.Input.Key.Enter) return;
            CommitTime(e, timeBox);
            ev.Handled = true;
        };
        doseBox.LostFocus += (_, _) => CommitDose(e, doseBox);
        doseBox.KeyDown += (_, ev) =>
        {
            if (ev.Key != System.Windows.Input.Key.Enter) return;
            CommitDose(e, doseBox);
            ev.Handled = true;
        };

        // —— 底部动作：没有「保存」这颗键，因为改动已经落盘了 ——
        // 「取消」的含义是**收起编辑器**，不是丢弃改动（README：改完立刻落盘，不设草稿状态）。
        var acts = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancel = Action("取消", Interactions.ButtonText);
        cancel.ToolTip = "收起编辑器（已经改过的都已经存了）";
        cancel.Click += (_, _) => CloseEdit();
        acts.Children.Add(cancel);

        var hint = Tb("改哪一项就立刻存哪一项", M.Serif, 10.5, Theme.Ink3,
            va: VerticalAlignment.Center, margin: new Thickness(8, 0, 0, 0), italic: true);
        Grid.SetColumn(hint, 1);
        acts.Children.Add(hint);

        var delBtn = Action("删除这条", Interactions.ButtonDanger);
        delBtn.HorizontalAlignment = HorizontalAlignment.Right;
        delBtn.Click += (_, _) =>
        {
            _confirming = e.Id;      // 行内展开确认，不弹窗
            Rebuild();
        };
        Grid.SetColumn(delBtn, 3);
        acts.Children.Add(delBtn);
        body.Children.Add(acts);

        if (_confirming == e.Id)
            body.Children.Add(ConfirmBand("删掉这条记录？统计里的次数会跟着少一次。",
                () => DeleteEvent(e.Id), CancelConfirm));

        return EditorShell(body);
    }

    /// <summary>
    /// 日期输入的**唯一**校验口径：ISO 解析 + 不许改到未来。
    /// 手打与日历点选都走这里，免得两条路各定一套「什么算合法」。
    /// 判不出来（含未来日期）返回 null，由调用方还原 —— 不默默改成别的日期。
    /// </summary>
    private static string? ParseDateInput(string raw)
    {
        var v = raw.Trim();
        var ok = DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var d)
                 || DateTime.TryParse(v, CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out d);
        if (!ok) return null;

        var iso = Stats.Iso(d);
        // 不能改到未来：这里记的是已经发生的服用。
        if (string.CompareOrdinal(iso, DateTime.Now.ToString("yyyy-MM-dd")) > 0) return null;
        return iso;
    }

    private void CommitDate(IntakeEvent e, TextBox box)
    {
        var iso = ParseDateInput(box.Text);
        if (iso is null)
        {
            box.Text = e.When.Date;   // 非法就还原，不要默默改成别的日期
            return;
        }
        if (iso == e.When.Date) return;
        WriteEvent(e, when: NewWhen(e, iso, e.When.Time));
    }

    /// <summary>
    /// 日历点选的写入：合法且确实变了才落盘，时段与份数按记录现值透传
    /// （Store.UpdateEvent 对这两项是无条件覆写，见 WriteEvent）。
    /// 返回 false = 这里没写，字段自己把日历收回去、文本还原成记录现值。
    /// 改到过去日期的标注行为没变：本页从来不给记录加「补记」字样，
    /// 卡内表头的「今天 / 库中最新」按落库后的日期现算，与手打那条路完全一致。
    /// </summary>
    private bool CommitPickedDate(IntakeEvent e, string picked)
    {
        var iso = ParseDateInput(picked);
        if (iso is null || iso == e.When.Date) return false;
        WriteEvent(e, when: NewWhen(e, iso, e.When.Time));
        return true;
    }

    /// <summary>
    /// 就地编辑器里的日期字段：**平时是等宽文本框（观感与上一批一致），点一下才换成 DatePicker**。
    ///
    /// 为什么这样折中：460 宽的半列里同一格还要放「·」与时间框，常驻的日历控件放不下，
    /// 而系统日历的蓝灰与暖纸配色打架；可换成纯文本框之后补记要手打日期，又比原来退步。
    /// 于是两头的便宜都留下：默认只给文本框，要点选时点它才长出日历，
    /// 选完 / 失焦 / Esc 就收回文本框。
    ///
    /// 手打这条路没丢：只有「点之前没有焦点」才跳日历（第二次点击是放光标，不抢），
    /// Tab 进来直接是编辑态，F4 也能叫出日历。
    ///
    /// 校验不在这里：选出来的日期交给宿主的 Commit，也就是 ParseDateInput 那一套
    /// （ISO 解析 / 不许未来），被拒就还原成记录现值 —— 和手打同一个标准。
    /// </summary>
    private sealed class DatePickField : Grid
    {
        private readonly TextBox _text;
        private readonly DatePicker _cal;
        private readonly List<UIElement> _companions = new();
        private string _iso;
        private bool _picking;
        private bool _hadFocus;
        private bool _seeding;         // 给日历灌初值那一下不算「人选了一天」


        /// <summary>宿主写入出口：true = 已接受（宿主随即重画）；false = 拒绝，本控件还原。</summary>
        public Func<string, bool>? Commit;

        /// <summary>文本态那个框：宿主的 LostFocus / 回车提交挂在它上面。</summary>
        public TextBox Text => _text;

        public DatePickField(string iso, double width)
        {
            _iso = iso;

            _text = Box(iso, width);
            // 按下时先记下「点之前有没有焦点」：TextBox 在 MouseLeftButtonDown 里就把焦点
            // 拿走了，等到 Up 再问永远是 true —— 不区分会让「第二次点击放光标」也跳成日历。
            _text.PreviewMouseLeftButtonDown += (_, _) => _hadFocus = _text.IsKeyboardFocused;
            // handledEventsToo：TextBox 自己会吃掉这一刀的类处理，普通实例 handler 收不到。
            _text.AddHandler(MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(
                (_, _) => { var had = _hadFocus; _hadFocus = false; if (!had) DeferEnterPick(); }), true);
            _text.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key != System.Windows.Input.Key.F4) return;
                EnterPick();
                ev.Handled = true;
            };
            Children.Add(_text);

            _cal = new DatePicker
            {
                MinWidth = 112,
                FontSize = 12.5,
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _cal.SelectedDateChanged += (_, _) => OnPicked();
            // 三条收回路径：选完（SelectedDateChanged → 落盘重画）、焦点离开日历、Esc。
            // 只认「焦点真的离开日历子树」，而且推迟到焦点落定之后再收：同一次点击的
            // 输入后处理会在日历与它内部的部件之间搬一次焦点，当场收等于刚展开就自己关掉。
            _cal.LostFocus += (_, _) => DeferExitPick();
            _cal.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key != System.Windows.Input.Key.Escape) return;
                ExitPick();
                _text.Focus();          // Esc = 我要手打，把光标还给文本框
                ev.Handled = true;
            };
            Children.Add(_cal);
        }

        /// <summary>日历展开时要一起收起的同格控件（不然那一格放不下日历）。</summary>
        public DatePickField With(params UIElement[] els)
        {
            _companions.AddRange(els);
            return this;
        }

        private void EnterPick()
        {
            if (_picking) return;
            _picking = true;
            _text.Visibility = Visibility.Collapsed;
            foreach (var el in _companions) el.Visibility = Visibility.Collapsed;

            // 把记录现有的日期灌进日历，让人一进来就看到自己站在哪一天。
            // 必须挡住这一次 SelectedDateChanged：它是**同步**触发的，不挡的话
            // 「灌初值」会被当成「人选了一天」，而那一天正是记录本身 ——
            // Commit 判「没变」返回 false，于是日历刚展开就被自己收回去（实测到过）。
            var cur = Stats.DateOf(_iso);
            _seeding = true;
            try
            {
                if (_cal.SelectedDate?.Date != cur.Date) _cal.SelectedDate = cur;
            }
            finally { _seeding = false; }

            _cal.Visibility = Visibility.Visible;
            _cal.Focus();
        }

        private void DeferEnterPick()
        {
            if (_picking) return;
            Dispatcher.BeginInvoke(new Action(EnterPick),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void DeferExitPick()
        {
            if (!_picking) return;
            if (_cal.IsKeyboardFocusWithin) return;
            Dispatcher.BeginInvoke(new Action(ExitPick),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ExitPick()
        {
            if (!_picking) return;
            _picking = false;
            _cal.Visibility = Visibility.Collapsed;
            _text.Text = _iso;          // 还原成上一次合法值，不留在被拒的那个日期上
            _text.Visibility = Visibility.Visible;
            foreach (var el in _companions) el.Visibility = Visibility.Visible;
        }

        private void OnPicked()
        {
            if (_seeding || !_picking || _cal.SelectedDate is not { } sel) return;
            var iso = Stats.Iso(sel);
            if (Commit?.Invoke(iso) == true)
            {
                _iso = iso;             // 宿主已落盘并按新值重画，这里只记住现值
                return;
            }
            ExitPick();                 // 未来 / 解析不出 / 与现值相同：收回文本框并还原
        }
    }

    private void CommitTime(IntakeEvent e, TextBox box)
    {
        var v = box.Text.Trim();
        if (v.Length == 0)
        {
            if (e.When.Time is null) return;      // 本来就是"只记到天"
            WriteEvent(e, when: NewWhen(e, e.When.Date, null));   // 明确清空
            return;
        }
        if (!Stats0.TryParseTime(v, out var parsed))
        {
            box.Text = e.When.Time ?? "";         // 非法就还原
            return;
        }
        if (parsed == (e.When.Time ?? "")) return;
        WriteEvent(e, when: NewWhen(e, e.When.Date, parsed));
    }

    private void CommitDose(IntakeEvent e, TextBox box)
    {
        if (!int.TryParse(box.Text.Trim(), out var n) || n < 1)
        {
            box.Text = (e.Dose ?? 1).ToString();
            return;
        }
        var next = n == 1 ? (int?)null : n;       // 1 份 = 不标份数
        if (next == e.Dose) return;
        WriteEvent(e, dose: next, keepDose: false);
    }

    /// <summary>品类选择：按常用度排一排，点一下换下一个（换完立刻落盘）。</summary>
    private void CycleItem(IntakeEvent e)
    {
        var route = Items.Get(e.ItemId)?.Route ?? "oral";
        var list = HistoryCalc.SheetItems(_host.Db, 8)
            .Concat(Items.All.Where(i => i.Route == route).Select(i => i.Id))
            .Distinct()
            .ToList();
        if (list.Count == 0) return;
        var idx = list.IndexOf(e.ItemId);
        var next = list[((idx < 0 ? -1 : idx) + 1) % list.Count];
        if (next == e.ItemId) return;
        WriteEvent(e, itemId: next);
    }

    /* ---------- 体感的就地编辑器 ---------- */

    /// <summary>
    /// 体感的就地编辑器。和服用记录那一栏同一个规矩：**改一项就落盘一项**，
    /// 动作区只有 `取消`（收起编辑器）与 `删除这条`（行内二次确认），没有「保存」。
    /// </summary>
    private UIElement ObsEditor2(Observation o)
    {
        if (_obsEditFor != o.Id)
        {
            _obsEditFor = o.Id;
            _obsEditKind = o.Kind;
            _obsEditSubject = o.Subject;
            _obsEditText = o.Text ?? "";
        }

        var body = new StackPanel();

        var kinds = new UniformGrid { Columns = 4 };
        foreach (var k in Kinds)
        {
            var on = _obsEditKind == k;
            var b = new Button
            {
                Content = KindNames[k],
                Margin = new Thickness(on ? 0 : 2, 0, on ? 0 : 2, 0),
            };
            Interactions.ApplyStyle(b, on ? Interactions.ChipOn : Interactions.Chip);
            var kk = k;
            b.Click += (_, _) =>
            {
                if (_obsEditKind == kk) return;
                _obsEditKind = kk;
                WriteObs(o, kind: kk);
            };
            kinds.Children.Add(b);
        }
        body.Children.Add(Field("类型", kinds, new Thickness(0, 0, 0, 9)));

        var subj = Box(_obsEditSubject ?? "", double.NaN);
        subj.HorizontalAlignment = HorizontalAlignment.Stretch;
        subj.FontFamily = M.Sans;
        subj.ToolTip = "部位或事情";
        subj.LostFocus += (_, _) => CommitObsSubject(o, subj);
        subj.KeyDown += (_, ev) =>
        {
            if (ev.Key != System.Windows.Input.Key.Enter) return;
            CommitObsSubject(o, subj);
        };
        body.Children.Add(Field("部位 / 事情", subj, new Thickness(0, 0, 0, 9)));

        var text = new TextBox
        {
            Text = _obsEditText ?? "",
            FontSize = 12.5,
            Padding = new Thickness(0),
            AcceptsReturn = true,
            Height = 60,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = M.Sans,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        text.SetResourceReference(TextBox.ForegroundProperty, Theme.Ink);
        text.LostFocus += (_, _) => CommitObsText(o, text);
        body.Children.Add(Field("补充（可留空）", text, new Thickness(0, 0, 0, 9)));

        // —— 底部只剩两档：收起 + 危险（没有「保存」，改动已经各自落盘了）——
        var acts = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancel = Action("收起", Interactions.ButtonText);
        cancel.ToolTip = "改动已经存好了，这里只是把编辑器收起来";
        cancel.Click += (_, _) => CloseEdit();
        acts.Children.Add(cancel);

        var del = Action("删除这条", Interactions.ButtonDanger);
        del.HorizontalAlignment = HorizontalAlignment.Right;
        del.Click += (_, _) =>
        {
            _confirming = o.Id;      // 行内展开确认，不弹窗
            Rebuild();
        };
        Grid.SetColumn(del, 2);
        acts.Children.Add(del);
        body.Children.Add(acts);

        if (_confirming == o.Id)
            body.Children.Add(ConfirmBand("删掉这条体感？", () => DeleteObs(o.Id), CancelConfirm));

        return EditorShell(body);
    }

    /// <summary>
    /// 体感的写入出口。UpdateObservation 认的是「传了就改、没传就保留」，
    /// 而 text 是可选字段：清空文字和「不改文字」必须分得开，所以那里多带一个 clearText。
    /// </summary>
    private void WriteObs(Observation o, string? kind = null, string? subject = null,
        string? text = null, bool clearText = false)
    {
        _host.UpdateObservation(o.Id, kind, subject, text, clearText);
        Rebuild();
    }

    /// <summary>部位不能改成空：空回去就不写，界面上还原成记录里的那个。</summary>
    private void CommitObsSubject(Observation o, TextBox box)
    {
        if (_rebuildQueued) return;
        var v = box.Text.Trim();
        if (v.Length == 0)
        {
            box.Text = o.Subject;
            _obsEditSubject = o.Subject;
            return;
        }
        _obsEditSubject = v;
        if (v == o.Subject) return;
        WriteObs(o, subject: v);
    }

    private void CommitObsText(Observation o, TextBox box)
    {
        if (_rebuildQueued) return;
        var v = box.Text.Trim();
        _obsEditText = box.Text;
        if (v.Length == 0)
        {
            if (string.IsNullOrEmpty(o.Text)) return;
            WriteObs(o, clearText: true);          // 明确清空补充
            return;
        }
        if (v == (o.Text ?? "")) return;
        WriteObs(o, text: v);
    }

    /// <summary>收起编辑器。已经落盘的改动留着，不返回、不丢弃。</summary>
    private void CloseEdit()
    {
        _editing = null;
        _confirming = null;
        _obsEditFor = null;
        Rebuild();
    }

    /* ---------- 编辑器外壳 / 删除确认 ---------- */

    /// <summary>
    /// 就地编辑器的壳：左 2px Accent 标 + 比卡片低一级的底（Paper）。
    /// 效果图 .edit{border-left:2px solid accent} —— 标就是"这条正在被改"的记号，
    /// 描边只走左边，四面描边会和卡片的边界打架。
    /// </summary>
    private static UIElement EditorShell(UIElement inner)
    {
        var ed = new Border
        {
            CornerRadius = new CornerRadius(2, 6, 6, 2),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(11, 10, 11, 10),
            Child = inner,
        };
        ed.SetResourceReference(Border.BackgroundProperty, Theme.Paper);
        ed.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        return ed;
    }

    /// <summary>
    /// 删除的二次确认：展开在这一条下面，不用 MessageBox。
    /// 弹窗会把整棵可视树拆掉重建（而这条记录就在被拆的那块里），
    /// 行内确认既保住位置，也让人看得见自己在删什么。
    /// </summary>
    private UIElement ConfirmBand(string question, Action onConfirm, Action onUndo)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var box = new Border
        {
            CornerRadius = M.RSm,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 8, 7),
            Child = sp,
        };
        box.SetResourceReference(Border.BackgroundProperty, Theme.Paper);
        box.SetResourceReference(Border.BorderBrushProperty, Theme.LineStrong);

        sp.Children.Add(Tb(question, M.Sans, 12, Theme.Ink2,
            va: VerticalAlignment.Center, margin: new Thickness(0, 0, 10, 0)));

        var yes = Action("确认删除", Interactions.ButtonDanger);
        yes.Click += (_, _) => onConfirm();
        sp.Children.Add(yes);

        var no = Action("先不删", Interactions.ButtonText);
        no.Margin = new Thickness(4, 0, 0, 0);
        no.Click += (_, _) => onUndo();
        sp.Children.Add(no);

        return box;
    }

    /* ---------- 筛选条：渲染器全项目只有 DateFilter.Bar 一份 ---------- */

    /// <summary>
    /// 这里原来有一份重复实现（FilterRail / Chip / ChipRow / IsoWeek），已删。
    /// 它当初存在的理由是 DateFilter.Bar 的芯片取的是写死的画刷实例、套不上样式键；
    /// 现在 Bar() 的颜色一律走样式键 StackItChip / StackItChipOn（样式里是 DynamicResource），
    /// 两份就没有理由并存了。选项集合、计数口径、取值格式（周存周一日期）、
    /// 「只看 X · N 条（全部 M 条）」这些语义都留在 Bar 里，一个字没改。
    ///</summary>

    /* ---------- 骨架 ---------- */

    /// <summary>
    /// 首帧骨架：几条和真实行同形状的灰条（Surface 卡 + LineSoft 条），
    /// 左边照旧留出一段脊线。不给转圈 —— 形状本身就在说明"这里将长什么"。
    /// </summary>
    private UIElement Skeleton()
    {
        var outer = new Border();
        outer.SetResourceReference(Border.BackgroundProperty, Theme.Paper);

        var sp = new StackPanel { Margin = new Thickness(20, 10, 20, 20) };
        for (var d = 0; d < 3; d++)
        {
            var g = new Grid { Margin = new Thickness(0, d == 0 ? 0 : 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(RailGutter, GridUnitType.Pixel) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

            var rail = new Border
            {
                Width = 1,
                Margin = new Thickness(RailLineX, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            rail.SetResourceReference(Border.BackgroundProperty, Theme.LineSoft);
            Grid.SetColumn(rail, 0);
            g.Children.Add(rail);

            var card = new StackPanel
            {
                Margin = new Thickness(8, 0, 0, 0),
            };
            foreach (var w in new[] { 210, 150, 186 })
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(42, GridUnitType.Pixel) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

                var tm = Bar(Theme.LineSoft, 28, 11);
                row.Children.Add(tm);

                var nm = Bar(Theme.LineSoft, w);
                nm.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(nm, 1);
                row.Children.Add(nm);

                var wrap = new Border
                {
                    Background = null,
                    Child = row,
                };
                wrap.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
                card.Children.Add(wrap);
            }
            Grid.SetColumn(card, 1);
            g.Children.Add(card);
            sp.Children.Add(g);
        }

        outer.Child = sp;
        outer.IsHitTestVisible = false;
        return outer;
    }

    private static Border Bar(string colorKey, double width, double height = 11)
    {
        var b = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(Border.BackgroundProperty, colorKey);
        return b;
    }

    /* ---------- 行数据 ---------- */

    private enum RowKind { Event, Obs }

    private sealed record Row(RowKind Kind, object? Data)
    {
        public string Date => Kind == RowKind.Event
            ? ((IntakeEvent)Data!).When.Date
            : ((Observation)Data!).When.Date;

        public string? Time => Kind == RowKind.Event
            ? ((IntakeEvent)Data!).When.Time
            : ((Observation)Data!).When.Time;
    }

    /// <summary>
    /// 服用与体感合并成一条流。同一天按时间倒序，没有时间的排在最后。
    /// 排序键用字符串比较：日期与 HH:mm 都是定长零填充的，字典序即时间序。
    /// </summary>
    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        rows.AddRange(_host.Db.Events.Select(e => new Row(RowKind.Event, e)));
        rows.AddRange(_host.Db.Observations.Select(o => new Row(RowKind.Obs, o)));

        return rows
            .OrderByDescending(r => r.Date, StringComparer.Ordinal)
            .ThenByDescending(r => r.Time ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /* ---------- 打开 / 确认 / 删除 ---------- */

    private void Open(string id)
    {
        _editing = _editing == id ? null : id;
        _confirming = null;
        Rebuild();
    }

    /// <summary>两步删除：先让确认条展开在这一条下面，再点才真删。</summary>
    private void Confirm(string id)
    {
        _confirming = id;
        _editing = null;
        Rebuild();
    }

    private void DeleteEvent(string id)
    {
        _host.RemoveEvent(id);
        _confirming = null;
        _editing = null;
        Rebuild();
    }

    private void DeleteObs(string id)
    {
        _host.RemoveObservation(id);
        _confirming = null;
        _editing = null;
        _obsEditFor = null;
        Rebuild();
    }
    /* ---------- 新增体感 ---------- */

    private UIElement ObsEditor()
    {
        var body = new StackPanel();

        var kinds = new UniformGrid { Columns = 4 };
        foreach (var k in Kinds)
        {
            var on = _obsKind == k;
            var b = new Button
            {
                Content = KindNames[k],
                Margin = new Thickness(on ? 0 : 2, 0, on ? 0 : 2, 4),
            };
            Interactions.ApplyStyle(b, on ? Interactions.ChipOn : Interactions.Chip);
            var kk = k;
            b.Click += (_, _) =>
            {
                _obsKind = kk;
                Rebuild();
            };
            kinds.Children.Add(b);
        }
        body.Children.Add(Field("类型", kinds, new Thickness(0, 0, 0, 9)));

        var subj = Box(_obsSubject, double.NaN);
        subj.HorizontalAlignment = HorizontalAlignment.Stretch;
        subj.FontFamily = M.Sans;
        subj.ToolTip = "部位或事情（必填）";
        subj.TextChanged += (_, _) => _obsSubject = subj.Text;
        body.Children.Add(Field("部位 / 事情", subj, new Thickness(0, 0, 0, 9)));

        var text = new TextBox
        {
            Text = _obsText,
            FontSize = 12.5,
            Padding = new Thickness(9, 6, 9, 6),
            AcceptsReturn = true,
            Height = 62,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = M.Sans,
        };
        text.SetResourceReference(TextBox.ForegroundProperty, Theme.Ink);
        text.TextChanged += (_, _) => _obsText = text.Text;   // 新记录没有"改哪写哪"的对象，先攒在字段里
        body.Children.Add(Field("补充（可留空）", text, new Thickness(0, 0, 0, 9)));

        if (_obsHint)
            body.Children.Add(Tb("先写一下部位或事情。", M.Sans, 11.5, Theme.Danger,
                margin: new Thickness(1, 0, 0, 8)));

        var acts = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = Action("记下", Interactions.Button);
        ok.Click += (_, _) =>
        {
            var s = (_obsSubject ?? "").Trim();
            if (s.Length == 0)
            {
                _obsHint = true;   // 行内提示，不弹窗
                Rebuild();               // 就地重画：换成 _host.Refresh() 会把这页整个新建，
                //                        行内提示和已经打进的字都会跟着没了（等于提示永远不出现）
                return;
            }
            _host.AddObservation(_obsKind, s, _obsText);
            _obsSubject = "";
            _obsText = "";
            _obsHint = false;
            _obsOpen = false;
            Rebuild();
        };
        acts.Children.Add(ok);

        var cancel = Action("取消", Interactions.ButtonText);
        cancel.Margin = new Thickness(4, 0, 0, 0);
        cancel.Click += (_, _) =>
        {
            _obsOpen = false;
            _obsHint = false;
            Rebuild();
        };
        acts.Children.Add(cancel);
        body.Children.Add(acts);

        return EditorShell(body);
    }

    /* ---------- 时段兜底（不要动阈值） ---------- */

    /// <summary>
    /// Slot 为空时按时间推一个（与网页版 slotOf 同一套阈值）。
    ///
    /// 这是**历史记录的显示兜底**：那 4 条无时段记录已经按这套阈值进过统计与高亮，
    /// 改阈值会让已入库记录的高亮跳位。凌晨相关的规则只该动「记一笔」，不该动这里。
    /// </summary>
    private static string SlotOfTime(string? time)
    {
        if (time is null) return "早";
        var parts = time.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var h)) return "早";
        return h < 11 ? "早" : h < 16 ? "中" : "晚";
    }

    /* ---------- 小件 ---------- */

    /// <summary>
    /// 文本小件。颜色一律 SetResourceReference（= DynamicResource）：
    /// 换配色时 ThemeRuntime 整组替换画刷实例，只有动态引用会跟着重新解析。
    ///
    /// 排版参数走命名可选参数而不是对象初始化器：`Tb(...) { Margin = ... }`
    /// 在 C# 里根本不合法（初始化器只能跟在 new 后面），而这一页几十处文本
    /// 都要设颜色 + 一两个排版细节，写成 `new TextBlock{...}` 就得每处自己
    /// 补一次 SetResourceReference —— 漏一处就是一处不跟配色换的写死色。
    /// </summary>
    private static TextBlock Tb(string text, FontFamily fam, double size, string colorKey,
        FontWeight? weight = null, Thickness? margin = null,
        VerticalAlignment? va = null, TextAlignment? align = null, bool italic = false,
        TextTrimming trim = TextTrimming.None, bool wrap = false, double lineHeight = double.NaN)
    {
        var t = new TextBlock { Text = text, FontFamily = fam, FontSize = size };
        if (weight is { } w) t.FontWeight = w;
        if (margin is { } m) t.Margin = m;
        if (va is { } v) t.VerticalAlignment = v;
        if (align is { } a) t.TextAlignment = a;
        if (italic) t.FontStyle = FontStyles.Italic;
        if (trim != TextTrimming.None) t.TextTrimming = trim;
        if (wrap)
        {
            t.TextWrapping = TextWrapping.Wrap;
            if (!double.IsNaN(lineHeight)) t.LineHeight = lineHeight;
        }
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        return t;
    }

    private static TextBlock TextBlock1(string text, string colorKey) =>
        Tb(text, M.Sans, 12.5, colorKey, wrap: true, lineHeight: 19);

    /// <summary>行间的 hairline：留白 + 一根线，不再套第二层卡片。</summary>
    private static Border Hairline()
    {
        var b = new Border { Height = 1 };
        b.SetResourceReference(Border.BackgroundProperty, Theme.LineSoft);
        return b;
    }

    private static FrameworkElement Gap(double h) => new() { Height = h };

    private static Border PlainCard(UIElement inner)
    {
        var c = new Border
        {
            CornerRadius = M.RMd,
            Padding = new Thickness(14, 13, 14, 13),
            Child = inner,
            Effect = M.CardShadow(),
        };
        c.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        return c;
    }

    /// <summary>小徽标（今天 / 库中最新 / 体感类型）。文字永远在场，底色只是辅助。</summary>
    private static Border Badge(string text, string bgKey, string fgKey, double rightMargin)
    {
        var t = Tb(text, M.Sans, 9.5, fgKey, FontWeights.SemiBold);
        var b = new Border
        {
            CornerRadius = M.RXs,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(rightMargin, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = t,
        };
        b.SetResourceReference(Border.BackgroundProperty, bgKey);
        return b;
    }

    /// <summary>编辑器的分组字段：小标签 + 控件。</summary>
    private static UIElement Field(string label, UIElement control, Thickness margin)
    {
        var sp = new StackPanel { Margin = margin };
        sp.Children.Add(Tb(label, M.Sans, 9.5, Theme.Ink3, FontWeights.SemiBold,
            margin: new Thickness(1, 0, 0, 5)));

        // 非当前项：Paper 底 + Line 描边；控件本身就是"格"，不再叠一层描边按钮。
        var box = new Border
        {
            CornerRadius = M.RXs,
            Padding = new Thickness(9, 7, 9, 7),
            BorderThickness = new Thickness(1),
            Child = control,
        };
        box.SetResourceReference(Border.BackgroundProperty, Theme.Paper);
        box.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        sp.Children.Add(box);
        return sp;
    }

    /// <summary>把一颗按钮装成"字段"的样子（品类那种点一下就换的）。</summary>
    private static void FieldBtn(Button b, bool current)
    {
        Interactions.ApplyStyle(b, Interactions.ButtonSecondary);
        b.SetResourceReference(Button.BackgroundProperty, Theme.Paper);
        b.SetResourceReference(Button.BorderBrushProperty, current ? Theme.Accent : Theme.Line);
        b.BorderThickness = new Thickness(1);
        b.Padding = new Thickness(9, 6, 9, 6);
        b.FontSize = 13;
        b.FontWeight = FontWeights.Normal;
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        Interactions.SetRadius(b, M.RXs);
    }

    /// <summary>等宽数字框。</summary>
    private static TextBox Box(string? text, double width)
    {
        var t = new TextBox
        {
            Text = text ?? "",
            FontFamily = M.Mono,
            FontSize = 12.5,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            IsTabStop = true,
        };
        if (!double.IsNaN(width)) t.Width = width;
        t.SetResourceReference(TextBox.ForegroundProperty, Theme.Ink);
        Tone.SetTabular(t, true);
        return t;
    }

    private static Button Action(string text, string styleKey)
    {
        var b = new Button { Content = text };
        Interactions.ApplyStyle(b, styleKey);
        return b;
    }
}
