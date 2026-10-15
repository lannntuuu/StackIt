using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackIt.App.UI;

namespace StackIt.App;

/// <summary>
/// 卡片皮肤 —— 「深色靠描边不靠投影」这条规则落到实处的地方。
///
/// 为什么不在 Views/ 里做：卡片样式 <c>StackItCard</c> 是交互层那一张 XAML 样式表
/// 定义的（Setter 用 DynamicResource，代码里造不出来），而统计页 / 台账 / 数据页
/// 都靠 <c>Interactions.ApplyStyle(el, Card)</c> 套它。那三个文件本批不动，
/// 所以这里**换掉资源键上挂的那个 Style 实例**：
///   派生一份 BasedOn 原样式 + 三条覆盖（描边刷、1px 厚度、Effect 归零）。
/// 视图侧一个字都不用改，套到的就是带描边的版本；切回浅色时把覆盖撤掉，
/// 资源查找顺落回 merged 字典里那份原样式 —— 浅色卡的观感和改之前逐像素一致。
///
/// 顶高光没有另找一层画：1px 的环只能吃一支刷子，所以高光合成进描边色里
/// （见 <see cref="PaperTheme.CardStrokeBrush"/>），最上面那条边比其余三边亮一档。
/// </summary>
internal static class CardSkin
{
    /// <summary>样式表里卡片那条的键名（与 Interactions.Card 同一个键）。</summary>
    private static string Key => Interactions.Card;

    /// <summary>
    /// 换纸底后重解析交互层样式表。
    ///
    /// 为什么需要：样式表里那两枚 DropShadowEffect 的颜色写的是
    /// <c>{x:Static M.ShadowInk}</c> —— x:Static 是**解析期快照**，换纸底不会重取，
    /// 于是深纸上还留着浅纸的暖棕阴影。生命周期不许动（那是 Hover.Shift 的机制），
    /// 只能改取值来源：把这张 merged 字典摘掉再装一次，解析时就吃到新的 M.ShadowInk。
    /// 顺序有讲究：Install() 里已经装了就直接 return，所以必须**先摘旧的**。
    /// </summary>
    public static void ReinstallSheet()
    {
        var res = Application.Current.Resources;
        ResourceDictionary? stale = null;
        foreach (ResourceDictionary d in res.MergedDictionaries)
            if (d.Contains(Interactions.Button)) { stale = d; break; }
        if (stale is null) return;      // 还没装过：让 Install() 自己去装
        res.MergedDictionaries.Remove(stale);
        Interactions.Install();
    }

    /// <summary>
    /// 按当前纸底给卡片样式定皮肤。每次 Theme.Apply 都调（幂等）。
    /// </summary>
    public static void Apply(PaperTheme paper)
    {
        var res = Application.Current.Resources;

        // 浅色：撤掉覆盖，查找落回样式表里那一份。不改任何值，
        // 所以默认主题的卡片和改之前完全一样（连 Effect 都是原来那个实例）。
        if (!paper.IsDark)
        {
            res.Remove(Key);
            return;
        }

        // merged 字典里那份**原始**样式（不是被我覆盖过的那份）。
        var baseStyle = MergedBase();
        if (baseStyle is null)
        {
            // 样式表还没装（启动顺序、或自检路径）。Interactions.ApplyStyle 本来就有
            // "取不到就在 Loaded 再补一次"的兜底，所以这里不硬造一份无本的样式。
            return;
        }

        var skin = new Style(typeof(Border)) { BasedOn = baseStyle };
        skin.Setters.Add(new Setter(Border.BorderBrushProperty, paper.CardStrokeBrush()));
        skin.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        // 深纸上卡片不要投影（分层交给 1px 描边 + 顶高光）。样式表里那条 Effect 是本样式
        // 的 BaseOn 设的，派生样式的 Setter 优先级更高，所以这里置 null 就能盖掉。
        skin.Setters.Add(new Setter(UIElement.EffectProperty, null!));
        res[Key] = skin;
    }

    /// <summary>
    /// 从 merged 字典里取原始卡片样式。
    /// 不能走 <c>Application.Current.Resources[key]</c>：那会先命中我压在应用资源上的
    /// 覆盖版，BasedOn 就变成"派生自自己"，样式链一层层套下去。
    /// </summary>
    private static Style? MergedBase()
    {
        foreach (ResourceDictionary d in Application.Current.Resources.MergedDictionaries)
            if (d.Contains(Key) && d[Key] is Style s) return s;
        return null;
    }
}
