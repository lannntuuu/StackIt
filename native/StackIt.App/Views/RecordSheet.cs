using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;      // 撤销条的 3 秒进度（只动 ScaleTransform）
using System.Windows.Threading;            // DispatcherTimer
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 记录弹层（记一笔）。第 3 批 B：照效果图第 ③ 屏重做视觉，并落地「凌晨归属」。
///
/// 写入语义一个字没动（设计见 design/记一笔重设计方案.md，断言见 Shot.SheetTest）：
///  - 常吃行 + 内联步进器，没有搜索框、没有 _rendering 重入锁；
///  - 同一 (品类, 时段) 连点只改**同一条**记录（dose+1），不生成 N 条 dose=1 的碎片；
///  - 减到 0 删整条；底部「撤销」两步内可回退（新增→删、加减→减回去）；
///  - 改动只局部刷新被点的那一行，不整树 Render。
///
/// 这一屏的层级：此刻（Mono 32）→ 时段下划线 tab（带全库真实条数）→ 常吃行
/// （键帽角标 + 宋体品名 + 副行真实文案 + 步进器）→ 底部三档动作（一个主操作）。
/// 键帽角标是新增的：1~8 快捷键以前完全没有可发现性（台账 P1）。
///
/// 记的是「已经发生的服用」，日期上限锁今天。
///
/// 「凌晨归属」（任务一，产品决策由用户定）只改**打开那一刻的默认归属**，
/// 见 <see cref="PreSleepUntilHour"/>：日期与时段在构造时一次定死成 _date / _slot，
/// 之后表头、常吃区、写入全部只读这两个字段 —— 任何一处不走同一个 _slot，
/// --sheet-test 的「＋ 记到当前时段那一列」就会在凌晨红。
/// </summary>
public sealed class RecordSheet : Window
{
    /* ---------- 凌晨归属：阈值集中在这里，别散成两处魔数 ---------- */

    /// <summary>
    /// 00:00–02:59（hour &lt; 3）打开弹层：记的是**昨天睡前**那一顿，
    /// 默认日期 = 前一天、默认时段 = 晚，界面标「睡前 · 归昨天」。
    ///
    /// 为什么 3 点前算昨天：这一档实际发生的是睡前那次，把它归到今天会让
    /// 「昨天吃了什么」凭空少一顿，而「今天凌晨」又多出一顿不该有的。
    /// </summary>
    internal const int PreSleepUntilHour = 3;

    /// <summary>
    /// 03:00–05:59（3 ≤ hour &lt; 6）：新的一天已经开始了，默认日期 = 今天，
    /// 但还没到早饭点，时段仍固定为「早」，界面标「凌晨 · 归今天」。
    /// 06:00 起走原来的 <see cref="SlotOf"/> 阈值（11 点前早 / 16 点前中 / 之后晚）。
    /// </summary>
    internal const int EarlyMorningUntilHour = 6;

    /// <summary>
    /// 测试用的时钟注入点，**只给 --sheet-test 用**，默认走 <see cref="DateTime.Now"/>。
    ///
    /// 为什么必须有：SheetTest 以前不注入时刻，这条自检实际测到哪个时段
    /// 取决于几点跑（08:00 测「早」、23:00 测「晚」）。凌晨归属落地之后，
    /// 这个不确定性从"时间"升级成了"语义"，必须能被固定时刻复现。
    /// 文件内所有"现在"一律走 <see cref="NowSafe"/>，不许再直接写 DateTime.Now。
    /// </summary>
    internal static Func<DateTime>? NowOverride;

    /// <summary>当前时刻（自检注入过就用注入值）。</summary>
    internal static DateTime NowSafe => NowOverride?.Invoke() ?? DateTime.Now;

    /* ---------- 字段 ---------- */

    private readonly MainWindow _host;
    private readonly StackPanel _body;
    private TextBlock _timeText = null!;
    private TextBlock _dayText = null!;
    private Border _attribChip = null!;
    private TextBlock _attribText = null!;
    private TextBlock _dateText = null!;
    private Button _todayBtn = null!;
    private TextBlock _summary = null!;
    private Button _undoBtn = null!;
    private Button _primaryBtn = null!;
    private TextBlock _primaryLabel = null!;
    private StackPanel _often = null!;
    private StackPanel _obsHost = null!;
    private Border _toastHost = null!;
    private Border _fade = null!;                  // 列表下沿渐隐（还有内容没露出来时才出现）
    private ScrollViewer _scroller = null!;
    private DispatcherTimer? _toastTimer;
    private FrameworkElement _expand = null!;
    private DatePicker _datePicker = null!;
    private TextBox _timeBox = null!;

    private readonly List<TabRef> _tabs = new();

    private string _date;
    private string _time;
    private string _slot = "早";
    private bool _expandOn;

    /// <summary>
    /// 归属标注的文字。空 = 不标（06:00 之后打开，默认归属没有特殊性）。
    /// 只在构造时按 <see cref="NowSafe"/> 算一次；用户手动改日期/时间/时段之后就撤掉，
    /// 因为那时写的已经不是"默认归属"了。它只影响表头那一枚 chip，不参与写入。
    /// </summary>
    private string _attribution = "";

    /// <summary>true = 本次打开落在 00:00–02:59，日期是"昨天"但含义是"睡前"，不写「补记」。</summary>
    private bool _preSleep;

    /// <summary>当前「常吃」区的品类顺序（1~8 热键对应这里，也是键帽角标的编号）。</summary>
    private List<string> _oftenOrder = new();

    /// <summary>itemId|slot -&gt; 该格对应的事件 id。连点同一格只改这一条。</summary>
    private readonly Dictionary<string, string> _cell = new();

    /// <summary>itemId|slot -&gt; 当天已记剂量（界面显示用）。</summary>
    private readonly Dictionary<string, int> _counts = new();

    /// <summary>本次操作栈，供撤销。Created=true 表示本次新建的事件。</summary>
    private readonly List<(string Id, bool Created)> _undo = new();

    /// <summary>itemId -&gt; 该品类所有行的引用，改动后局部刷新。</summary>
    private readonly Dictionary<string, List<ChipRef>> _chips = new();

    /// <summary>时段 -&gt; 全库条数。取自 Stats.SlotTotal，不在这里另算一套口径。</summary>
    private Dictionary<string, int> _tabTotals = new();

    /// <summary>itemId -&gt; 该品类在各时段的条数（Stats.ItemStat.Slots），副行文案用它。</summary>
    private Dictionary<string, Dictionary<string, int>> _itemSlots = new();

    /// <summary>主操作这一次要记谁：连点之后是"再记最后那一项"，刚打开时常吃第 1 项。</summary>
    private string? _lastItem;

    /* ---------- 记一条体感（就地展开，不弹窗） ---------- */

    private bool _obsOpen;
    private string _obsKind = "benefit";
    private string _obsSubject = "";
    private string _obsNote = "";
    private bool _obsHint;

    private static readonly string[] Slots = HistoryCalc.Slots; // 早 中 晚

    private static readonly (string Id, string Name)[] ObsKinds =
    {
        ("benefit", "好转"), ("sideEffect", "不适"), ("symptom", "症状"), ("note", "备注"),
    };

    public RecordSheet(MainWindow host)
    {
        _host = host;

        // —— 单一出口：时刻 → 时间 → 日期 → 时段，一次定死，后面全部沿用 ——
        var now = NowSafe;
        _time = Stats0.Hhmmss(now);
        _preSleep = now.Hour < PreSleepUntilHour;
        _date = Stats0.Iso(_preSleep ? now.Date.AddDays(-1) : now.Date);
        _slot = DefaultSlotOf(now.Hour, _time);
        _attribution = DefaultAttribution(now.Hour);

        Title = "记一笔";
        Width = 460;
        // 高度跟着屏幕走，小屏上不顶满：留 72px 保证抬头和底部条都在视野里。
        var cap = SystemParameters.WorkArea.Height - 72;
        Height = Math.Max(460, Math.Min(640, cap));
        MinHeight = 460;
        MinWidth = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.SetResourceReference(BackgroundProperty, Theme.Paper);   // 弹层开着时切纸底也要跟
        UseLayoutRounding = true;

        // 根容器必须是 Grid，不能是 StackPanel。
        //
        // StackPanel 量子级时给的是**无限高度**，于是里面的 ScrollViewer 永远认为
        // 「内容没超出自己」：ScrollableHeight = 0 → 滚动条不出现、滚轮也没反应；
        // 而窗口高度是固定的，多出来的补剂直接被裁在下沿外面 —— 表现就是
        // 「常吃一多就翻不下去」。三个主页面没这问题：它们的根本身就是 ScrollViewer。
        //
        // 只有列表那一行是 *（可伸缩），抬头与底部条各自钉住：任何滚动位置下
        // 「此刻」和「记这一笔」都够得着，主按钮也不随内容多少上下跳。
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 此刻 + 时段 tab
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 展开的日期 / 时间条
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });     // 唯一可滚区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 记一条体感
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 撤销条
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 底部动作
        Content = root;

        var top = SheetTop();
        Grid.SetRow(top, 0);
        root.Children.Add(top);
        var when = WhenBar(out _expand);
        Grid.SetRow(when, 1);
        root.Children.Add(when);

        _body = new StackPanel();
        _scroller = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(16, 10, 16, 10),
                Child = _body,
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // 下沿渐隐：列表被截断时给一个「下面还有」的信号。
        // 纸底色取当前配色的 Paper，切强调色后重开面板就是对的色。
        var paper = Theme.Current.Paper;
        _fade = new Border
        {
            Height = 26,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(
                Color.FromArgb(0, paper.R, paper.G, paper.B),
                Color.FromArgb(235, paper.R, paper.G, paper.B),
                new Point(0, 0), new Point(0, 1)),
        };
        var listCell = new Grid();
        listCell.Children.Add(_scroller);
        listCell.Children.Add(_fade);
        Grid.SetRow(listCell, 2);
        root.Children.Add(listCell);
        // 三个来源都要重算：视口高变了、滚了、或列表内容增减了（换时段会重建常吃区）。
        _scroller.ScrollChanged += (_, _) => UpdateFade();
        _scroller.SizeChanged += (_, _) => UpdateFade();
        _body.SizeChanged += (_, _) => UpdateFade();

        // 记一条体感：展开在列表与底部条之间，不弹任何框
        _obsHost = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 0, 16, 8),
        };
        Grid.SetRow(_obsHost, 3);
        root.Children.Add(_obsHost);

        // 撤销条：只在刚记完的那 3 秒里出现（免弹窗设计的载体）
        _toastHost = new Border();
        Grid.SetRow(_toastHost, 4);
        root.Children.Add(_toastHost);

        var footer = Footer(out _summary, out _undoBtn);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        _often = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        // 把当天已有记录灌进 _cell / _counts，再按当前时段排常吃、建行
        PourDay();
        BuildBody();

        Loaded += (_, _) => Keyboard.Focus(this);

        // 键盘快捷键（设计文档 §7）：1~8 记常吃第 N 项、Enter 记主操作那一项、
        // Ctrl+Z 撤销、Esc 关闭、↑/↓ 步进分钟、Ctrl+↑/Ctrl+↓ 步进小时。
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// 下沿渐隐只在「还能往下滚」时出现：内容本来就短、或已经滚到底，都收起来 ——
    /// 常驻的渐变看着像画坏了。
    /// </summary>
    private void UpdateFade()
    {
        var s = _scroller;
        if (_fade is null || s is null) return;
        var more = s.ScrollableHeight > 1 && s.VerticalOffset < s.ScrollableHeight - 1;
        _fade.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Undo();
            e.Handled = true;
            return;
        }
        // 文本框里有焦点时让位给输入：否则敲 08:00 会被数字热键吞掉、回车会被主操作吞掉
        var typing = Keyboard.FocusedElement is TextBox;

        if (e.Key == Key.Return && !typing && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            var target = _lastItem ?? (_oftenOrder.Count > 0 ? _oftenOrder[0] : null);
            if (target is not null) Tap(target, _slot);
            e.Handled = true;
            return;
        }

        // 数字键 1~8：记常吃第 N 项（D1~D8，避免与小键盘冲突）。
        if (e.Key >= Key.D1 && e.Key <= Key.D8 && (Keyboard.Modifiers & ModifierKeys.Control) == 0
            && !typing)
        {
            var idx = (int)e.Key - (int)Key.D1;
            if (idx < _oftenOrder.Count) Tap(_oftenOrder[idx], _slot);
            e.Handled = true;
            return;
        }
        // 时间条展开时才吃方向键，避免误触
        if (_expandOn)
        {
            if (e.Key == Key.Up) { NudgeMinute((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 60 : 5); e.Handled = true; }
            else if (e.Key == Key.Down) { NudgeMinute((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? -60 : -5); e.Handled = true; }
        }
    }

    /* ---------- 顶部：此刻 + 归属标注 + 时段下划线 tab ---------- */

    private UIElement SheetTop()
    {
        var shell = new Border
        {
            Padding = new Thickness(16, 13, 16, 11),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        shell.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        shell.SetResourceReference(Border.BorderBrushProperty, Theme.Line);

        var col = new StackPanel();

        // —— 第 1 行：这一屏唯一的大字是「此刻」 ——
        var big = new StackPanel { Orientation = Orientation.Horizontal };
        _timeText = new TextBlock
        {
            FontFamily = M.Mono,
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _timeText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);
        Tone.SetTabular(_timeText, true);
        big.Children.Add(_timeText);

        var whenWrap = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 4),
        };
        _dayText = new TextBlock
        {
            FontFamily = M.Serif,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dayText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink2);
        whenWrap.Children.Add(_dayText);

        _attribText = new TextBlock
        {
            FontFamily = M.Sans,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _attribText.SetResourceReference(TextBlock.ForegroundProperty, Theme.AccentDeep);
        _attribChip = new Border
        {
            CornerRadius = M.RXs,
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _attribText,
            Visibility = Visibility.Collapsed,
        };
        _attribChip.SetResourceReference(Border.BackgroundProperty, Theme.AccentSoft);
        whenWrap.Children.Add(_attribChip);

        big.Children.Add(whenWrap);
        col.Children.Add(big);

        // —— 第 2 行：时段 tab（左） + 日期 / 今天 / 改时间（右） ——
        var row2 = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row2.Children.Add(SlotTabs());

        var jump = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _dateText = new TextBlock
        {
            FontFamily = M.Mono,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _dateText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        jump.Children.Add(_dateText);

        // 「今天」跳回：只在日期不是今天时出现（补记时是逃生口，睡前那档也一样）
        _todayBtn = new Button { Content = "今天", Visibility = Visibility.Collapsed };
        Interactions.ApplyStyle(_todayBtn, Interactions.ButtonText);
        _todayBtn.Click += (_, _) => JumpToToday();
        jump.Children.Add(_todayBtn);

        // 这颗按钮的 Content 必须是精确的「改时间」——自检按这个字符串找它
        var change = new Button { Content = "改时间", VerticalAlignment = VerticalAlignment.Center };
        Interactions.ApplyStyle(change, Interactions.ButtonText);
        change.Click += (_, _) =>
        {
            _expandOn = !_expandOn;
            _expand.Visibility = _expandOn ? Visibility.Visible : Visibility.Collapsed;
        };
        jump.Children.Add(change);

        Grid.SetColumn(jump, 1);
        row2.Children.Add(jump);
        col.Children.Add(row2);

        shell.Child = col;
        UpdateHeader();
        return shell;
    }

    /// <summary>
    /// 早 / 中 / 晚：下划线 tab（不再是 chip）。
    /// 每个 tab 带该时段**全库实际条数**，数字取自 <see cref="StatsResult.SlotTotal"/>，
    /// 这里不另算一套口径 —— 统计页显示的同一批数必须和这里对得上。
    /// 指示条用 scaleX 长出来（<see cref="Motion.ScaleX"/>），不动 Width 所以不重排。
    /// </summary>
    private UIElement SlotTabs()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

        foreach (var s in Slots)
        {
            var cell = new Grid { Margin = new Thickness(0, 0, 18, 0) };
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = s,
                FontFamily = M.Sans,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var count = new TextBlock
            {
                Text = "",
                FontFamily = M.Mono,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 1),
            };
            Tone.SetTabular(count, true);

            var text = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            text.Children.Add(label);
            text.Children.Add(count);

            var b = new Button
            {
                Content = text,
                Padding = new Thickness(1, 3, 1, 5),
                Cursor = Cursors.Hand,
            };
            Interactions.ApplyStyle(b, Interactions.Tab);
            var ss = s;
            b.Click += (_, _) => SetSlot(ss);
            Grid.SetRow(b, 0);
            cell.Children.Add(b);

            var ind = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ind.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            Grid.SetRow(ind, 1);
            cell.Children.Add(ind);

            _tabs.Add(new TabRef { Slot = s, Btn = b, Count = count, Ind = ind });
            row.Children.Add(cell);
        }

        PaintTabs();
        return row;
    }

    private void PaintTabs()
    {
        foreach (var t in _tabs)
        {
            var on = t.Slot == _slot;
            // 「哪一个是当前」交给样式触发器（TabHost.IsCurrent），代码不写 Foreground：
            // 本地值优先级高于 IsMouseOver 触发器，而且从此不跟配色换。
            TabHost.SetIsCurrent(t.Btn, on);
            Motion.ScaleX(t.Ind, on ? 1.0 : 0.0);
            var n = _tabTotals.TryGetValue(t.Slot, out var v) ? v : 0;
            t.Count.Text = " " + n;
            t.Count.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.AccentDeep : Theme.Ink3);
            t.Btn.ToolTip = $"{t.Slot}时段 · 全库 {n} 条";
        }
    }

    private void UpdateHeader()
    {
        var week = "周" + "日一二三四五六"[(int)Core.Stats.DateOf(_date).DayOfWeek];
        _timeText.Text = _time;

        // 睡前那档的日期天然就是昨天，但它不是「补记」——那是漏记的另一种说法，
        // 而这是规则本身：写「昨天」，含义交给旁边那枚归属 chip 说。
        _dayText.Text = SameDay(_date)
            ? $"{week} · 今天"
            : _preSleep
                ? $"{week} · 昨天"
                : $"{week} · {_date}（补记）";

        _dateText.Text = _date;
        _attribChip.Visibility = _attribution.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _attribText.Text = _attribution;
        _todayBtn.Visibility = SameDay(_date) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void JumpToToday()
    {
        _date = Stats0.Today();
        _attribution = "";       // 已经不再是"打开时的默认归属"了
        _preSleep = false;
        _datePicker.SelectedDate = Core.Stats.DateOf(_date);
        UpdateHeader();
        PourDay();
        BuildBody();
        RefreshFooter();
    }

    /* ---------- 日期 + 时间（折叠，默认隐藏） ---------- */

    private UIElement WhenBar(out FrameworkElement expand)
    {
        var wrap = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 4),
            Visibility = Visibility.Collapsed,
        };
        expand = wrap;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(96, GridUnitType.Pixel) });

        _datePicker = new DatePicker
        {
            SelectedDate = Core.Stats.DateOf(_date),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
        };
        _datePicker.SelectedDateChanged += (_, _) =>
        {
            if (_datePicker.SelectedDate is not { } sel) return;
            var picked = Core.Stats.Iso(sel);
            // 记录的是已发生的服用，不能记未来；选到未来直接弹回今天。
            // 「凌晨归属」把默认日期改成昨天，也不构成绕过：上限仍是今天。
            if (string.CompareOrdinal(picked, Stats0.Today()) > 0)
            {
                picked = Stats0.Today();
                _datePicker.SelectedDate = Core.Stats.DateOf(picked);
            }
            _date = picked;
            // 用户自己选的日期：归属标注与"睡前"这层含义到此为止
            _attribution = "";
            _preSleep = false;
            UpdateHeader();
            PourDay();
            BuildBody();
            RefreshFooter();
        };
        row.Children.Add(_datePicker);

        _timeBox = new TextBox
        {
            Text = _time,
            FontFamily = M.Mono,
            FontSize = 13,
            Padding = new Thickness(6, 5, 6, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        _timeBox.SetResourceReference(BackgroundProperty, Theme.Surface);
        _timeBox.SetResourceReference(Control.ForegroundProperty, Theme.Ink);
        _timeBox.SetResourceReference(Control.BorderBrushProperty, Theme.LineStrong);
        _timeBox.LostFocus += (_, _) => CommitTime();
        Grid.SetColumn(_timeBox, 1);
        row.Children.Add(_timeBox);
        wrap.Children.Add(row);

        // 时间步进：↑↓ 调分钟，避免裸敲 HH:mm
        var step = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        step.Children.Add(StepBtn("−5 分", () => NudgeMinute(-5), 64));
        step.Children.Add(StepBtn("+5 分", () => NudgeMinute(5), 64));
        step.Children.Add(Text(
            $"默认时段「{_slot}」由此刻的时间推出来；改时间会带着改它",
            M.Sans, 10.5, Theme.Ink3, va: VerticalAlignment.Center, margin: new Thickness(6, 0, 0, 0)));
        wrap.Children.Add(step);

        return wrap;
    }

    private void CommitTime()
    {
        var v = _timeBox.Text.Trim();
        if (!Stats0.TryParseTime(v, out var norm)) { _timeBox.Text = _time; return; }
        _time = norm;
        _timeBox.Text = _time;
        var ns = SlotOf(_time);
        // 手动改过时间：凌晨那两档的"默认归属"不再成立，撤掉标注（时段本身照样跟着时间走）
        if (ns != _slot || _attribution.Length > 0) { _attribution = ""; _preSleep = false; }
        if (ns != _slot)
        {
            _slot = ns;
            PaintTabs();
        }
        UpdateHeader();
        BuildBody();
    }

    private void NudgeMinute(int delta)
    {
        var parts = _time.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
            return;
        var total = h * 60 + m + delta;
        if (total < 0) total = 0;
        if (total > 24 * 60 - 1) total = 24 * 60 - 1;
        _time = $"{total / 60:00}:{total % 60:00}";
        _timeBox.Text = _time;
        var ns = SlotOf(_time);
        if (ns != _slot || _attribution.Length > 0) { _attribution = ""; _preSleep = false; }
        if (ns != _slot) { _slot = ns; PaintTabs(); }
        UpdateHeader();
        BuildBody();
    }

    private void SetSlot(string s)
    {
        if (_slot == s) return;
        _slot = s;
        // 时段是人自己挑的了，凌晨那两档的默认标注随之失效（日期不动，写入仍只认 _slot）
        _attribution = "";
        _preSleep = false;
        PaintTabs();
        UpdateHeader();
        BuildBody();
    }

    /* ---------- 主体：常吃行（键帽角标 + 品名 + 副行 + 步进器） ---------- */

    private void BuildBody()
    {
        _body.Children.Clear();
        _chips.Clear();
        _often.Children.Clear();

        RefreshStatsSnapshot();
        PaintTabs();

        _oftenOrder = Stats.RecentItems(_host.Db, slot: _slot, limit: 8);

        _body.Children.Add(OftenLabel());
        _body.Children.Add(OftenHint());
        for (var i = 0; i < _oftenOrder.Count; i++)
            _often.Children.Add(Chip(_oftenOrder[i], i + 1));
        _body.Children.Add(_often);

        // 其余口服收进折叠区
        var often = new HashSet<string>(_oftenOrder);
        var others = Items.All
            .Where(i => i.Route == "oral" && !often.Contains(i.Id))
            .Select(i => i.Id)
            .ToList();
        if (others.Count > 0)
            _body.Children.Add(Collapsible($"其余口服（{Items.All.Count(i => i.Route == "oral")}）", others));

        // 外用单独一行，默认折叠，不进口服统计语义
        var topicals = Items.All.Where(i => i.Route == "topical").Select(i => i.Id).ToList();
        if (topicals.Count > 0)
            _body.Children.Add(Collapsible($"外用（{topicals.Count}）", topicals));

        RefreshPrimary();
    }

    /// <summary>时段条数与每品类各时段条数：都从 Stats.Compute 取，不在弹层里另算。</summary>
    private void RefreshStatsSnapshot()
    {
        var st = Core.Stats.Compute(_host.Db);
        _tabTotals = new Dictionary<string, int>(st.SlotTotal);
        _itemSlots = new Dictionary<string, Dictionary<string, int>>();
        foreach (var i in st.Items) _itemSlots[i.ItemId] = i.Slots;
    }

    /// <summary>
    /// 时段条数就地 ±1。Tap / Dec 不整树重建（局部刷新是这一版的前提），
    /// 但 tab 上那个数必须跟着真正落盘的条数走；下一次 BuildBody 会用 Stats 整体重算兜住。
    /// </summary>
    private void BumpTotal(string slot, int delta)
    {
        var cur = _tabTotals.TryGetValue(slot, out var v) ? v : 0;
        var next = cur + delta;
        _tabTotals[slot] = next < 0 ? 0 : next;
        PaintTabs();
    }

    /// <summary>常吃区的表头行：常吃 + 依据（近 14 天、同时段）+ 键帽范围。</summary>
    private UIElement OftenLabel()
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        g.Children.Add(Text("常吃", M.Sans, 10.5, Theme.Ink3, FontWeights.SemiBold));

        // 「近 14 天」不是装饰：RecentItems 的窗口就是 withinDays:14，且只按当前时段排
        var why = Text($"近 14 天 · 只按「{_slot}」时段排序", M.Serif, 11, Theme.Ink3,
            margin: new Thickness(8, 0, 0, 0), italic: true);
        Grid.SetColumn(why, 1);
        g.Children.Add(why);

        var n = _oftenOrder.Count;
        var caps = Text(n > 0 ? $"{n} 种 · 1–{n}" : "0 种", M.Mono, 10.5, Theme.Ink3);
        Grid.SetColumn(caps, 2);
        g.Children.Add(caps);
        return g;
    }

    /// <summary>
    /// 步进器语义写在行上，不再藏在 tooltip 里（台账 P1：快捷键与累加规则都不可见）。
    /// </summary>
    private UIElement OftenHint() => Text(
        "连点 ＋ 就是往同一条上累加（不另生新条）· 点 − 直接减，减到 0 删掉整条 · 数字键 1 起对应每行角标",
        M.Sans, 10.5, Theme.Ink3, wrap: true, lineHeight: 15, margin: new Thickness(0, 0, 0, 8));

    /// <summary>
    /// 一个品类一行。所有行都是同一套：键帽角标 + 宋体品名 + 副行真实文案 + [−] N [＋]。
    /// 未记：整行 Surface、− 禁用、＋ 描边；已记：整行 AccentSoft、＋ 变实心。
    /// </summary>
    private UIElement Chip(string itemId, int keycap)
    {
        var it = Items.Get(itemId);
        if (it is null) return new Border();

        var name = new TextBlock
        {
            Text = it.Name,
            FontFamily = M.Serif,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var sub = new TextBlock
        {
            FontFamily = M.Sans,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(sub);

        // − / ＋ 同样大、同样显眼，始终并排，直接可用
        var minus = RoundBtn("−", () => Dec(itemId, _slot));
        minus.Tag = "dec|" + itemId;
        var count = new TextBlock
        {
            FontFamily = M.Mono,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Width = 30,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Tone.SetTabular(count, true);
        count.Tag = "cnt|" + itemId;
        var plus = RoundBtn("＋", () => Tap(itemId, _slot));
        plus.Tag = "inc|" + itemId;

        var ctrl = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ctrl.Children.Add(minus);
        ctrl.Children.Add(count);
        ctrl.Children.Add(plus);

        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (keycap > 0)
        {
            var kb = Keycap(keycap.ToString());
            kb.ToolTip = $"快捷键 {keycap}：记一份 {it.Name}（{_slot}）";
            row.Children.Add(kb);
        }

        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        Grid.SetColumn(ctrl, 2);
        row.Children.Add(ctrl);

        var border = new Border
        {
            CornerRadius = M.RSm,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 0, 7),
            Child = row,
            // 这枚 tooltip 是「记到当前时段那一列」断言的取数处：格式必须是 品名（时段），
            // 里面不能再出现第二个括号，也不能写别的时段名。
            ToolTip = $"{it.Name}（{_slot}）",
            Effect = M.CardShadow(),
        };
        border.SetResourceReference(Border.BorderBrushProperty, Theme.LineSoft);
        Hover.SetBaseKey(border, Theme.Surface);
        Hover.SetHoverKey(border, Theme.Surface2);
        Hover.SetShift(border, true);

        var c = new ChipRef
        {
            ItemId = itemId,
            Row = border,
            Name = name,
            Sub = sub,
            Minus = minus,
            Count = count,
            Plus = plus,
        };
        if (!_chips.ContainsKey(itemId)) _chips[itemId] = new List<ChipRef>();
        _chips[itemId].Add(c);
        UpdateChip(c);
        return border;
    }

    private static Border Keycap(string label)
    {
        var t = new TextBlock
        {
            Text = label,
            FontFamily = M.Mono,
            FontSize = 9.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        var b = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = M.RXs,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = t,
        };
        b.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        return b;
    }

    /// <summary>按当前 _slot 的已记量刷新一行：0 = 灰显且 − 禁用，&gt;0 = 整行 AccentSoft、＋ 实心。</summary>
    private void UpdateChip(ChipRef c)
    {
        var key = c.ItemId + "|" + _slot;
        var n = _counts.TryGetValue(key, out var v) ? v : 0;
        var on = n > 0;

        c.Count.Text = on ? n.ToString() : "–";      // 没记是「–」，不是 0：0 会被读成"吃了 0 片"
        c.Count.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.AccentDeep : Theme.Ink3);
        c.Name.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.Ink : Theme.Ink2);
        c.Sub.Text = SubLine(c.ItemId, n);
        c.Sub.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.AccentDeep : Theme.Ink3);

        // 整行转 AccentSoft：底色键交给 Hover 重挂（换键会让它重新 Attach，不会串色）
        Hover.SetBaseKey(c.Row, on ? Theme.AccentSoft : Theme.Surface);
        Hover.SetHoverKey(c.Row, on ? Theme.AccentSoft : Theme.Surface2);
        c.Row.SetResourceReference(Border.BorderBrushProperty, on ? Theme.AccentSoft : Theme.LineSoft);

        // ＋ 变实心：换的是样式键，不是往元素上写死画刷
        Interactions.ApplyStyle(c.Plus, on ? Interactions.Button : Interactions.ButtonSecondary);
        c.Minus.Opacity = on ? 1.0 : 0.5;
        c.Minus.IsEnabled = on;   // 已经 0 了不能再减
        c.Plus.IsEnabled = true;  // 任何状态都能加（记第一份 / 继续加）
    }

    /// <summary>
    /// 副行文案。**每个数都来自库里真实的条数**（Stats.ItemStat.Slots），
    /// 取不到就不报数，改用有依据的说法，绝不编一个数字上去。
    /// </summary>
    private string SubLine(string itemId, int today)
    {
        var unit = Items.Get(itemId)?.Unit ?? "份";
        if (today > 0) return $"今天已记 {today} {unit} · 连点即累加";

        var per = _itemSlots.TryGetValue(itemId, out var d) ? d : null;
        var all = per is null ? 0 : per.Values.Sum();
        if (all == 0) return "从没记过 · 点 ＋ 记第一份";

        var inSlot = per!.TryGetValue(_slot, out var n) ? n : 0;
        if (inSlot > 0) return $"这个时段 {inSlot} 次";

        var best = Slots.OrderByDescending(s => per.TryGetValue(s, out var v) ? v : 0).First();
        return $"多记在{best} · 这个时段还没记过";
    }

    private void RefreshChip(string itemId)
    {
        if (_chips.TryGetValue(itemId, out var list))
            foreach (var c in list) UpdateChip(c);
    }

    /* ---------- 动作 ---------- */

    // ＋ 调 Tap（加 1），− 调 Dec（直接减 1，归零删整条）。
    // 加减都是显式按钮，符合药品/补剂类 App 惯例，不再有「点格子切换 +1/−1」那种隐式语义。

    /// <summary>点 ＋：该格还没有事件就新建（dose=1），已有就改同一条（dose+1）。</summary>
    private void Tap(string itemId, string slot)
    {
        var key = itemId + "|" + slot;
        if (_cell.TryGetValue(key, out var id))
        {
            var ev = _host.Db.Events.FirstOrDefault(x => x.Id == id);
            var d = ev?.Dose ?? 1;
            // slot 必须透传：Store.UpdateEvent 会把 slot 字段原样覆写（Store.cs:339），
            // 传 null 会把这条记录原本的时段洗掉（设计上点的是哪一列就记哪列）。
            _host.UpdateEvent(id, null, null, d + 1, slot);
            _counts[key] = d + 1;
            _undo.Add((id, false));
        }
        else
        {
            var eid = Guid.NewGuid().ToString("N")[..12];
            _host.AddEvent(eid, itemId,
                new When { Date = _date, Time = _time, Precision = "minute" }, 1, slot);
            _cell[key] = eid;
            _counts[key] = 1;
            _undo.Add((eid, true));
            BumpTotal(slot, +1);      // 新建一条 = 该时段全库条数 +1（BuildBody 会整体重算）
        }
        _lastItem = itemId;
        RefreshChip(itemId);
        RefreshPrimary();
        RefreshFooter();
        ShowToast("已记", $"{Items.NameOf(itemId)} ×{_counts[key]} · {slot}");
    }

    private void Dec(string itemId, string slot)
    {
        var key = itemId + "|" + slot;
        if (!_cell.TryGetValue(key, out var id)) return;
        var ev = _host.Db.Events.FirstOrDefault(x => x.Id == id);
        var d = (ev?.Dose) ?? (_counts.TryGetValue(key, out var c) ? c : 1);
        if (d <= 1)
        {
            _host.RemoveEvent(id);
            _cell.Remove(key);
            _counts.Remove(key);
            BumpTotal(slot, -1);
        }
        else
        {
            // 同上：slot 透传，别让减一次份数把时段顺手洗掉
            _host.UpdateEvent(id, null, null, d - 1, slot);
            _counts[key] = d - 1;
        }
        _lastItem = itemId;
        RefreshChip(itemId);
        RefreshPrimary();
        RefreshFooter();
        var n = _counts.TryGetValue(key, out var v) ? v : 0;
        ShowToast(n == 0 ? "已删" : "已记",
            n == 0 ? $"{Items.NameOf(itemId)} · {slot}" : $"{Items.NameOf(itemId)} ×{n} · {slot}");
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var (id, created) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        if (created)
        {
            _host.RemoveEvent(id);
        }
        else
        {
            var ev = _host.Db.Events.FirstOrDefault(x => x.Id == id);
            var d = ev?.Dose ?? 1;
            // 撤销一次编辑 = 把该格剂量减 1；slot 透传，避免把时段洗掉。
            if (d <= 1) _host.RemoveEvent(id);
            else _host.UpdateEvent(id, null, null, d - 1, ev?.Slot);
        }
        // 撤销后从库里重新同步当天格映射，避免引用到已删的事件
        PourDay();
        BuildBody();
        RefreshFooter();
        ShowToast("已撤销", Items.NameOf(_lastItem ?? ""));
    }

    /// <summary>把当前日期下已有的记录灌进 _cell / _counts。</summary>
    private void PourDay()
    {
        _cell.Clear();
        _counts.Clear();
        foreach (var e in _host.Db.Events.Where(e => e.When.Date == _date))
        {
            var key = e.ItemId + "|" + e.Slot;
            if (!_cell.ContainsKey(key)) _cell[key] = e.Id;
            _counts[key] = (_counts.TryGetValue(key, out var c) ? c : 0) + (e.Dose ?? 1);
        }
    }

    /* ---------- 底部：本次已记 + 三档动作 ---------- */

    private UIElement Footer(out TextBlock summary, out Button undo)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 8, 16, 10),
            Effect = M.FloatShadow(),
        };
        border.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        border.SetResourceReference(Border.BorderBrushProperty, Theme.Line);

        var col = new StackPanel();

        // —— 第 1 行：本次已记 + 撤销（文字级） ——
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        summary = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink2);
        row.Children.Add(summary);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // 这颗按钮的 Content 必须是精确的「撤销」——自检按这个字符串找它
        undo = new Button { Content = "撤销", VerticalAlignment = VerticalAlignment.Center };
        Interactions.ApplyStyle(undo, Interactions.ButtonText);
        undo.Click += (_, _) => Undo();
        right.Children.Add(undo);
        right.Children.Add(Text("Ctrl+Z", M.Mono, 9.5, Theme.Ink3,
            va: VerticalAlignment.Center, margin: new Thickness(6, 1, 0, 0)));
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        col.Children.Add(row);

        // —— 第 2 行：次级 + **唯一一个**主操作 ——
        var acts = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        acts.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

        var obs = new Button { Content = "记一条体感", Height = 40 };
        Interactions.ApplyStyle(obs, Interactions.ButtonSecondary);
        obs.Click += (_, _) => ToggleObs();
        acts.Children.Add(obs);

        var plus = new TextBlock
        {
            Text = "＋",
            FontFamily = M.Mono,
            FontSize = 16,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var label = new TextBlock
        {
            FontFamily = M.Serif,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _primaryLabel = label;
        var kbd = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = M.RXs,
            Padding = new Thickness(5, 1, 5, 1),
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Enter",
                FontFamily = M.Mono,
                FontSize = 10,
            },
        };
        kbd.SetResourceReference(Border.BorderBrushProperty, Theme.OnAccent);
        ((TextBlock)kbd.Child).SetResourceReference(TextBlock.ForegroundProperty, Theme.OnAccent);

        var pg = new Grid();
        pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pg.Children.Add(plus);
        Grid.SetColumn(label, 1);
        pg.Children.Add(label);
        Grid.SetColumn(kbd, 2);
        pg.Children.Add(kbd);

        _primaryBtn = new Button
        {
            Content = pg,
            Height = 40,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Interactions.ApplyStyle(_primaryBtn, Interactions.Cta);
        _primaryBtn.Click += (_, _) =>
        {
            var target = _lastItem ?? (_oftenOrder.Count > 0 ? _oftenOrder[0] : null);
            if (target is not null) Tap(target, _slot);
        };
        Grid.SetColumn(_primaryBtn, 1);
        acts.Children.Add(_primaryBtn);

        col.Children.Add(acts);
        border.Child = col;
        return border;
    }

    /// <summary>
    /// 主操作的文案。它做的事和写出来的一样：记一份当前时段的某个品类
    /// —— 刚记过就「再记」那一项（连点累加），还没记过就常吃第 1 项。
    /// 弹层没有草稿状态，所以这里不存在"提交"这种假动作。
    /// </summary>
    private void RefreshPrimary()
    {
        var target = _lastItem ?? (_oftenOrder.Count > 0 ? _oftenOrder[0] : null);
        if (target is null)
        {
            _primaryLabel.Text = "先点上面的 ＋";
            _primaryBtn.IsEnabled = false;
            _primaryBtn.ToolTip = "常吃区还是空的，先展开「其余口服」记一份";
            return;
        }
        var name = Items.NameOf(target);
        _primaryBtn.IsEnabled = true;
        _primaryLabel.Text = _lastItem is null ? $"记 {name}" : $"再记 {name}";
        _primaryBtn.ToolTip = $"记一份 {name}（{_slot}）· 与点这一行的 ＋ 完全相同（Enter）";
    }

    private void RefreshFooter()
    {
        _summary.Text = _counts.Count == 0
            ? "还没记"
            : "今天已记 " + string.Join("、", _counts.Select(kv =>
                $"{Items.NameOf(kv.Key.Split('|')[0])} ×{kv.Value}"));
        _undoBtn.IsEnabled = _undo.Count > 0;
    }

    /* ---------- 撤销条：3 秒免弹窗 ---------- */

    /// <summary>
    /// 「已记 X ×N · 时段」+ 撤销 + 3 秒进度。
    /// 保留免弹窗设计：这条会自己消失，不需要用户点掉，也不弹 MessageBox。
    /// 进度只动 ScaleTransform（布局属性一律不动）。
    /// </summary>
    private void ShowToast(string verb, string what)
    {
        HideToast();

        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 8, 10, 8),
        };
        inner.Children.Add(Text(verb + " ", M.Sans, 12, Theme.OnAccent,
            va: VerticalAlignment.Center));
        inner.Children.Add(Text(what, M.Serif, 12.5, Theme.OnAccent, FontWeights.SemiBold,
            va: VerticalAlignment.Center, trim: TextTrimming.CharacterEllipsis));

        var link = new Button
        {
            Content = "撤销",
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Interactions.ApplyStyle(link, Interactions.ButtonText);
        link.SetResourceReference(Control.ForegroundProperty, Theme.AccentSoft);
        link.Click += (_, _) => Undo();
        inner.Children.Add(link);

        // 3 秒进度：贴在条子底边的一根 2px 横线，从满宽缩到 0
        var bar = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0),
        };
        bar.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
        bar.RenderTransformOrigin = new Point(0, 0.5);
        var scale = new ScaleTransform(1, 1);
        bar.RenderTransform = scale;

        var box = new Grid();
        box.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        box.RowDefinitions.Add(new RowDefinition { Height = new(2, GridUnitType.Pixel) });
        var holder = new Border
        {
            CornerRadius = M.RSm,
            Padding = new Thickness(0),
            Effect = M.FloatShadow(),
            Child = box,
        };
        holder.SetResourceReference(Border.BackgroundProperty, Theme.Ink);
        box.Children.Add(inner);
        Grid.SetRow(bar, 1);
        box.Children.Add(bar);

        _toastHost.Child = holder;
        _toastHost.Margin = new Thickness(16, 2, 16, 6);

        if (Motion.Enabled)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(
                1.0, 0.0, TimeSpan.FromSeconds(3)));
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (_, _) => { _toastTimer!.Stop(); HideToast(); };
            _toastTimer.Start();
        }
        // 离屏渲染 / 自检（Motion.Enabled=false）没有消息循环推进时间线：
        // 条子留着显示成满进度，截图能拍到，也不会卡在自检里。
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        _toastTimer = null;
        _toastHost.Child = null;
        _toastHost.Margin = new Thickness(0);
    }

    /* ---------- 记一条体感（就地展开，不弹窗） ---------- */

    private void ToggleObs()
    {
        _obsOpen = !_obsOpen;
        _obsHint = false;
        RenderObsPanel();
    }

    private void RenderObsPanel()
    {
        _obsHost.Children.Clear();
        _obsHost.Visibility = _obsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!_obsOpen) return;

        var card = new Border
        {
            CornerRadius = M.RMd,
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        card.SetResourceReference(Border.BorderBrushProperty, Theme.LineSoft);

        var col = new StackPanel();

        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, name) in ObsKinds)
        {
            var on = _obsKind == id;
            var b = new Button { Content = name, Margin = new Thickness(0, 0, 5, 0) };
            Interactions.ApplyStyle(b, on ? Interactions.ChipOn : Interactions.Chip);
            var kk = id;
            b.Click += (_, _) => { _obsKind = kk; RenderObsPanel(); };
            kinds.Children.Add(b);
        }
        col.Children.Add(kinds);

        var subj = new TextBox
        {
            Text = _obsSubject,
            FontFamily = M.Sans,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1),
        };
        subj.SetResourceReference(BackgroundProperty, Theme.Paper);
        subj.SetResourceReference(Control.ForegroundProperty, Theme.Ink);
        subj.SetResourceReference(Control.BorderBrushProperty, Theme.LineStrong);
        subj.Tag = "obs-subject";
        subj.ToolTip = "部位或事情（必填）";
        subj.TextChanged += (_, _) => _obsSubject = subj.Text;
        col.Children.Add(subj);

        var note = new TextBox
        {
            Text = _obsNote,
            FontFamily = M.Sans,
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 0),
            AcceptsReturn = true,
            Height = 48,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
        };
        note.SetResourceReference(BackgroundProperty, Theme.Paper);
        note.SetResourceReference(Control.ForegroundProperty, Theme.Ink);
        note.SetResourceReference(Control.BorderBrushProperty, Theme.Line);
        note.Tag = "obs-note";
        note.ToolTip = "补充（可留空）";
        note.TextChanged += (_, _) => _obsNote = note.Text;
        col.Children.Add(note);

        if (_obsHint)
            col.Children.Add(Text("先写一下部位或事情。", M.Sans, 11.5, Theme.Danger,
                margin: new Thickness(1, 6, 0, 0)));

        var acts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new Button { Content = "记下" };
        Interactions.ApplyStyle(ok, Interactions.ButtonSecondary);
        ok.Click += (_, _) =>
        {
            var s = (_obsSubject ?? "").Trim();
            if (s.Length == 0)
            {
                _obsHint = true;      // 行内提示，不弹窗
                RenderObsPanel();
                return;
            }
            _host.AddObservation(_obsKind, s, _obsNote);
            _obsSubject = "";
            _obsNote = "";
            _obsHint = false;
            _obsOpen = false;
            RenderObsPanel();
            ShowToast("已记体感", s);
        };
        acts.Children.Add(ok);

        var cancel = new Button { Content = "取消", Margin = new Thickness(6, 0, 0, 0) };
        Interactions.ApplyStyle(cancel, Interactions.ButtonText);
        cancel.Click += (_, _) => { _obsOpen = false; _obsHint = false; RenderObsPanel(); };
        acts.Children.Add(cancel);

        col.Children.Add(acts);
        card.Child = col;
        _obsHost.Children.Add(card);
    }

    /* ---------- 小工具 ---------- */

    private UIElement Collapsible(string title, List<string> ids)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        foreach (var id in ids) wrap.Children.Add(Chip(id, 0));

        var btn = new Button
        {
            Content = "▸ " + title,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(1),
        };
        Interactions.ApplyStyle(btn, Interactions.ButtonSecondary);
        btn.SetResourceReference(Control.BackgroundProperty, Theme.Paper);
        var shown = false;
        btn.Click += (_, _) =>
        {
            shown = !shown;
            wrap.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            btn.Content = (shown ? "▾ " : "▸ ") + title;
        };

        var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        sp.Children.Add(btn);
        sp.Children.Add(wrap);
        return sp;
    }

    private static Button RoundBtn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            FontFamily = M.Mono,
            FontSize = 15,
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Interactions.SetRadius(b, new CornerRadius(13));
        Interactions.ApplyStyle(b, Interactions.ButtonSecondary);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button StepBtn(string text, Action onClick, double width)
    {
        var b = new Button
        {
            Content = text,
            Width = width,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Interactions.ApplyStyle(b, Interactions.ButtonSecondary);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static TextBlock Text(string text, FontFamily fam, double size, string colorKey,
        FontWeight? weight = null, Thickness? margin = null, VerticalAlignment? va = null,
        bool italic = false, TextTrimming trim = TextTrimming.None, bool wrap = false,
        double lineHeight = double.NaN)
    {
        var t = new TextBlock { Text = text, FontFamily = fam, FontSize = size };
        if (weight is { } w) t.FontWeight = w;
        if (margin is { } m) t.Margin = m;
        if (va is { } v) t.VerticalAlignment = v;
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

    /// <summary>
    /// 打开那一刻的默认时段。凌晨两档是产品决策（见 <see cref="PreSleepUntilHour"/>），
    /// 06:00 起仍是原来的 <see cref="SlotOf"/>。
    /// </summary>
    private static string DefaultSlotOf(int hour, string time)
    {
        if (hour < PreSleepUntilHour) return "晚";
        if (hour < EarlyMorningUntilHour) return "早";
        return SlotOf(time);
    }

    /// <summary>表头那枚标注。只在打开时算一次，改过日期/时间/时段就撤。</summary>
    private static string DefaultAttribution(int hour)
    {
        if (hour < PreSleepUntilHour) return "睡前 · 归昨天";
        if (hour < EarlyMorningUntilHour) return "凌晨 · 归今天";
        return "";
    }

    /// <summary>时段由时间决定：11 点前算早，11-16 点算中，之后算晚。这条阈值没动。</summary>
    private static string SlotOf(string time)
    {
        var parts = time.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var h)) return "早";
        return h < 11 ? "早" : h < 16 ? "中" : "晚";
    }

    private static bool SameDay(string date) =>
        string.CompareOrdinal(date, Stats0.Today()) == 0;

    private sealed class TabRef
    {
        public string Slot = "";
        public Button Btn = null!;
        public TextBlock Count = null!;
        public Border Ind = null!;
    }

    private sealed class ChipRef
    {
        public string ItemId = "";
        public Border Row = null!;
        public TextBlock Name = null!;
        public TextBlock Sub = null!;
        public Button Minus = null!;
        public TextBlock Count = null!;
        public Button Plus = null!;
    }
}

/// <summary>日期小工具。Window 里已有同名成员，单独放避免冲突。</summary>
internal static class Stats0
{
    /// <summary>
    /// 今天。**必须走 RecordSheet.NowSafe**：自检注入过时刻之后，
    /// 「日期不能选到未来」的上限校验和 SameDay 的「今天」要用同一个"今天"，
    /// 否则凌晨归属那两档会把校验和标注各自认一个日子。
    /// </summary>
    public static string Today() => RecordSheet.NowSafe.ToString("yyyy-MM-dd");

    public static string Hhmmss(DateTime dt) => dt.ToString("HH:mm");

    public static string Iso(DateTime d) => Core.Stats.Iso(d);

    public static DateTime DateOf(string iso) => Core.Stats.DateOf(iso);

    /// <summary>只接受 HH:mm 或 HH：mm（全角冒号也要），非法返回 false。</summary>
    public static bool TryParseTime(string s, out string norm)
    {
        norm = "";
        s = s.Trim().Replace('：', ':');
        var parts = s.Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        norm = $"{h:00}:{m:00}";
        return true;
    }
}
