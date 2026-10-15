using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace StackIt.App;

/// <summary>
/// 配色。对应 design/UI重设计-效果图.html 的 :root 令牌块，
/// 以及它的第二套取值 design/UI重设计-效果图-深色纸.html。
///
/// 两个**正交**的维度：
///   强调色 —— 六套（琥珀 / 陶土 / 青 / 靛 / 紫 / 苔）；
///   纸底   —— 两套（<see cref="PaperKind.Light"/> / <see cref="PaperKind.Dark"/>）。
/// 一个 Palette = 当前纸底的整组底色 + 那一套强调色的三档。
///
/// 为什么底色要固定：产品规则，同一纸底下六套预设必须**只差强调色**。
/// 之前六套共用 Radix slate（冷灰），后来整组换成暖纸（Paper F4F1EB 起），
/// 但"唯一变量是强调色"这条没变 —— Surface2 也归在底色家族里，六套同值，
/// 不然某两套的差别就变成"底色也微妙不同"，肉眼分不出来。
/// 换成深色纸时这条照样成立：深纸上的六套同样只差 Accent / AccentDeep / AccentSoft。
///
/// WPF 没有 CSS 变量，这里用 ResourceDictionary 在运行时整组替换。
/// 键名两套纸底完全相同、只换值，所以换纸不需要动任何一处引用。
/// </summary>
public sealed record Palette(
    string Id, string Name, string Vibe,
    Color Paper, Color Surface, Color Surface2, Color Ink, Color Ink2, Color Ink3,
    Color Line, Color LineSoft, Color LineStrong,
    Color Accent, Color AccentDeep, Color AccentSoft,
    Color OnAccent, Color Danger, Color DangerSoft)
{
    public SolidColorBrush P(Color c) => new(c);
}

/// <summary>画布与字体的度量，全部集中在这里，不要散落魔法数字。</summary>
public static class M
{
    // —— 圆角要有层次，不要全站一个值 ——
    // 之前统一 6px：卡片和控件一样圆，层级全平（设计台账 P1「只有三档字重、层级全平」
    // 的另一半）。半径本身就是层级信号：越大越"容器"。
    /// <summary>容器（页面级面板、底部条）</summary>
    public static readonly CornerRadius RLg = new(10);
    /// <summary>卡片（承载数据的主结构）</summary>
    public static readonly CornerRadius RMd = new(8);
    /// <summary>控件（按钮、输入、芯片）</summary>
    public static readonly CornerRadius RSm = new(6);
    /// <summary>小格（格视图、键帽、色点）</summary>
    public static readonly CornerRadius RXs = new(2);

    /// <summary>兼容旧引用：现有代码里的 M.R 就是控件档。新代码请写明确档位。</summary>
    public static readonly CornerRadius R = RSm;

    /// <summary>
    /// 正文族。
    ///
    /// 旧值写的是 `-apple-system, BlinkMacSystemFont, Segoe UI, PingFang SC, ...`
    /// —— 那是 CSS 字体栈，WPF 的 FontFamily 不认 -apple-system / BlinkMacSystemFont，
    /// 这个声明从来没按设计生效过（整站掉回宋体/雅黑的默认解析）。
    /// 现在只列本机确认装好的族名，按"设计意图 → 系统回退"排序。
    /// </summary>
    public static readonly FontFamily Sans = new("Noto Sans SC, Microsoft YaHei, Segoe UI");

    /// <summary>
    /// 标题 / 品名族。设计要「宋体承载名称」，而旧代码里**根本没有衬线族**，
    /// 全站 0 个衬线声明。SimSun 作末位回退（本机一定有），首选思源宋。
    /// </summary>
    public static readonly FontFamily Serif = new("Noto Serif SC, SimSun");

    /// <summary>
    /// 数字用等宽，位数变化时不跳动。
    ///
    /// WPF 没有 font-variant-numeric：等宽数字要靠**字体本身是 tabular 的**
    /// （Consolas 天然是）+ 附加属性 <c>Tone.SetTabular</c> 打开 OpenType 的 tnum/lnum。
    /// 给非等宽字体（思源黑体的拉丁数字是比例宽）开这个才是数字对齐的正解，
    /// 所以它挂在任何元素上都能用，不必把整段换成 Mono 族。
    /// </summary>
    public static readonly FontFamily Mono = new("Consolas, Menlo, monospace");

    // —— 阴影：统一暖棕，光源在上（投影朝下）——
    // 不能用黑色：黑投影落在暖纸底上会发灰，纸感当场没了。
    // RGB(94,58,19) 就是琥珀的 AccentDeep，六套共用（阴影跟着强调色走会脏）。
    //
    // 「纸底」维度加进来之后它不再是常量：深纸上暖棕投影读不出来，
    // 图纸把阴影墨色换成近黑的暖棕（#080502 级别），并且只留给浮层。
    // 它是**属性而不是 readonly 字段**：值由 ApplyShadowInk 随纸底换，
    // 下面那几个工厂每次都现读，所以代码里创建的 Effect 天然跟着主题走。
    // （XAML 里的 {x:Static M.ShadowInk} 是解析期快照 —— 换纸底时
    //  ThemeRuntime 会重解析交互层样式表，见 CardSkin.Reinstall。）
    public static Color ShadowInk { get; private set; } = PaperTheme.Light.ShadowInk;

    /// <summary>当前纸底（只取阴影相关的值，不参与算色）。</summary>
    private static PaperTheme _skin = PaperTheme.Light;

    /// <summary>
    /// 把阴影墨色与强度切到某一纸底。
    /// 必须在 <c>Interactions.Install()</c> 解析样式表**之前**调用一次，
    /// 否则样式表里那两枚 DropShadowEffect 会固化成浅纸的值。
    /// </summary>
    public static void ApplyShadowInk(PaperTheme p)
    {
        _skin = p;
        ShadowInk = p.ShadowInk;
    }

    /// <summary>卡片底：1px 贴地 + 4px 散开，只在扫过时看得见。</summary>
    public static DropShadowEffect CardShadow() =>
        Shadow(_skin.CardShadowAlpha, 1, 4, 0.22);

    /// <summary>hover / 抬起：阴影加深，元素像被指头推起来。</summary>
    public static DropShadowEffect HoverShadow() =>
        Shadow(_skin.HoverShadowAlpha, 3, 10, 0.42);

    /// <summary>浮层（弹层、底部条）：深纸下唯一还该看见投影的一层。</summary>
    public static DropShadowEffect FloatShadow() =>
        Shadow(_skin.FloatShadowAlpha, 4, 20, 0.35);

    /// <summary>
    /// 「阴影加深」的实现载体：一个**常驻但透明**的效果层。
    ///
    /// 为什么不直接动画 Effect：Effect 不是可动画的标量，而且加/换 Effect 会让
    /// 整个元素重新走一次光栅化。设计约束是「只能用 Transform 和 Opacity 动画」——
    /// 所以这里把阴影本身做成常驻对象，只动它的 Opacity（GPU 合成，不触发布局）。
    /// </summary>
    public static DropShadowEffect ShadowLayer() =>
        Shadow(_skin.ShadowLayerAlpha, 3, 12, 0.0);

    private static DropShadowEffect Shadow(byte alpha, double depth,
        double blur, double opacity) => new()
    {
        Color = Color.FromArgb(alpha, ShadowInk.R, ShadowInk.G, ShadowInk.B),
        ShadowDepth = depth,
        BlurRadius = blur,
        // 光源固定左上：WPF 的 Direction 是**投影的方向**（角度），
        // 270 度 = 朝下，等于光从左上来。全站只用这一个方向，不要一处朝下一处朝右。
        Direction = 270,
        Opacity = opacity,
        RenderingBias = RenderingBias.Performance,
    };
}

/// <summary>
/// 算色。HSL 转换与明度色阶的唯一实现处 —— 之前 ThemeRuntime 里有一份私有的，
/// 现在两边共用这里，避免同一套色阶出现两份会各自漂移的实现。
/// </summary>
public static class Tone
{
    /// <summary>
    /// 格视图 7 档（G1..G7）的明度，固定间隔 0.850 → 0.200，浅 → 深。
    ///
    /// 为什么是"固定明度间隔"而不是相对 Accent 的比例：换成深色强调色（靛）时，
    /// 按比例铺会整条挤在暗部，浅档全看不见（README 记过这条）。
    /// 为什么上界只到 0.850：再浅就和卡片底 Surface(FCFAF6) 同色了 ——
    /// 4px 小格上等于没画，这正是 README 记的另一个坑（曾经拿 Line 当浅档）。
    /// 为什么下界到 0.200：再深会开始失彩、读起来像换了色相而不是变深
    /// （也踩过：深档跳到 Ink 之后色相整个变了）。
    /// </summary>
    public static readonly double[] GridLightness =
        { 0.850, 0.755, 0.660, 0.560, 0.455, 0.335, 0.200 };

    /// <summary>
    /// 旧 7 档（Grid0..6）的明度。保留原值：格视图已经在用它，
    /// 本批不动 Views/，所以两套并存，第 2 批把 Views/ 切到 G1..G7 后删。
    /// </summary>
    public static readonly double[] GridLegacyLightness =
        { 0.88, 0.80, 0.70, 0.60, 0.48, 0.37, 0.27 };

    /// <summary>
    /// 格视图 7 档明度阶：G1 → G7。浅纸上「越多次越深」，深纸上整条取反
    /// （<paramref name="invert"/>=true → 越多次越亮）。
    /// </summary>
    /// <param name="accent">该套配色的强调色（色相/饱和度来自它，明度由上面的固定间隔给）。</param>
    /// <param name="invert">true = 明度序列取反。只翻方向，不换生成器、不改间隔。</param>
    public static Color[] GridRamp(Color accent, bool invert = false) =>
        Ramp(accent, GridLightness, invert);

    /// <summary>
    /// 按给定明度序列把强调色铺成色阶（色相/饱和度不动，只变明度）。
    ///
    /// <paramref name="invert"/> 是唯一的"方向"开关：把序列从末位往前取，
    /// 于是同一组固定间隔在深纸上读成「越亮 = 越多」。
    /// 两套主题共用这一个生成器 —— 写两份迟早会各自漂移（明度上下界那两个坑
    /// 就是两份实现各改一半留下来的）。
    /// </summary>
    public static Color[] Ramp(Color accent, double[] lightness, bool invert = false)
    {
        var (h, s, _) = RgbToHsl(accent);
        var outp = new Color[lightness.Length];
        for (var i = 0; i < lightness.Length; i++)
        {
            var l = invert ? lightness[lightness.Length - 1 - i] : lightness[i];
            outp[i] = HslToRgb(h, s, l);
        }
        return outp;
    }

    public static (double h, double s, double l) RgbToHsl(Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = System.Math.Max(r, System.Math.Max(g, b));
        var min = System.Math.Min(r, System.Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
        return (h, s, l);
    }

    public static Color HslToRgb(double h, double s, double l)
    {
        if (s == 0)
        {
            var v = (byte)System.Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return Color.FromRgb(
            (byte)System.Math.Round(Hue(h + 1.0 / 3) * 255),
            (byte)System.Math.Round(Hue(h) * 255),
            (byte)System.Math.Round(Hue(h - 1.0 / 3) * 255));
    }

    /// <summary>
    /// 数字对齐（font-variant-numeric 的 WPF 等价手段）。
    ///
    /// 用附加属性而不是让各处换成 Mono 族：排版上品名/正文得是无衬线，
    /// 只有数字要 tabular。Typography.NumeralAlignment 走 OpenType 的 tnum，
    /// 字体不支持时 WPF 会静默不生效（不抛），所以可以放心挂在任何 TextBlock 上。
    /// </summary>
    public static readonly DependencyProperty TabularProperty =
        DependencyProperty.RegisterAttached(
            "Tabular", typeof(bool), typeof(Tone),
            new PropertyMetadata(false, OnTabular));

    public static void SetTabular(DependencyObject o, bool v) =>
        o.SetValue(TabularProperty, v);
    public static bool GetTabular(DependencyObject o) => (bool)o.GetValue(TabularProperty);

    private static void OnTabular(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Typography 的元素级附加属性可以设在**任何** DependencyObject 上：设在容器上，
        // 里面的文本靠继承拿到；字体没有 tnum 特性时 WPF 静默不生效（不抛异常），
        // 所以不需要为"字体不支持"兜任何分支。
        var on = (bool)e.NewValue;
        System.Windows.Documents.Typography.SetNumeralAlignment(d,
            on ? FontNumeralAlignment.Tabular : FontNumeralAlignment.Normal);

        // 非等宽字体（思源黑体）的拉丁数字默认比例宽，光开 tnum 有些字体不认；
        // 数字位数变化时整行会左右跳。所以再把等宽数字落到能落的地方：
        // 元素自己就是文本控件时，顺手把 FontFamily 换成 Mono 族是不行的
        // （会改字形、破坏排版），因此这里只动 OpenType 特性，不做字体替换。
        System.Windows.Documents.Typography.SetNumeralStyle(d,
            on ? FontNumeralStyle.Lining : FontNumeralStyle.Normal);
    }
}

public static class Palettes
{
    // —— 底色不在这里：它属于「纸底」维度，见 PaperTheme ——
    // 旧值（Radix slate 冷灰）：Paper F0F0F3 / Surface FCFCFD / Ink 1C2024 /
    // Ink2=Ink3 60646C（两套同值，等于没有二级灰）/ Line E0E1E6 /
    // LineSoft E8E8EC / LineStrong 777B84 / Danger B3261E / DangerSoft F7E5E4。
    // 现在这组值在 PaperTheme.Light 里，深纸那一组在 PaperTheme.Dark 里。

    /// <summary>六套的身份：Id / 名称 / 「感觉」。顺序就是界面上色卡的顺序。</summary>
    private static readonly (string Id, string Name, string Vibe)[] Identity =
    {
        ("amber", "琥珀", "像体温、像晨光"),
        ("orange", "陶土", "手账、印章感，暖而沉"),
        ("teal", "青", "干净清爽，医疗记录该有的样子"),
        ("indigo", "靛", "压深的普鲁士蓝，冷静专业"),
        ("violet", "紫", "有辨识度，不落俗套"),
        ("lime", "苔", "自然但不土"),
    };

    /// <summary>
    /// 浅纸上的六个强调色，每个取自一个 Radix 色阶家族（都只降饱和、不改色相）。
    /// acc=交互前景 / deep=承载文字 / soft=浅底，三档是同家族内的相邻级。
    /// </summary>
    private static readonly AccentSet[] LightAccents =
    {
        new(Color.FromRgb(0xA2, 0x68, 0x1F), Color.FromRgb(0x5E, 0x3A, 0x13), Color.FromRgb(0xF3, 0xE6, 0xCE)),
        new(Color.FromRgb(0xC4, 0x56, 0x0B), Color.FromRgb(0x58, 0x2D, 0x1D), Color.FromRgb(0xF7, 0xE6, 0xCF)),
        new(Color.FromRgb(0x1B, 0x82, 0x71), Color.FromRgb(0x0D, 0x3D, 0x38), Color.FromRgb(0xD8, 0xEE, 0xE8)),
        new(Color.FromRgb(0x43, 0x57, 0x9B), Color.FromRgb(0x1F, 0x2D, 0x5C), Color.FromRgb(0xDF, 0xE6, 0xF7)),
        new(Color.FromRgb(0x65, 0x50, 0xB9), Color.FromRgb(0x2F, 0x26, 0x5F), Color.FromRgb(0xE8, 0xE2, 0xF8)),
        new(Color.FromRgb(0x5F, 0x7B, 0x33), Color.FromRgb(0x37, 0x40, 0x1C), Color.FromRgb(0xE7, 0xEF, 0xD2)),
    };

    /// <summary>
    /// 深纸上的同一组色相，三档全部重新校过（图纸实测对比度，不是估的）。
    ///
    /// **语义翻转**，键名不改、只换明度轴上的方向：
    ///   AccentDeep = 更亮的「文字级」（浅纸那边是更深的那一档）
    ///   Accent     = 交互前景，自己就能承载文字（浅纸的 #A2681F 压在深纸上只剩 3.08:1）
    ///   AccentSoft = 低饱和深底，专职选中态 / hover 底 / 提示条底
    /// </summary>
    private static readonly AccentSet[] DarkAccents =
    {
        new(Color.FromRgb(0xD7, 0x9A, 0x3E), Color.FromRgb(0xEA, 0xB4, 0x5E), Color.FromRgb(0x3A, 0x2C, 0x17)),
        new(Color.FromRgb(0xDE, 0x7B, 0x3C), Color.FromRgb(0xEE, 0x9A, 0x5F), Color.FromRgb(0x3A, 0x24, 0x17)),
        new(Color.FromRgb(0x46, 0xBF, 0xA8), Color.FromRgb(0x74, 0xD8, 0xBC), Color.FromRgb(0x14, 0x31, 0x2C)),
        new(Color.FromRgb(0x6E, 0x8A, 0xE8), Color.FromRgb(0x9B, 0xB0, 0xF0), Color.FromRgb(0x1C, 0x24, 0x44)),
        new(Color.FromRgb(0x9C, 0x86, 0xEA), Color.FromRgb(0xBC, 0xAB, 0xF4), Color.FromRgb(0x25, 0x1E, 0x46)),
        new(Color.FromRgb(0x93, 0xB4, 0x57), Color.FromRgb(0xB6, 0xD4, 0x7E), Color.FromRgb(0x26, 0x31, 0x1A)),
    };

    /// <summary>浅纸上的六套。顺序与 Identity 一致；「换了纸」之后要看的不是这个，是 For(kind)。</summary>
    public static readonly Palette[] All = Build(PaperKind.Light);

    /// <summary>某一纸底下的六套预设 —— 底色同值，只差强调色。</summary>
    public static Palette[] For(PaperKind kind) => Build(kind);

    private static Palette[] Build(PaperKind kind)
    {
        var paper = PaperTheme.Get(kind);
        var acc = Accents(kind);
        var outp = new Palette[Identity.Length];
        for (var i = 0; i < Identity.Length; i++)
            outp[i] = Compose(Identity[i], paper, acc[i]);
        return outp;
    }

    /// <summary>把「一套强调色 + 一张纸」拼成一个完整 Palette。ThemeRuntime 与色卡都走这里。</summary>
    public static Palette Resolve(string id, PaperKind kind)
    {
        var i = Index(id);
        return Compose(Identity[i], PaperTheme.Get(kind), Accents(kind)[i]);
    }

    private static AccentSet[] Accents(PaperKind kind) =>
        kind == PaperKind.Dark ? DarkAccents : LightAccents;

    private static Palette Compose((string Id, string Name, string Vibe) idn,
        PaperTheme p, AccentSet a) =>
        new(idn.Id, idn.Name, idn.Vibe,
            p.Paper, p.Surface, p.Surface2, p.Ink, p.Ink2, p.Ink3,
            p.Line, p.LineSoft, p.LineStrong,
            a.Accent, a.AccentDeep, a.AccentSoft,
            p.OnAccent, p.Danger, p.DangerSoft);

    /// <summary>按 id 取那一套的下标；认不出来的 id 一律回第一套（跟旧行为一致）。</summary>
    public static int Index(string id)
    {
        var i = Array.FindIndex(Identity, x => x.Id == id);
        return i < 0 ? 0 : i;
    }

    /// <summary>旧入口：浅纸上的那一套。切了纸底请改用 <see cref="Resolve(string, PaperKind)"/>。</summary>
    public static Palette Get(string id) => Resolve(id, PaperKind.Light);
}
