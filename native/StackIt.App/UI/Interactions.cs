using System.Windows.Threading;         // DispatcherTimer（hover 淡出后摘阴影层）
using System.ComponentModel;    // DependencyPropertyDescriptor
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace StackIt.App.UI;

/// <summary>
/// 交互层：hover / pressed / 键盘 focus 三种状态，以及承载它们的动画。
///
/// 为什么必须有这个文件：改之前全项目 IsMouseOver ×0、Triggers ×0、
/// FocusVisualStyle ×0、Storyboard ×0、DoubleAnimation ×0 —— 只有 Cursor=Hand。
/// "看着像能点"和"点了知道点着了"的差别全在这几态上。
///
/// 两条硬规矩（设计台账定的，不是随手写的）：
///
/// 1. **只用 Transform 和 Opacity 做动画。** 动 Width/Height/Margin/Padding 会
///    触发 measure/arrange；统计页与时间流一页上千个元素，一次 hover 引发整页
///    重排就是卡顿。位移用 TranslateTransform，缩放用 ScaleTransform，
///    阴影加深用 Effect.Opacity（阴影对象常驻，只动它的透明度）。
/// 2. **底色阶跃，不渐变。** Surface → Surface2 是"这一行被指头碰到了"的信号，
///    换画刷不触发布局，所以直接切；要做过渡的是位置、阴影、缩放。
///
/// 颜色一律走 DynamicResource（这里用 SetResourceReference，它就是
/// {DynamicResource} 的程序等价物），所以切强调色时交互态跟着一起变。
///
/// 公开 API（后续批次直接用）：
///   ui:Hover.Shift="True"                行/卡片开 hover 抬起
///   ui:Hover.BaseKey / ui:Hover.HoverKey 常态底 / hover 底的资源键
///   ui:Focus.Ring="True"                 键盘 focus 外环
///   ui:TabHost.IsCurrent="True"          页签「当前项」状态
///   ui:Tone.SetTabular                   等宽数字（在 Theme.cs）
///   Interactions.Button / ButtonSecondary / ButtonText / ButtonDanger
///   Interactions.Chip / ChipOn / Card / Cta / Tab / Radius   样式键与圆角
///   Interactions.StyleOf(key) / ApplyStyle(el, key)          代码里套样式
///   Motion.ShiftY / Scale / ScaleX / Shadow                  动画原语
/// </summary>
public static class Hover
{
    /// <summary>hover 抬起：底色 Surface→Surface2、上移 1px、阴影加深；pressed 缩到 0.985。</summary>
    public static readonly DependencyProperty ShiftProperty =
        DependencyProperty.RegisterAttached("Shift", typeof(bool), typeof(Hover),
            new PropertyMetadata(false, OnShiftChanged));

    public static void SetShift(DependencyObject o, bool v) => o.SetValue(ShiftProperty, v);
    public static bool GetShift(DependencyObject o) => (bool)o.GetValue(ShiftProperty);

    /// <summary>
    /// 常态底的资源键，默认 Surface。
    ///
    /// 为什么要单独开这个键：挂 Shift 的元素底色未必是 Surface。不指明的话，
    /// 走上去和走开都往 Surface2 靠，颜色就串了。
    /// 实底按钮（主按钮/选中芯片）请走样式模板里的触发器，不要用 Shift。
    /// </summary>
    public static readonly DependencyProperty BaseKeyProperty =
        DependencyProperty.RegisterAttached("BaseKey", typeof(string), typeof(Hover),
            new PropertyMetadata(Theme.Surface, OnKeyChanged));

    public static void SetBaseKey(DependencyObject o, string v) => o.SetValue(BaseKeyProperty, v);
    public static string GetBaseKey(DependencyObject o) => (string)o.GetValue(BaseKeyProperty);

    /// <summary>hover 底的资源键，默认 Surface2。</summary>
    public static readonly DependencyProperty HoverKeyProperty =
        DependencyProperty.RegisterAttached("HoverKey", typeof(string), typeof(Hover),
            new PropertyMetadata(Theme.Surface2, OnKeyChanged));

    public static void SetHoverKey(DependencyObject o, string v) => o.SetValue(HoverKeyProperty, v);
    public static string GetHoverKey(DependencyObject o) => (string)o.GetValue(HoverKeyProperty);

    /// <summary>抬起的高度（设计稿 translateY(-1px)）。</summary>
    public const double LiftY = -1;

    /// <summary>按下的缩放（设计稿 scale(.985)）。</summary>
    public const double PressScale = 0.985;

    private static void OnShiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) State.Of(el).Attach(el);
        else State.Detach(el);
    }

    /// <summary>
    /// 换底色键时重挂一次。
    ///
    /// 为什么要管：Attach() 把常态底设成了**当时**的 BaseKey，而样式里
    /// "Shift=True" 与 "BaseKey=Paper" 谁先生效取决于 Setter 顺序。先 Shift、
    /// 后改 BaseKey 的话元素会留在旧键的底色上 —— 又是一次静默串色。
    /// </summary>
    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && GetShift(el))
            State.Of(el).Attach(el);   // Attach 开头自带 Release，重复调用安全
    }

    /// <summary>
    /// 每个元素一份状态。用 ConditionalWeakTable：页面每次 Refresh 都整树重建，
    /// 元素被拆掉时状态跟着一起消失，不会攒出一个只增不减的表。
    /// </summary>
    private sealed class State
    {
        private static readonly ConditionalWeakTable<FrameworkElement, State> Table = new();

        private FrameworkElement? _el;
        private DependencyProperty? _bg;
        private DropShadowEffect? _shadow;
        // _ownsShadow：这个 Effect 是为 hover 临时挂上去的，淡出后要摘掉。
        // 元素自带的 Effect 只「借来动画」，不摘。
        private bool _ownsShadow;
        private DispatcherTimer? _detach;
        private DependencyPropertyDescriptor? _hoverDesc;
        private DependencyPropertyDescriptor? _pressDesc;
        private EventHandler? _hoverHandler;
        private EventHandler? _pressHandler;
        private bool _hovering;
        private bool _pressed;

        public static State Of(FrameworkElement el) => Table.GetValue(el, _ => new State());

        public static void Detach(FrameworkElement el)
        {
            if (Table.TryGetValue(el, out var s)) s.Release(el);
        }

        public void Attach(FrameworkElement el)
        {
            if (_el != null) Release(el);   // 重复设 True / 换底色键都不重复挂

            _el = el;
            _bg = BackgroundDP(el);
            if (_bg != null) el.SetResourceReference(_bg, GetBaseKey(el));

            // 先把 [缩放, 位移] 那组 Transform 备好，动画才有东西可动
            _ = Motion.EnsureTransforms(el);

            // 静止时**不挂**阴影层。
            //
            // 之前是「常驻一个 opacity=0 的 DropShadowEffect，之后只动它的 Opacity」。
            // 坑在于：WPF 只要元素带着 Effect 就走位图特效管线，opacity=0 只是画出来
            // 看不见，光栅化那一趟照样跑 —— 列表几百行、每行一个，全摊在渲染线程上，
            // 而 TimingProbe 只量测量与排版，量不到它（所以「数据页 44ms 却感觉卡」）。
            // 现在：hover 才创建，淡出后摘掉，见 SetHover / ScheduleShadowDetach。
            // 元素本来就自带 Effect 的不动它 —— 那是设计要的投影，不是 hover 附加层。
            // （旧逻辑遇到冻结的自带 Effect 会直接把它换成阴影层，等于把卡片自己的
            // 投影弄丢了；现在改成宁可不动画也不抢。）
            switch (el.Effect)
            {
                case DropShadowEffect { IsFrozen: false } own:
                    _shadow = own;      // 自带且可动画：借来做淡入淡出，离开不摘
                    break;
                case DropShadowEffect:
                    break;              // 自带但冻结（共享实例）：往它身上动画会抛，不动
                default:
                    _ownsShadow = true; // 静止时 el.Effect 保持原样（通常是 null）
                    break;
            }

            // IsMouseOver / IsPressed 是**只读依赖属性**：WPF 里既没有对应的路由事件，
            // DependencyObject 也没有公开的 PropertyChanged 事件，只能走
            // DependencyPropertyDescriptor.AddValueChanged。
            // 不换成 MouseEnter/MouseLeave：元素一被按下 WPF 就捕获鼠标，
            // 捕获期间 MouseLeave 不触发 —— 状态会卡在 hover 上回不来。
            _hoverHandler = (_, _) => OnHoverChanged();
            _hoverDesc = DependencyPropertyDescriptor
                .FromProperty(UIElement.IsMouseOverProperty, typeof(UIElement));
            _hoverDesc?.AddValueChanged(el, _hoverHandler);

            if (el is ButtonBase)
            {
                _pressHandler = (_, _) => OnPressChanged();
                _pressDesc = DependencyPropertyDescriptor
                    .FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
                _pressDesc?.AddValueChanged(el, _pressHandler);
            }
            else
            {
                // 其余元素（Border 行 / 卡片）没有按下态，只能自己听鼠标键。
                el.MouseLeftButtonDown += OnDown;
                el.MouseLeftButtonUp += OnUp;
            }
        }

        private void Release(FrameworkElement el)
        {
            _detach?.Stop();
            _detach = null;
            _shadow = null;
            _ownsShadow = false;
            if (_hoverDesc != null && _hoverHandler != null)
                _hoverDesc.RemoveValueChanged(el, _hoverHandler);
            if (_pressDesc != null && _pressHandler != null)
                _pressDesc.RemoveValueChanged(el, _pressHandler);
            _hoverDesc = _pressDesc = null;
            _hoverHandler = _pressHandler = null;

            el.MouseLeftButtonDown -= OnDown;
            el.MouseLeftButtonUp -= OnUp;
            if (_bg != null) el.ClearValue(_bg);
            _el = null;
            _hovering = false;
            _pressed = false;
        }

        private void OnHoverChanged()
        {
            if (_el is null) return;
            SetHover((bool)_el.GetValue(UIElement.IsMouseOverProperty));
        }

        private void OnPressChanged()
        {
            if (_el is null) return;
            SetPress((bool)_el.GetValue(ButtonBase.IsPressedProperty));
        }

        private void OnDown(object? sender, MouseButtonEventArgs e) => SetPress(true);
        private void OnUp(object? sender, MouseButtonEventArgs e) => SetPress(false);

        private void SetHover(bool on)
        {
            if (_el is null || on == _hovering) return;
            _hovering = on;
            if (_bg != null)
                _el.SetResourceReference(_bg, on ? GetHoverKey(_el) : GetBaseKey(_el));
            Motion.ShiftY(_el, on ? LiftY : 0);
            if (on && _ownsShadow && _shadow is null)
            {
                var fx = M.ShadowLayer();
                _el.Effect = fx;
                _shadow = fx;
            }
            Motion.Shadow(_shadow, on ? 1.0 : 0.0);
            if (!on) ScheduleShadowDetach();
        }

        private void SetPress(bool on)
        {
            if (_el is null || on == _pressed) return;
            _pressed = on;
            Motion.Scale(_el, on ? PressScale : 1.0);
        }
        /// <summary>
        /// 淡出结束后把附加阴影层摘掉 —— 静止的元素不该带着 Effect。
        /// 用一次性 DispatcherTimer 而不是动画 Completed：动画是在 effect 对象上跑的，
        /// 摘除时机只要比动画稍晚一点就够，不必精确同步。
        /// </summary>
        private void ScheduleShadowDetach()
        {
            if (!_ownsShadow || _shadow is null) return;
            _detach ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Ms + 60) };
            _detach.Stop();
            _detach.Tick -= OnDetachTick;
            _detach.Tick += OnDetachTick;
            _detach.Start();
        }
        private void OnDetachTick(object? sender, EventArgs e)
        {
            _detach?.Stop();
            if (_hovering || _pressed || _el is null) return;   // 又移回来了，这轮不摘
            if (_ownsShadow && _shadow != null && ReferenceEquals(_el.Effect, _shadow))
                _el.ClearValue(UIElement.EffectProperty);
            _shadow = null;
        }

        /// <summary>
        /// 找这个元素的 Background 依赖属性。
        ///
        /// 为什么不能一律用 Control.BackgroundProperty：Border 不是 Control
        /// （台账行、卡片都是 Border），Panel 也不是。三者的 Background 是三个
        /// 不同的 DP，认错了要么 InvalidCastException 要么静默无效。
        /// </summary>
        internal static DependencyProperty? BackgroundDP(DependencyObject el) => el switch
        {
            Control => Control.BackgroundProperty,
            Border => Border.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            _ => null,
        };
    }
}

/// <summary>
/// 键盘 focus 环：2px Accent 外环 + 2px offset。
///
/// 用 WPF 的 FocusVisualStyle（走 AdornerLayer 画在元素外圈）而不是改
/// BorderBrush/BorderThickness：后者会把元素的测量尺寸撑大 4px，一按 Tab
/// 整行都在抖 —— 那是"动画触发布局"的另一种犯法形式；而且会跟现有描边
/// （次级按钮本来就有一圈 Line）打架，把描边直接吃掉。
///
/// WPF 只在**键盘导航**之后显示它（鼠标点击不显示），正是设计要的语义。
/// </summary>
public static class Focus
{
    public static readonly DependencyProperty RingProperty =
        DependencyProperty.RegisterAttached("Ring", typeof(bool), typeof(Focus),
            new PropertyMetadata(false, OnRingChanged));

    public static void SetRing(DependencyObject o, bool v) => o.SetValue(RingProperty, v);
    public static bool GetRing(DependencyObject o) => (bool)o.GetValue(RingProperty);

    /// <summary>样式表里那枚环的键名。</summary>
    public const string RingStyleKey = "StackItFocusRing";

    private static void OnRingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if (!(bool)e.NewValue)
        {
            el.ClearValue(FrameworkElement.FocusVisualStyleProperty);
            return;
        }

        // 环本体在 Interactions.xaml 里。那张表还没并进资源时（属性设得比
        // Install() 早）就在 Loaded 再补一次 —— 顺序不该决定有没有焦点框。
        if (Interactions.StyleOf(RingStyleKey) is { } s) el.FocusVisualStyle = s;
        else el.Loaded += (_, _) =>
        {
            if (Interactions.StyleOf(RingStyleKey) is { } t) el.FocusVisualStyle = t;
        };

        // 非 Control（Border 卡片/行）默认不可聚焦，环永远不出现，所以先让它可聚焦。
        // 注意 IsTabStop 只存在于 Control 上：Border 能被程序聚焦、但 Tab 键跳不进去。
        // 真需要 Tab 可达的行，请包一层 Control（或用 Button 当行）。
        if (el is not Control) el.Focusable = true;
    }
}

/// <summary>
/// 页签 / 分段控件的「当前项」状态。
///
/// 为什么不由代码直接写 Foreground / FontWeight：那是**本地值**，优先级高于
/// 样式里的 IsMouseOver 触发器 —— 当前页签会既看不到 hover 反应，那个写死的
/// 画刷从此也不跟着配色换。设成附加属性之后，代码只说"哪一个是当前"，
/// 剩下的交给样式触发器（见 Interactions.xaml 的 StackItTab）。
/// </summary>
public static class TabHost
{
    public static readonly DependencyProperty IsCurrentProperty =
        DependencyProperty.RegisterAttached("IsCurrent", typeof(bool), typeof(TabHost),
            new PropertyMetadata(false));

    public static void SetIsCurrent(DependencyObject o, bool v) => o.SetValue(IsCurrentProperty, v);
    public static bool GetIsCurrent(DependencyObject o) => (bool)o.GetValue(IsCurrentProperty);
}

/// <summary>
/// 动画原语。只有 Transform 与 Opacity 两种可动画量 —— 见 <see cref="Hover"/> 的说明。
///
/// <see cref="Enabled"/>：离屏渲染 / 自检模式下关掉。那些模式没有消息循环推进
/// 动画时间线，BeginAnimation 之后元素停在**起始值** —— 截图里页签指示条会
/// 永远是 scaleX 0，看着像指示根本没做。关掉之后直接落终值，
/// 测的仍是同一批属性、同一个状态。
/// </summary>
public static class Motion
{
    /// <summary>hover/pressed 过渡时长，对齐设计稿的 180ms（台账区间 160–240ms）。</summary>
    public const int Ms = 180;

    /// <summary>页签指示条这类"横向长出来"的过渡，设计稿 220ms。</summary>
    public const int MsSlow = 220;

    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// 近似 CSS cubic-bezier(.2,.8,.3,1)：起段快、尾段收得干净。
    /// WPF 没有三次贝塞尔缓动，CubicEase(EaseOut) 是最接近的一档。
    /// </summary>
    public static IEasingFunction Ease => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>指示条用的缓动：CircleEase 尾巴更黏，长出来那一下不那么机械。</summary>
    public static IEasingFunction EaseSlow =>
        new CircleEase { EasingMode = EasingMode.EaseOut };

    /// <summary>确保元素有一套 [缩放, 位移] 的 RenderTransform，并返回这两个变换。</summary>
    internal static (TranslateTransform, ScaleTransform) EnsureTransforms(FrameworkElement el)
    {
        // 原点取中心：缩放才是"原地压下去"，而不是往右下角斜
        el.RenderTransformOrigin = new Point(0.5, 0.5);

        if (el.RenderTransform is TransformGroup g)
        {
            var s = g.Children.OfType<ScaleTransform>().FirstOrDefault() ?? new ScaleTransform();
            var t = g.Children.OfType<TranslateTransform>().FirstOrDefault() ?? new TranslateTransform();
            if (!g.Children.Contains(s)) g.Children.Insert(0, s);
            if (!g.Children.Contains(t)) g.Children.Add(t);
            return (t, s);
        }

        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { scale, shift } };
        return (shift, scale);
    }

    /// <summary>位移（只动 Y，不动 Height/Margin）。</summary>
    public static void ShiftY(FrameworkElement el, double y, int ms = Ms)
    {
        var (shift, _) = EnsureTransforms(el);
        Animate(shift, TranslateTransform.YProperty, y, ms, Ease);
    }

    /// <summary>等比缩放（pressed 0.985）。</summary>
    public static void Scale(FrameworkElement el, double s, int ms = Ms)
    {
        var (_, scale) = EnsureTransforms(el);
        Animate(scale, ScaleTransform.ScaleXProperty, s, ms, Ease);
        Animate(scale, ScaleTransform.ScaleYProperty, s, ms, Ease);
    }

    /// <summary>
    /// 横向长出来（页签指示条 / 下划线）。不动 Width —— 那是布局属性。
    /// </summary>
    /// <param name="centerOrigin">true 从中间向两边长；false 从左边长（原点贴左）。</param>
    public static void ScaleX(FrameworkElement el, double x,
        int ms = MsSlow, bool centerOrigin = true)
    {
        var (_, scale) = EnsureTransforms(el);
        if (!centerOrigin) el.RenderTransformOrigin = new Point(0, 0.5);
        Animate(scale, ScaleTransform.ScaleXProperty, x, ms, EaseSlow);
    }

    /// <summary>阴影加深：动的是常驻阴影层的 Opacity，不是 Effect 本身。</summary>
    public static void Shadow(DropShadowEffect? fx, double opacity, int ms = Ms)
    {
        if (fx is null || fx.IsFrozen) return;
        Animate(fx, DropShadowEffect.OpacityProperty, opacity, ms, Ease);
    }

    // 参数类型用 Animatable：BeginAnimation 定义在 Animatable / UIElement 上，
    // DependencyObject 没有这个方法。三个动画目标（TranslateTransform、
    // ScaleTransform、DropShadowEffect）都派生自 Animatable，正好覆盖。
    private static void Animate(Animatable target, DependencyProperty dp,
        double to, int ms, IEasingFunction ease)
    {
        if (!Enabled)
        {
            // 直接落终值。用 SetCurrentValue 而不是 SetValue：后者会清掉继承/样式来源，
            // 自检里要的只是"这一帧渲染出终态"。
            target.SetCurrentValue(dp, to);
            return;
        }

        // From 取**当前生效值**（有动画时 GetValue 返回的就是动画此刻的值）。
        // 不写 From 的话 WPF 拿的是动画之前的基准值：每次数据刷新都会重跑一次
        // Highlight()，指示条就会先跳回 0 再长出来 —— 看着像闪。
        var from = target.GetValue(dp) is double cur ? cur : to;
        target.BeginAnimation(dp, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
        });
    }
}

/// <summary>
/// 样式表的装载入口与键名。样式本体在 <c>UI/Interactions.xaml</c>。
///
/// 为什么样式表单独一张 XAML，而不是写进 Application.Resources 或用代码造 Style：
/// Setter 的颜色必须是 **DynamicResource**（切强调色要整组跟着变），而
/// DynamicResource 是标记扩展，只能出现在 XAML 里 —— 代码里的等价物是
/// FrameworkElement.SetResourceReference，那是"设在元素上"，塞不进 Setter。
/// </summary>
public static class Interactions
{
    private static readonly Uri SheetUri = new(
        "pack://application:,,,/StackIt.App;component/UI/Interactions.xaml", UriKind.Absolute);

    /// <summary>
    /// 圆角档位，供样式覆盖模板里那个 Border 的 CornerRadius。
    ///
    /// 为什么要自己开一个附加属性：CornerRadius 是 Border 的属性，Control 上
    /// 没有，样式没法"透传"给模板；模板里用 Binding + RelativeSource TemplatedParent
    /// 读它（TemplateBinding 只认模板目标类型上存在的属性）。
    /// </summary>
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached("Radius", typeof(CornerRadius), typeof(Interactions),
            new PropertyMetadata(M.RSm));

    public static void SetRadius(DependencyObject o, CornerRadius v) => o.SetValue(RadiusProperty, v);
    public static CornerRadius GetRadius(DependencyObject o) => (CornerRadius)o.GetValue(RadiusProperty);

    /// <summary>把样式表并进 Application.Resources。幂等，重复调用只装一次。</summary>
    public static void Install()
    {
        var res = Application.Current.Resources;
        foreach (ResourceDictionary d in res.MergedDictionaries)
            if (d.Source == SheetUri) return;
        res.MergedDictionaries.Add(new ResourceDictionary { Source = SheetUri });
    }

    // —— 样式键名。Views/ 里用这些键套样式 ——
    /// <summary>主按钮：AccentDeep 实底 + OnAccent 字。一屏最多一个。</summary>
    public const string Button = "StackItButton";
    /// <summary>次按钮：Surface 底 + Line 描边。</summary>
    public const string ButtonSecondary = "StackItButtonSecondary";
    /// <summary>文字按钮：无边框无底，hover 出下划线。</summary>
    public const string ButtonText = "StackItButtonText";
    /// <summary>危险按钮：Danger 描边 + Danger 字，不是实底红。</summary>
    public const string ButtonDanger = "StackItButtonDanger";
    public const string Chip = "StackItChip";
    public const string ChipOn = "StackItChipOn";
    /// <summary>卡片：承载数据的主结构才配卡片，其余用留白 + hairline。</summary>
    public const string Card = "StackItCard";
    /// <summary>底部「记一笔」主按钮（宋体 15.5，AccentDeep 实底）。</summary>
    public const string Cta = "StackItCta";
    /// <summary>底部页签。</summary>
    public const string Tab = "StackItTab";

    /// <summary>按键取样式。取不到返回 null（样式表还没装），调用方走默认外观。</summary>
    public static Style? StyleOf(string key) => Application.Current.Resources[key] as Style;

    /// <summary>
    /// 代码里给元素套样式。样式表还没并进资源时（构造顺序、离屏自检）会在
    /// Loaded 再补一次，不至于静默套空。
    /// </summary>
    public static void ApplyStyle(FrameworkElement el, string key)
    {
        if (StyleOf(key) is { } s) el.Style = s;
        else el.Loaded += (_, _) =>
        {
            if (StyleOf(key) is { } t) el.Style = t;
        };
    }
}
