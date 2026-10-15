using System.Windows;
using System.Windows.Media;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App;

/// <summary>
/// 运行时主题。WPF 里没有 CSS 变量，改主题就是往 Application.Resources 里
/// 覆盖一组画刷（Brush）。键名与网页版的 CSS 变量同名，便于对照。
///
/// 这里只负责**登记键**；算色（HSL、明度色阶）在 Theme.cs 的 <see cref="Tone"/>，
/// 底色取值在 <see cref="PaperTheme"/>，强调色取值在 <see cref="Palettes"/>。
/// 三份职责分开，别在这里写算法。
///
/// 两个正交维度：
///   <see cref="Kind"/>（纸底 浅/深）决定底色那一组值；
///   <see cref="Current"/> 的 Id（六套强调色）只决定 Accent / AccentDeep / AccentSoft。
/// 两处的**键名一模一样**，换一个维度不碰另一个维度的键。
///
/// 界面侧的颜色必须一律 DynamicResource / SetResourceReference：
/// 换资源是整组替换 Brush **对象**，StaticResource 在解析时就把实例抄走了，
/// 换字典之后不会重解析，那一处会永远留在旧色上。
/// 只有字体族 / 圆角 / 动效时长不参与主题，那些继续用 StaticResource（更稳）。
/// </summary>
public static class Theme
{
    public const string Paper = "Paper";
    /// <summary>桌面底（窗口外面那一层）。图纸里叫 paper-deep，深色最低的一档也不给纯黑。</summary>
    public const string PaperDeep = "PaperDeep";
    public const string Surface = "Surface";
    /// <summary>hover 底。只比 Surface 深一档，交互层（Hover.Shift）用。</summary>
    public const string Surface2 = "Surface2";
    public const string Ink = "Ink";
    public const string Ink2 = "Ink2";
    public const string Ink3 = "Ink3";
    public const string Line = "Line";
    public const string LineSoft = "LineSoft";
    public const string LineStrong = "LineStrong";
    public const string Accent = "Accent";
    public const string AccentDeep = "AccentDeep";
    public const string AccentSoft = "AccentSoft";
    public const string OnAccent = "OnAccent";
    public const string Danger = "Danger";
    public const string DangerSoft = "DangerSoft";

    // —— 深色专属（图纸页脚第 3、4 条要的四个键）——
    /// <summary>卡片描边刷。深色 = Line + 顶高光合成后的竖直渐变；浅色 = Line 实色。</summary>
    public const string CardStroke = "CardStroke";
    /// <summary>顶高光的原始色（rgba(255,236,205,.06) 那个量级）。浅色为 Transparent。</summary>
    public const string PaperSheen = "PaperSheen";
    /// <summary>阴影墨色（Color 而不是 Brush）：DropShadowEffect.Color 吃的是 Color。</summary>
    public const string ShadowInkColor = "ShadowInkColor";
    /// <summary>色阶方向。false = 越多次越深（浅纸），true = 越多次越亮（深纸）。</summary>
    public const string RampInvert = "RampInvert";
    /// <summary>格内文字候选 · 「压在深底上」那支墨水。</summary>
    public const string GTextOnDark = "GTextOnDark";
    /// <summary>格内文字候选 · 「压在浅底上」那支墨水。</summary>
    public const string GTextOnLight = "GTextOnLight";

    // —— 格视图的用量色阶（旧键，GridView 在用）——
    // 单独一组，不复用 Line / Accent：那两个是**界面**颜色，拿来编码
    // 数据会出两类问题 —— 浅档跟卡片底色几乎同色（小格上等于没画），
    // 深档跳到 Ink 之后色相整个变了（读起来像换了语义而不是变深）。
    // 这里是显式的单色明度渐变。
    public const string Grid0 = "Grid0";
    public const string Grid1 = "Grid1";
    public const string Grid2 = "Grid2";
    public const string Grid3 = "Grid3";
    public const string Grid4 = "Grid4";
    public const string Grid5 = "Grid5";
    public const string Grid6 = "Grid6";

    // —— 格视图色阶（新键，v2 重设计用；避开上面两个坑的同一套办法）——
    // 与 Grid0..6 的差别只在明度上下界：G1 收到 0.850（再浅就融进 Surface），
    // G7 压到 0.200（再深就失彩、看着像换了色相）。两套并存是过渡状态，
    // 等 Views/ 切到 G1..G7（第 2 批）就删掉旧的那组。
    public const string G1 = "G1";
    public const string G2 = "G2";
    public const string G3 = "G3";
    public const string G4 = "G4";
    public const string G5 = "G5";
    public const string G6 = "G6";
    public const string G7 = "G7";

    /// <summary>当前那一套（= 当前纸底 + 当前强调色，两个维度合起来的完整取值）。</summary>
    public static Palette Current { get; private set; } = Palettes.All[0];

    /// <summary>当前纸底。默认浅色 —— 深色是第二套，不是替换。</summary>
    public static PaperKind Kind { get; private set; } = PaperKind.Light;

    /// <summary>当前纸底的全部取值。</summary>
    public static PaperTheme CurrentPaper => PaperTheme.Get(Kind);

    /// <summary>配色（强调色）变了。纸底变了也会触发 —— 那时整组值都换了。</summary>
    public static event Action<Palette>? Changed;

    /// <summary>只有「换纸」才触发。给需要重建非 DynamicResource 内容的地方用。</summary>
    public static event Action<PaperKind>? PaperChanged;

    /// <summary>当前色阶方向（= 深色取反）。视图侧不用读资源里的 bool：那是 object。</summary>
    public static bool RampInverted => CurrentPaper.RampInvert;

    /// <summary>
    /// 换强调色。底色不动 —— 六套之间唯一的变量就是强调色。
    /// 立即生效：所有颜色都走 DynamicResource，换掉 Brush 实例就够了。
    /// </summary>
    public static void Apply(string id)
    {
        var p = Palettes.Resolve(id, Kind);
        Current = p;
        Store.SavePalette(id);
        // SavePalette 是整份覆盖（只写 palette 一个键），会把 paper 抹掉，所以紧跟一次合并写。
        Prefs.SavePaper(Kind);
        Write(p);
        Changed?.Invoke(p);
    }

    /// <summary>
    /// 换纸底（浅 / 深）。与换强调色是两件事，走的是同一个登记入口。
    /// </summary>
    public static void ApplyPaper(PaperKind kind)
    {
        if (kind == Kind) { Write(Current); return; }
        SetKind(kind);
        Prefs.SavePaper(kind);
        // 样式表里的阴影色是 x:Static 解析期快照 —— 重解析一次才吃到新的 M.ShadowInk。
        CardSkin.ReinstallSheet();
        Apply(Current.Id);
        PaperChanged?.Invoke(kind);
    }

    /// <summary>
    /// 只做「纸底」这一半，不存不广播。
    /// 给 App.OnStartup 用：必须在 <c>Interactions.Install()</c> **之前**跑一次，
    /// 样式表里那两枚 DropShadowEffect 才解析得到当前纸底的阴影墨色。
    /// </summary>
    public static void PreparePaper()
    {
        SetKind(PaperTheme.Parse(Prefs.LoadPaperId()));
    }

    private static void SetKind(PaperKind kind)
    {
        Kind = kind;
        M.ApplyShadowInk(PaperTheme.Get(kind));
    }

    /// <summary>启动时读偏好，不要每次启动都回默认配色 / 默认纸底。</summary>
    public static void Restore()
    {
        PreparePaper();
        Apply(Store.LoadPalette());
    }

    /// <summary>
    /// 把一套 Palette（含它那张纸）整组登记进应用资源。
    /// 键名两套纸底完全相同，只换值 —— 所以这里没有任何按纸底分支的键。
    /// </summary>
    private static void Write(Palette p)
    {
        var paper = CurrentPaper;
        var res = Application.Current.Resources;

        void Put(string key, Color c) => res[key] = Freeze(new SolidColorBrush(c));

        Put(Paper, p.Paper);
        Put(PaperDeep, paper.PaperDeep);
        Put(Surface, p.Surface);
        Put(Surface2, p.Surface2);
        Put(Ink, p.Ink);
        Put(Ink2, p.Ink2);
        Put(Ink3, p.Ink3);
        Put(Line, p.Line);
        Put(LineSoft, p.LineSoft);
        Put(LineStrong, p.LineStrong);
        Put(Accent, p.Accent);
        Put(AccentDeep, p.AccentDeep);
        Put(AccentSoft, p.AccentSoft);
        Put(OnAccent, p.OnAccent);
        Put(Danger, p.Danger);
        Put(DangerSoft, p.DangerSoft);

        // 深纸专属的四类键。阴影色是 Color（DropShadowEffect.Color 不是 Brush），
        // 描边那支刷子由 CardSkin 直接挂到卡片样式上（它是渐变，不是一个 Color）。
        res[CardStroke] = paper.CardStrokeBrush();
        Put(PaperSheen, paper.Sheen);
        Put(GTextOnDark, paper.IsDark ? paper.Ink : p.OnAccent);
        Put(GTextOnLight, paper.IsDark ? p.OnAccent : paper.Ink);
        res[ShadowInkColor] = paper.ShadowInk;
        res[RampInvert] = paper.RampInvert;

        foreach (var (key, c) in Ramp(p.Accent, paper.RampInvert))
            Put(key, c);

        CardSkin.Apply(paper);
    }

    /// <summary>
    /// 从强调色生成两组 7 档明度渐变，一次把 14 个键都算出来。
    ///
    /// 为什么自己算而不是取现成的色：Accent / AccentDeep / AccentSoft 是给
    /// 交互和文字用的，三档跨度太大且色相会飘。数据色阶要的是**匀速变化**，
    /// 每一档都还是同一个强调色的色相 —— 用户才能把"深浅"直接读成"多少"。
    ///
    /// 明度按固定间隔铺开，而不是相对 Accent 的比例 ——
    /// 否则换成深色的强调色（比如靛）时整条色阶会挤在暗部，浅档全看不见。
    /// 具体的明度值在 Tone 里，那里同时写了上界/下界为什么是这两个数。
    ///
    /// <paramref name="invert"/> 是深色纸唯一的差别：把明度序列从末位往前取，
    /// 于是"越多次越亮"。仍然是同一个生成器、同一组固定间隔（分档断点一个字没改）。
    /// 0 次仍然画空心（LineStrong）、窗口外仍然完全留白 —— 那两条在 LedgerRow 里，
    /// 这里翻不了它们，也不该翻。
    /// </summary>
    private static (string Key, Color Color)[] Ramp(Color accent, bool invert)
    {
        var legacy = Tone.Ramp(accent, Tone.GridLegacyLightness, invert);
        var grid = Tone.GridRamp(accent, invert);

        string[] legacyKeys =
        {
            Grid0, Grid1, Grid2, Grid3, Grid4, Grid5, Grid6,
        };
        string[] gridKeys = { G1, G2, G3, G4, G5, G6, G7 };

        var outp = new (string, Color)[legacyKeys.Length + gridKeys.Length];
        for (var i = 0; i < legacyKeys.Length; i++)
            outp[i] = (legacyKeys[i], legacy[i]);
        for (var i = 0; i < gridKeys.Length; i++)
            outp[legacyKeys.Length + i] = (gridKeys[i], grid[i]);
        return outp;
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
