using System.Windows;
using System.Windows.Media;

namespace StackIt.App;

/// <summary>
/// 「纸底」维度 —— 与「强调色」六套**正交**。
/// 浅 / 深两份底色，键名完全相同、只换值，所以换纸不需要动任何一处引用。
///
/// 图纸：<c>design/UI重设计-效果图.html</c>（浅）与
/// <c>design/UI重设计-效果图-深色纸.html</c>（深）。所有取值照图纸抄，
/// 不要自创 —— 那两组数是按 WCAG 2.1 相对亮度实测过对比度的。
/// 两条硬底线也随图纸一起搬过来：深色**不用纯黑**（最暗 #17140F）、
/// **不用纯白**（文字最亮 #EDE4D3），纯色在纸上会发虚、边缘起毛。
/// </summary>
public enum PaperKind
{
    /// <summary>暖灰纸（默认）。三年量级的记录长期读，浅色是默认那一套。</summary>
    Light,
    /// <summary>暖棕深纸。夜间 / 低光环境的第二套。</summary>
    Dark,
}

/// <summary>
/// 一套强调色的三档。底色不在这里 —— 底色属于纸底，六套共用同一张纸。
/// </summary>
/// <param name="Accent">交互前景（描边 / 指示条 / 色点）。</param>
/// <param name="AccentDeep">
/// 浅纸 = 更深的那一档（承载文字、实底按钮）；
/// 深纸 = **更亮**的那一档（承载文字）。同一个键，明度轴上的方向相反。
/// </param>
/// <param name="AccentSoft">
/// 浅纸 = 淡高亮底；深纸 = 低饱和深底（选中态 / hover 底 / 提示条底）。</param>
public readonly record struct AccentSet(Color Accent, Color AccentDeep, Color AccentSoft);

/// <summary>
/// 一张纸：整套底色 + 与纸绑定的那几个键（OnAccent / Danger / 阴影 / 描边 / 色阶方向）。
///
/// 职责边界：这里只放**取值**，不放算法（算色在 <see cref="Tone"/>）、
/// 不登记资源（登记在 <see cref="Theme"/>）。
/// </summary>
public sealed record PaperTheme(
    PaperKind Kind, string PrefId, string Name, string Vibe,
    Color PaperDeep, Color Paper, Color Surface, Color Surface2,
    Color Ink, Color Ink2, Color Ink3,
    Color Line, Color LineSoft, Color LineStrong,
    Color OnAccent, Color Danger, Color DangerSoft,
    // —— 阴影：深纸上暖棕投影读不出来，换成近黑的暖棕，并且只留给浮层 ——
    Color ShadowInk, byte CardShadowAlpha, byte HoverShadowAlpha,
    byte ShadowLayerAlpha, byte FloatShadowAlpha,
    // —— 深色专属：分层靠描边 + 顶高光，不靠投影 ——
    Color Sheen, bool RampInvert, double GTextCutL)
{
    /// <summary>浅纸。逐字沿用旧值，一个都没动（默认主题必须和改之前一样）。</summary>
    public static readonly PaperTheme Light = new(
        PaperKind.Light, "light", "浅", "暖灰纸 · 默认",
        Hex(0xEA, 0xE4, 0xDA),   // PaperDeep 桌面
        Hex(0xF4, 0xF1, 0xEB),   // Paper
        Hex(0xFC, 0xFA, 0xF6),   // Surface 卡片
        Hex(0xF6, 0xF2, 0xEA),   // Surface2 hover 底
        Hex(0x21, 0x1B, 0x14),   // Ink
        Hex(0x5C, 0x53, 0x46),   // Ink2
        Hex(0x70, 0x68, 0x5B),   // Ink3 元信息。旧值 #8C8272 压在**纯白**上也只有 3.78:1 ——
                                  // 浅纸上不存在能让它达标的底色，所以改取值本身：
                                  // 同色相压暗 20%，on 纸 4.88 / on hover 4.92（≥4.5），
                                  // 与 Ink2 仍可辨 1.37×（再深一档就糊成两级了）。
                                  // LineStrong 保持 #8C8272：它是非文字图形（门槛 3:1），
                                  // 而且空心环本该比实心格更安静。
        Hex(0xDC, 0xD4, 0xC6),   // Line
        Hex(0xE8, 0xE1, 0xD5),   // LineSoft
        Hex(0x8C, 0x82, 0x72),   // LineStrong 空心格描边
        Hex(0xFF, 0xFD, 0xF7),   // OnAccent 琥珀实底上的字
        Hex(0xA6, 0x2B, 0x22),   // Danger
        Hex(0xF5, 0xE2, 0xDE),   // DangerSoft
        Hex(0x5E, 0x3A, 0x13),   // ShadowInk 暖棕（= 琥珀 AccentDeep）
        0x0D, 0x15, 0x2E, 0x12,  // 卡片 / hover / 阴影层 / 浮层 的 alpha（旧值原样）
        Colors.Transparent,      // 浅纸不画顶高光（本身就接近白，加上去只会发灰）
        false,                   // 色阶方向：越多次越深
        0.213);

    /// <summary>
    /// 深纸。四层关系是「桌面 &lt; hover &lt; 纸 &lt; 卡片」—— 与浅纸同构但**方向相反**：
    /// 浅纸的 hover 是抬亮（Surface→Surface2 更亮），深纸的 hover 是压暗。
    /// 为什么反过来：暗底上浅色文字的对比度随底色变暗而**升高**，抬亮反而会把三级
    /// 文字里最浅的 Ink3 压到 4.32:1（不达 4.5）。压暗之后 Ink3 到 5.52:1，
    /// 而 Ink/Ink2/Ink3 三个取值一个字都不用改（层级比值 1.80 / 1.40 原样保留）。
    /// </summary>
    public static readonly PaperTheme Dark = new(
        PaperKind.Dark, "dark", "深", "暖棕深纸 · 夜间",
        Hex(0x17, 0x14, 0x0F),
        Hex(0x21, 0x1C, 0x15),
        Hex(0x2A, 0x24, 0x1B),
        Hex(0x1D, 0x18, 0x12),   // Surface2 hover 底：往暗走，见上面说明
        Hex(0xED, 0xE4, 0xD3),   // Ink 米白，不是 #FFF
        Hex(0xB8, 0xAA, 0x95),
        Hex(0x9C, 0x8E, 0x7D),
        Hex(0x4A, 0x41, 0x2E),
        Hex(0x36, 0x30, 0x1F),
        Hex(0x8A, 0x7D, 0x67),   // LineStrong 压在 Surface 上 3.81:1，保证空心格看得见
        Hex(0x1B, 0x16, 0x10),   // OnAccent 翻转：琥珀底现在自己是亮的那一方
        Hex(0xE0, 0x70, 0x5F),   // Danger 提亮（压在深纸上 4.87:1），不随六套强调色变
        Hex(0x39, 0x20, 0x1C),   // DangerSoft
        Hex(0x08, 0x05, 0x02),   // ShadowInk 近黑的暖棕 —— 仍然不是 #000
        0x2E, 0x30, 0x38, 0xD9,  // 卡片/hover/阴影层压到很低；浮层留一道重的
        HexA(0x26, 0xFF, 0xEC, 0xCD),   // Sheen α=0.149：合成到 Line 上每通道 +26，1px 才看得出来
        true,                    // 色阶方向：越多次越亮
        0.213);

    public static PaperTheme Get(PaperKind kind) => kind == PaperKind.Dark ? Dark : Light;

    /// <summary>偏好里的字符串 → 纸底。认不出来的一律回浅色（默认必须是浅）。</summary>
    public static PaperKind Parse(string? id) =>
        string.Equals(id, Dark.PrefId, StringComparison.OrdinalIgnoreCase)
            ? PaperKind.Dark : PaperKind.Light;

    /// <summary>这张纸的强调色三档该取哪一组。</summary>
    public bool IsDark => Kind == PaperKind.Dark;

    /// <summary>
    /// 卡片描边刷：深色 = 1px Line，顶上一道极淡高光（模拟纸面反光）；浅色 = Line 实色。
    ///
    /// 高光是**合成**过的实色（Sheen 压在 Line 上），不是叠一层半透明：
    /// WPF 的 BorderBrush 只能给一支刷子，而一条 1px 的环没法既描边又叠色。
    /// 竖直渐变让最上面那 1px 走到合成后的亮值，往下立刻回落到 Line。
    /// </summary>
    public Brush CardStrokeBrush()
    {
        if (!IsDark) return Frozen(new SolidColorBrush(Line));

        var top = Mix(Sheen, Line);
        return Frozen(new LinearGradientBrush(
            new GradientStopCollection
            {
                new(top, 0.0),
                new(Line, 0.02),
                new(Line, 1.0),
            },
            new Point(0, 0), new Point(0, 1)));
    }

    /// <summary>
    /// sRGB 相对亮度（WCAG 2.1：线性化后 L=0.2126R+0.7152G+0.0722B）。
    /// 图纸的对比度表和「格内数字按底色亮度取色」都靠它，不要换成加权和的近似。
    /// </summary>
    public static double Lum(Color c)
    {
        double F(double v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    /// <summary>前景压在后景上的对比度比值。</summary>
    public static double Contrast(Color fg, Color bg)
    {
        var (a, b) = Lum(fg) >= Lum(bg) ? (Lum(fg), Lum(bg)) : (Lum(bg), Lum(fg));
        return (a + 0.05) / (b + 0.05);
    }

    /// <summary>带 alpha 的前景合成到不透明后景上（图纸的 sheen 是半透明的）。</summary>
    public static Color Mix(Color over, Color under)
    {
        var t = over.A / 255.0;
        return Color.FromRgb(
            (byte)System.Math.Round(over.R * t + under.R * (1 - t)),
            (byte)System.Math.Round(over.G * t + under.G * (1 - t)),
            (byte)System.Math.Round(over.B * t + under.B * (1 - t)));
    }

    private static Color Hex(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color HexA(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
