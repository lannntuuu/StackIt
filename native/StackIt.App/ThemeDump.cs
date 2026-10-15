using System;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace StackIt.App;

/// <summary>
/// 主题取值诊断（<c>--theme-dump</c>）。
///
/// 为什么要它：色阶不是写死的七个十六进制，而是「强调色 + 固定明度间隔」现算的，
/// 六套强调色 × 两种纸底一共 12 条。要核对某套下 G1..G7 实际是什么、格内数字
/// 该用哪支墨水，只能问应用自己 ——
/// 从截图取样会被界面元素干扰（试过两次：一次抓到系统滚动条的 #F0F0F0/#CDCDCD，
/// 一次抓到 Ink3 的文字行），而「选了哪支墨水」这件事在像素上和「对比度够不够」
/// 根本划不开。所以把判断挪到唯一知道答案的地方：ThemeRuntime 注册的那批值。
/// </summary>
internal static class ThemeDump
{
    public static bool Run()
    {
        var fails = 0;
        foreach (var kind in new[] { PaperKind.Light, PaperKind.Dark })
        {
            var paper = PaperTheme.Get(kind);
            var surf = Hex(paper.Surface);
            Console.WriteLine($"== {kind}｜纸 {Hex(paper.Paper)}｜卡片 {surf}｜hover {Hex(paper.Surface2)}｜" +
                              $"方向 {(paper.RampInvert ? "越多次越亮" : "越多次越深")}");

            // 三级文字必须在该纸的**每一种**文字底上都够 4.5:1。
            //
            // 只查 hover 底是不够的 —— 元信息绝大多数时候压在纸面与卡片上。
            // 浅纸的 Ink3 就是这么漏过去的：#8C8272 连纯白都只有 3.78:1，
            // 换任何浅底色都救不了，只能改取值本身。
            foreach (var bg in new (string Name, Color C)[]
                     { ("纸", paper.Paper), ("卡片", paper.Surface), ("hover", paper.Surface2) })
            {
                var inks = new (string, Color)[] { ("Ink", paper.Ink), ("Ink2", paper.Ink2), ("Ink3", paper.Ink3) };
                var txt = inks.Select(i => $"{i.Item1}={Contrast(i.Item2, bg.C):F2}");
                var bad = inks.Any(i => Contrast(i.Item2, bg.C) < 4.5);
                if (bad) fails++;
                Console.WriteLine($"   文字 on {bg.Name,-6} {string.Join("  ", txt)}  " + (bad ? "FAIL" : "PASS"));
            }

            // 顶高光：合成进描边色的那一档，量它和 Line 的实际差值
            var top = PaperTheme.Mix(paper.Sheen, paper.Line);
            Console.WriteLine($"   顶高光 {Hex(top)} 对 Line {Hex(paper.Line)} = " +
                              $"{Contrast(top, paper.Line):F2}" +
                              (paper.IsDark ? (Contrast(top, paper.Line) >= 1.35 ? "  PASS（1px 上看得出来）" : "  FAIL（太弱）") : "  （浅纸不画高光）"));

            foreach (var p in Palettes.For(kind))
            {
                var ramp = Tone.GridRamp(p.Accent, paper.RampInvert);
                Console.WriteLine($"   {p.Id,-7} 色阶 {string.Join(" ", ramp.Select(Hex))}");

                // 节律矩阵只用 G3..G7（五档），逐档取「两支候选墨水里对比度高的那支」
                var lightInk = paper.IsDark ? paper.Ink : p.OnAccent;
                var darkInk = paper.IsDark ? p.OnAccent : paper.Ink;
                var sb = new StringBuilder();
                var min = 99.0;
                for (var i = 2; i < ramp.Length; i++)
                {
                    var c = Math.Max(Contrast(lightInk, ramp[i]), Contrast(darkInk, ramp[i]));
                    min = Math.Min(min, c);
                    sb.Append($"G{i + 1}={c:F2} ");
                }
                var ok = min >= 4.5;
                if (!ok) fails++;
                Console.WriteLine($"          格内数字 {sb}".TrimEnd() + $"   最低 {min:F2}  {(ok ? "PASS" : "FAIL")}");
            }
        }
        Console.WriteLine(fails == 0
            ? "全部通过：12 条色阶的格内数字都 ≥4.5:1"
            : $"{fails} 处不达标");
        Environment.Exit(fails == 0 ? 0 : 1);
        return true;
    }

    private static double Contrast(Color a, Color b) => PaperTheme.Contrast(a, b);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
