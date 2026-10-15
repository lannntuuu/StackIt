using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;   // DropShadowEffect（色卡的常驻阴影层）
using Microsoft.Win32;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 数据页：配色色卡、数据目录回退链诊断、导入 / 导出 / 恢复。
/// 结构照 design/UI重设计-效果图.html 第 ④ 屏。
///
/// 第 3 批 A 改掉的三件事：
///   1. 配色从 6 个小 pill 变成**色卡网格**：每张画该套的 deep / accent / soft 三条色带、
///      宋体名称和 README 里那句「感觉」。光看小色块判断不出实际观感。
///   2. 「数据存在哪」从一句提示变成**回退链诊断卡**：每个候选位置一行、一行一个状态
///      （使用中 · 可写 / 备用 / 失败 + 具体原因）。产品明确要求写不了时不能只说「失败了」。
///   3. 导入结果不再用 MessageBox 报数，改成页内的「解析结果」块；按钮分三级，
///      「从 JSON 恢复」这类危险动作走行内二次确认。
///
/// **自检契约（Shot.cs 的 --import-test 直接依赖，签名与语义都不许变）**：
///   <see cref="ConfirmOverride"/> / <see cref="MsgOverride"/> /
///   <see cref="ConfirmButtons"/>（必须是 YesNo）/ <see cref="DoImport"/>
///   —— DoImport 必须仍是界面按钮回调走的那同一条代码路径。
/// </summary>
public sealed class DataView : ScrollViewer
{
    private readonly MainWindow _host;
    private readonly StackPanel _root;

    /// <summary>解析结果块所在卡片，还没导入过时整个隐藏（Collapsed，不占位）。</summary>
    private readonly Border _parseCard;
    private readonly StackPanel _parseHost;

    /// <summary>页内提示条。导入 / 导出 / 恢复的结果都写在这里，不弹 MessageBox。</summary>
    private readonly Border _statusCard;
    private readonly TextBlock _statusText;

    /// <summary>「从 JSON 恢复」的行内二次确认条（危险动作，不用 MessageBox）。</summary>
    private readonly Border _dangerConfirm;
    private readonly TextBlock _dangerText;
    private string _pendingRestore = "";

    /// <summary>这一次导入的来源文件名，只用来把结果块标题写清楚。自检直接调 DoImport 时为空。</summary>
    private string _importSource = "";

    /// <summary>
    /// 当前在场的数据页。页内提示要用它，而「还在场」只能靠 Loaded/Unloaded 跟：
    /// 元素被换掉之后 IsLoaded 仍然留 true，拿它当判据会静默吞掉提示。
    /// </summary>
    private static DataView? _live;
    private bool _attached;

    public DataView(MainWindow host)
    {
        _host = host;
        _root = new StackPanel { Margin = new Thickness(20, 18, 20, 24) };
        Content = _root;
        _live = this;
        Loaded += (_, _) => { _attached = true; _live = this; };
        Unloaded += (_, _) => _attached = false;

        // —— 抬头：与统计页同一套（eyebrow + 宋体大标题 + 等宽元信息）——
        _root.Children.Add(Head());

        // —— 常驻提示：真源是导出文件 ——
        _root.Children.Add(SourceNote());

        // —— 配色（色卡网格）——
        _root.Children.Add(Spacer(24));
        _root.Children.Add(Sect("配色", "底色固定，只差强调色",
            $"{Palettes.All.Length} 套 · 即时生效"));
        _root.Children.Add(PaletteGrid());
        _root.Children.Add(new TextBlock
        {
            Text = "底色都一样，区别只在强调色——方便直接比颜色。",
            FontSize = 11.5,
            FontFamily = M.Sans,
            Margin = new Thickness(2, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));

        // —— 纸底（浅 / 深）：单独一行，不跟色卡挤在同一排 ——
        // 图纸页脚第 8 条：它是「换纸」不是「换色」。混在一排里，用户会把深纸
        // 读成第七套配色，而这两个维度本来是正交的（换纸不动强调色，换色不动纸）。
        _root.Children.Add(Spacer(26));
        _root.Children.Add(Sect("纸底", "换纸，不是换色",
            $"{Theme.CurrentPaper.Name} · 即时生效"));
        _root.Children.Add(PaperRow());
        _root.Children.Add(new TextBlock
        {
            Text = "两套纸底共用同一组资源键，只换值；字体、圆角、动效时长不参与主题。" +
                   "深色靠描边分层，不靠投影。",
            FontSize = 11.5,
            FontFamily = M.Sans,
            Margin = new Thickness(2, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        // —— 数据存在哪（回退链诊断卡）——
        _root.Children.Add(Spacer(26));
        _root.Children.Add(Sect("数据存在哪", "回退链按实际状态列", null));
        _root.Children.Add(DataDirCard());

        // —— 导入与导出 ——
        _root.Children.Add(Spacer(26));
        _root.Children.Add(Sect("导入与导出", "结果写在页上，不弹窗",
            $"当前 {N(_host.Db.Events.Count)} 条"));
        _root.Children.Add(ActionRow());

        (_dangerConfirm, _dangerText) = DangerConfirm();
        _root.Children.Add(_dangerConfirm);

        (_statusCard, _statusText) = StatusBar();
        _root.Children.Add(_statusCard);

        (_parseCard, _parseHost) = ParseCard();
        _root.Children.Add(_parseCard);
    }

    /* ---------- 抬头 ---------- */

    /// <summary>
    /// 抬头三行：小字 eyebrow、宋体大标题、等宽元信息（文件名 / 大小 / 最后写入）。
    /// 大小和时间都取自己写的那个 db.json；读不到就如实说读不到，不编数字。
    /// </summary>
    private UIElement Head()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        sp.Children.Add(new TextBlock
        {
            Text = Eyebrow("STACKIT · 先备份，再谈别的"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));

        sp.Children.Add(new TextBlock
        {
            Text = "数据",
            FontFamily = M.Serif,
            FontSize = 27,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 5, 0, 0),
        }.Ref(Theme.Ink, TextBlock.ForegroundProperty));

        string meta;
        try
        {
            var fi = new FileInfo(Store.DbPath);
            meta = fi.Exists
                ? $"工作副本 {Path.GetFileName(Store.DbPath)} · {Human(fi.Length)} · " +
                  $"上次写入 {fi.LastWriteTime:yyyy-MM-dd HH:mm}"
                : $"工作副本 {Path.GetFileName(Store.DbPath)} · 还没写过（{Store.DataDir}）";
        }
        catch (Exception ex)
        {
            meta = $"工作副本 db.json · 读不到大小（{ex.GetType().Name}）";
        }

        sp.Children.Add(new TextBlock
        {
            Text = meta,
            FontFamily = M.Mono,
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));

        return sp;
    }

    /// <summary>
    /// 常驻提示 + 上次导出时间。
    ///
    /// prefs.json 里现在只有 palette 一个字段，所以「上次导出」在本机确实是**没记过**的。
    /// 这里读得到字段就显示真实值，读不到就写「未记录」，绝不编一个天数出来。
    /// </summary>
    private UIElement SourceNote()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "◆",
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
        }.Ref(Theme.Accent, TextBlock.ForegroundProperty));

        var doc = new TextBlock
        {
            FontSize = 11.5,
            FontFamily = M.Sans,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        doc.Inlines.Add(new System.Windows.Documents.Run("真源是导出文件，db.json 是工作副本。")
        { FontWeight = FontWeights.Bold });
        doc.Inlines.Add(new System.Windows.Documents.Run(
            $" 上次导出：{LastExportText()} · 数据页有一键导出"));
        doc.SetResourceReference(TextBlock.ForegroundProperty, Theme.AccentDeep);
        row.Children.Add(doc);

        var b = new Border
        {
            Child = row,
            CornerRadius = M.RSm,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
        };
        b.SetResourceReference(Border.BackgroundProperty, Theme.AccentSoft);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        return b;
    }

    private static string LastExportText()
    {
        try
        {
            var path = Store.PrefsPath;
            if (!File.Exists(path)) return "未记录";
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(path, System.Text.Encoding.UTF8));
            foreach (var key in new[] { "lastExport", "lastExportAt", "exportedAt" })
                if (doc.RootElement.TryGetProperty(key, out var v))
                {
                    var s = v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() : v.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
        }
        catch
        {
            // 读偏好失败不算错：这一行只是把「有没有记过导出」说清楚。
        }
        return "未记录（prefs 里只有配色）";
    }

    /* ---------- 配色：色卡网格 ---------- */

    /// <summary>2 列 × 3 行。列宽相等，六张卡并排直接比颜色。</summary>
    private UIElement PaletteGrid()
    {
        var grid = new UniformGrid { Columns = 2, Rows = 3 };
        // 摆的是**当前这张纸上**的六套：色带要是浅纸那组值，切到深纸就全错。
        foreach (var p in Palettes.For(Theme.Kind)) grid.Children.Add(PaletteCard(p));
        return grid;
    }

    /// <summary>
    /// 一张色卡：三条色带（AccentDeep / Accent / AccentSoft，就是效果图上那三条）
    /// + 宋体名称 + 那套的「感觉」文案。
    ///
    /// 色带必须是**该套 Palette 的字面值**而不是 DynamicResource：六套并排摆着，
    /// 每张卡的带本来就该是它自己那套的颜色，跟着当前配色换反倒全错。
    /// 名称 / 说明 / 环这些界面色照旧走资源键。
    /// </summary>
    private UIElement PaletteCard(Palette p)
    {
        var on = Theme.Current.Id == p.Id;

        var body = new StackPanel();

        var bars = new Grid { Height = 34 };
        for (var i = 0; i < 3; i++)
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        void Bar(Color c, int col)
        {
            var r = new Border { Background = new SolidColorBrush(c) };
            Grid.SetColumn(r, col);
            bars.Children.Add(r);
        }
        Bar(p.AccentDeep, 0);   // 承载文字 / 主按钮
        Bar(p.Accent, 1);       // 交互前景
        Bar(p.AccentSoft, 2);   // 浅底
        body.Children.Add(bars);

        var bd = new Grid { Margin = new Thickness(11, 9, 11, 11) };
        bd.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        bd.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            FontFamily = M.Serif,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.Inlines.Add(new System.Windows.Documents.Run(p.Name));
        if (on)
            name.Inlines.Add(new System.Windows.Documents.Run(" · 使用中")
            {
                FontFamily = M.Sans,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
            });
        // Foreground 必须在 Inlines 排完之后设：TextBlock.Foreground 是**继承属性**，
        // 设在 TextBlock 上会把 Run 一起抢过去，名称就会跟着当前配色跳（六套并排时全错）。
        name.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);

        var vibe = new TextBlock
        {
            Text = p.Vibe,
            FontFamily = M.Serif,
            FontSize = 10.5,
            FontStyle = FontStyles.Italic,
            LineHeight = 15,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        vibe.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);

        var txt = new StackPanel();
        txt.Children.Add(name);
        txt.Children.Add(vibe);
        Grid.SetColumn(txt, 0);
        bd.Children.Add(txt);

        if (on)
        {
            var ck = new TextBlock
            {
                Text = "✓",
                FontFamily = M.Mono,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 1, 0, 0),
            };
            ck.SetResourceReference(TextBlock.ForegroundProperty, Theme.AccentDeep);
            Grid.SetColumn(ck, 1);
            bd.Children.Add(ck);
        }

        body.Children.Add(bd);

        var card = new Border
        {
            Margin = new Thickness(2),
            CornerRadius = M.RMd,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Background = new SolidColorBrush(p.Surface),
            Child = body,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
        };
        card.SetResourceReference(Border.BorderBrushProperty, on ? Theme.Accent : Theme.Line);
        // 这 6 张色卡常驻一个全透明的阴影层，是**故意**的例外：
        // 数量固定为 6、不随数据增长，常驻的代价可以忽略；而列表里的行有几百条，
        // 常驻就等于每帧都为它们走一遍位图特效管线（opacity=0 也不例外），
        // 所以 Hover.Shift 那条路径改成了「hover 才挂、淡出后摘」，见 Interactions.State。
        // 这里保留常驻，是因为卡片要连外圈环一起抬起、动画挂在 cell 上，
        // 换成按需挂载会让 hover 那一刻整棵子树重新光栅化，反而更抖。
        card.Effect = M.ShadowLayer();

        // 外圈 2px 环。厚度恒为 2（不选中时 Collapsed 而不是 0 厚度），
        // 每张卡的尺寸就完全一致，选中与否不会让网格抖一下。
        var ring = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = RMdPlus,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Opacity = .22,
            Visibility = on ? Visibility.Visible : Visibility.Collapsed,
        };
        ring.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);

        // 抬起 2px 的是**整张卡连那圈环**：只抬内层会露出环，像卡从环里滑出去。
        var cell = new Grid { Margin = new Thickness(3) };

        void Enter()
        {
            card.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            Motion.ShiftY(cell, -2);
            Motion.Shadow(card.Effect as DropShadowEffect, 1.0);
        }
        void Leave()
        {
            card.SetResourceReference(Border.BorderBrushProperty, on ? Theme.Accent : Theme.Line);
            Motion.ShiftY(cell, 0);
            Motion.Shadow(card.Effect as DropShadowEffect, 0.0);
        }

        card.MouseEnter += (_, _) => Enter();
        card.MouseLeave += (_, _) => Leave();
        card.LostMouseCapture += (_, _) => { if (!card.IsMouseOver) Leave(); };
        card.MouseLeftButtonUp += (_, _) => Pick(p);
        card.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
                Pick(p);
        };
        StackIt.App.UI.Focus.SetRing(card, true);

        cell.Children.Add(card);
        cell.Children.Add(ring);
        return cell;
    }

    private static readonly CornerRadius RMdPlus = new(M.RMd.TopLeft + 2);

    /// <summary>1 星号的列宽。写 GridLength.Star 编译不过（这个静态成员在 WPF 里不存在）。</summary>
    private static readonly GridLength Star = new(1, GridUnitType.Star);

    /// <summary>点色卡：立即生效并持久化（Theme.Apply 里就是 Store.SavePalette）。</summary>
    private void Pick(Palette p)
    {
        Theme.Apply(p.Id);
        _host.Refresh();
    }

    /* ---------- 纸底：浅 / 深（与六套强调色正交的另一维） ---------- */

    /// <summary>
    /// 单独一行、两个芯片：浅 / 深。
    ///
    /// 为什么不做成「太阳/月亮」图标开关：那种控件只说"翻一下"，不说翻完是什么样。
    /// 这里每个芯片带一小条**纸样**（纸 / 卡片 / hover 三层，取那张纸的实色），
    /// 换之前就能把两张纸摆在一起比 —— 和上面六张色卡是同一个道理。
    /// </summary>
    private UIElement PaperRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(PaperChip(PaperKind.Light));
        row.Children.Add(Gap(8));
        row.Children.Add(PaperChip(PaperKind.Dark));
        return row;
    }

    private UIElement PaperChip(PaperKind kind)
    {
        var p = PaperTheme.Get(kind);
        var on = Theme.Kind == kind;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(PaperSample(p));
        // 文字不写颜色：套的是芯片样式，前景由它给（选中态是实底，字要跟着翻）。
        content.Children.Add(new TextBlock
        {
            Text = on ? $"{p.Name} · 使用中" : p.Name,
            FontFamily = M.Serif,
            FontSize = 12,
            FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });

        var b = new Button
        {
            Content = content,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = "paper|" + p.PrefId,
            ToolTip = $"{p.Vibe}\n纸 {Hex(p.Paper)} · 卡片 {Hex(p.Surface)} · hover {Hex(p.Surface2)}",
        };
        Interactions.ApplyStyle(b, on ? Interactions.ChipOn : Interactions.Chip);
        // 只换纸，不换强调色：Theme.ApplyPaper 里会重登记整组键并落 prefs。
        // 重画由 MainWindow 订阅的 Theme.PaperChanged 统一做（这一页也在其中）。
        b.Click += (_, _) => Theme.ApplyPaper(kind);
        StackIt.App.UI.Focus.SetRing(b, true);
        return b;
    }

    /// <summary>
    /// 一张纸的三层样品。颜色必须是**那张纸的字面值**而不是 DynamicResource：
    /// 两个芯片并排摆着，各自都该是自己的颜色。
    /// </summary>
    private static UIElement PaperSample(PaperTheme p)
    {
        var g = new Grid { Width = 33, Height = 12 };
        for (var i = 0; i < 3; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        void Stop(Color c, int col)
        {
            var r = new Border { Background = new SolidColorBrush(c) };
            Grid.SetColumn(r, col);
            g.Children.Add(r);
        }
        Stop(p.Paper, 0);
        Stop(p.Surface, 1);
        Stop(p.Surface2, 2);

        return new Border
        {
            Child = g,
            CornerRadius = M.RXs,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        }.Ref(Theme.Line, Border.BorderBrushProperty);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /* ---------- 数据存在哪：回退链诊断卡 ---------- */

    /// <summary>
    /// 候选位置的标签，顺序与 Store.Candidates() 一致。
    /// Core 不动，所以这里只能镜像它的优先级；真实路径按同名环境变量取，
    /// 环境变量没设时这三行本来就不可用，如实写成「未配置」。
    /// </summary>
    private static readonly (string Label, string Why, string Env)[] CandidateSpecs =
    {
        ("环境变量 STACKIT_DATA_DIR", "环境变量 STACKIT_DATA_DIR", "STACKIT_DATA_DIR"),
        ("%APPDATA%\\StackIt", "%APPDATA%", "APPDATA"),
        ("<程序目录>\\data", "程序所在目录", ""),
        ("%LOCALAPPDATA%\\StackIt", "%LOCALAPPDATA%", "LOCALAPPDATA"),
        ("文档\\StackIt", "文档目录", ""),
        ("%TEMP%\\StackIt", "临时目录", "TEMP"),
    };

    /// <summary>
    /// 回退链诊断卡：按 Store 的优先级一个位置一行，一行一个状态。
    /// 写不了时把**每个位置的具体原因**列出来（不只说「失败了」）——
    /// 用户要的是知道该改哪一个，而不是只知道坏了。
    /// </summary>
    private UIElement DataDirCard()
    {
        var sp = new StackPanel();
        var problem = Store.DataDirProblem;
        var allBad = problem is not null;
        var used = Store.DataDir;

        var rows = new List<(string Label, string Path, string Why, bool Available)>();
        foreach (var (label, why, env) in CandidateSpecs)
        {
            var dir = DirFor(why, env);
            rows.Add((label, dir ?? "", why, dir is not null));
        }

        var chosen = rows.FindIndex(r => r.Available && Same(r.Path, used));
        if (allBad) chosen = -1;
        if (chosen < 0 && !allBad)
        {
            // 认不出「使用中」这一行（多半是 STACKIT_DATA_DIR 的展开形式对不上）：
            // 宁可直接把当前目录补成首行说明，也不要在别的位置上打「使用中」。
            sp.Children.Add(RowRow($"db.json（当前数据目录 {Path.GetFileName(used)}）",
                used, "使用中 · 可写",
                Theme.AccentDeep, Theme.AccentSoft, true, null));
        }

        for (var i = 0; i < rows.Count; i++)
        {
            // 不可用的位置（环境变量没设）在「能用」的情况下直接不列 ——
            // 列出来只会让人以为它也是个备胎；全写不了时则一个都不漏，
            // 因为那时每一行为什么失败都是用户要看的信息。
            if (!rows[i].Available && !allBad) continue;

            string st, stFg, stBg;
            string? reason = null;

            if (!allBad && i == chosen)
            {
                st = "使用中 · 可写"; stFg = Theme.AccentDeep; stBg = Theme.AccentSoft;
            }
            else if (allBad || (chosen >= 0 && i < chosen))
            {
                // Store 是从前往后试的：排在使用中之前的位置都真失败过。
                st = "失败"; stFg = Theme.Danger; stBg = Theme.DangerSoft;
                reason = FailReason(problem, rows[i].Why, rows[i].Path) ?? ProbeReason(rows[i].Path);
            }
            else
            {
                st = "备用"; stFg = Theme.Ink3; stBg = Theme.Surface;
            }

            sp.Children.Add(RowRow(rows[i].Label, rows[i].Path, st, stFg, stBg,
                !allBad && i == chosen, reason));
        }

        if (allBad)
            sp.Children.Add(AllBadNote(problem!));

        sp.Children.Add(DbLine());

        var acts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };
        acts.Children.Add(Sec(Store.DataDirProblem is null ? "复制路径" : "复制这个位置",
            CopyPath, "copy"));
        acts.Children.Add(Gap(8));
        acts.Children.Add(Txt("打开文件夹", OpenFolder, "open"));
        sp.Children.Add(acts);

        return Plain(inner: sp);
    }

    /// <summary>Store 的每个候选都是无条件 CreateDirectory 的，所以这里照同一个规则推。</summary>
    private static string? DirFor(string why, string env)
    {
        var v = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(v))
            return why == "程序所在目录" ? Path.Combine(AppContext.BaseDirectory, "data") : null;
        return why switch
        {
            "环境变量 STACKIT_DATA_DIR" => Path.GetFullPath(v),
            "%APPDATA%" => v,
            "%LOCALAPPDATA%" => Path.Combine(v, "StackIt"),
            "临时目录" => Path.Combine(v.TrimEnd('\\', '/'), "StackIt"),
            _ => v,
        };
    }

    /// <summary>
    /// 诊断卡里的一行：名称 / 路径 / 状态徽标，失败时下面追一条具体原因。
    /// 徽标永远带文字（不只靠颜色），色只用来加深「哪一个坏了」。
    /// </summary>
    private static UIElement RowRow(string label, string dir, string st,
        string stFg, string stBg, bool now, string? reason)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 0, now ? 2 : 9) };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        left.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = M.Sans,
            FontSize = 11.5,
            FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal,
        }.Ref(now ? Theme.Ink : Theme.Ink2, TextBlock.ForegroundProperty));
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(dir) ? "（本机没有这个位置）" : dir,
            FontFamily = M.Mono,
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        Grid.SetColumn(left, 0);
        g.Children.Add(left);

        var badge = new Border
        {
            CornerRadius = M.RXs,
            Padding = new Thickness(6, 2, 6, 2),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = st,
                FontSize = 10,
                FontFamily = M.Sans,
                FontWeight = FontWeights.SemiBold,
            }.Ref(stFg, TextBlock.ForegroundProperty),
        };
        badge.SetResourceReference(Border.BackgroundProperty, stBg);
        badge.SetResourceReference(Border.BorderBrushProperty,
            stBg == Theme.Surface ? Theme.Line : stBg);
        Grid.SetColumn(badge, 1);
        g.Children.Add(badge);

        cell.Children.Add(g);

        if (reason is not null)
        {
            var why = new TextBlock
            {
                Text = "↳ " + reason,
                FontFamily = M.Mono,
                FontSize = 10.5,
                LineHeight = 15,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            why.SetResourceReference(TextBlock.ForegroundProperty, Theme.Danger);
            cell.Children.Add(why);
        }

        return cell;
    }

    /// <summary>所有位置都写不了时的整块说明：位置清单在上面每行已经给了，这里只说下一步。</summary>
    private static UIElement AllBadNote(string problem)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "⚠ 所有位置都写不了，现在只能看不能记。每一条的具体原因已经列在上面。",
            FontFamily = M.Sans,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Danger, TextBlock.ForegroundProperty));

        if (!string.IsNullOrWhiteSpace(problem))
            sp.Children.Add(new TextBlock
            {
                Text = problem.Replace("\n", "\n"),
                FontFamily = M.Mono,
                FontSize = 10.5,
                LineHeight = 15,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            }.Ref(Theme.Ink2, TextBlock.ForegroundProperty));

        sp.Children.Add(new TextBlock
        {
            Text = "可以设环境变量 STACKIT_DATA_DIR 指一个能写的目录，再重新打开程序。",
            FontFamily = M.Sans,
            FontSize = 11,
            LineHeight = 16,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Ink2, TextBlock.ForegroundProperty));

        var b = new Border
        {
            Child = sp,
            CornerRadius = M.RSm,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 4, 0, 0),
        };
        b.SetResourceReference(Border.BackgroundProperty, Theme.DangerSoft);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Danger);
        return b;
    }

    /// <summary>
    /// 从 Store 记下的失败原因里取这个位置那一条。
    /// 格式（ResolveDir 拼的）：「· {为什么}：{路径}\n  {异常消息}」，条目之间换行分隔。
    /// </summary>
    private static string? FailReason(string? problem, string why, string dir)
    {
        if (string.IsNullOrWhiteSpace(problem)) return null;

        var blocks = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var line in problem!.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("· "))
            {
                if (cur.Length > 0) blocks.Add(cur.ToString());
                cur = new System.Text.StringBuilder(t);
            }
            else if (cur.Length > 0) cur.Append('\n').Append(t.Trim());
        }
        if (cur.Length > 0) blocks.Add(cur.ToString());

        // 先按位置标签命中，命中不了再按路径命中（路径可能大小写不同 / 结尾多个分隔符）。
        var hit = blocks.FirstOrDefault(b => b.Contains($"：{dir}", StringComparison.OrdinalIgnoreCase))
                  ?? blocks.FirstOrDefault(b =>
                      b.Contains($"{why}：", StringComparison.OrdinalIgnoreCase)
                      || b.Contains($"{why} :", StringComparison.OrdinalIgnoreCase));
        if (hit is null) return null;

        var rest = hit;
        var sep = hit.IndexOf('：');
        if (sep > 0 && sep + 1 < hit.Length) rest = hit.Substring(sep + 1).TrimStart();
        return rest.Length > 0 ? rest.Replace("\n", " ") : null;
    }

    /// <summary>
    /// Store 没留下原因时（退到备用目录的情况）界面自己复现一次它的探测，取真实异常。
    ///
    /// 只在「这个位置排在能用那个之前」时跑 —— 也就是 Store 本来就写不进来的目录，
    /// 所以这里几乎不会真的落文件；仍写完就删、并兜住一切异常。
    /// 绝不猜原因：拿不到就老实说原因没被记下来。
    /// </summary>
    private static string? ProbeReason(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "这个位置在本机不存在";
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".stackit-diag-probe");
            var leftover = File.Exists(probe);
            File.WriteAllText(probe, "ok");
            var msg = leftover
                ? "上一次探测留下的 .write-test 还在，这个目录可能被人占着"
                : "写得进去（启动时探测失败的原因已经不复存在）";
            try { File.Delete(probe); } catch { /* 删不掉不影响结论 */ }
            return msg;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}：{ex.Message}";
        }
    }

    /// <summary>库文件的大小与最后写入时间 —— 备份时认的就是这两个数。</summary>
    private UIElement DbLine()
    {
        var g = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var f = new TextBlock
        {
            FontFamily = M.Sans,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        f.Inlines.Add(new System.Windows.Documents.Run("库文件 "));
        f.Inlines.Add(new System.Windows.Documents.Run(DbStat()));
        f.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink2);
        Grid.SetColumn(f, 0);
        g.Children.Add(f);

        var b = new TextBlock
        {
            Text = Store.DataDirProblem is null ? "可写" : "写不了",
            FontFamily = M.Sans,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(TextBlock.ForegroundProperty,
            Store.DataDirProblem is null ? Theme.AccentDeep : Theme.Danger);
        Grid.SetColumn(b, 1);
        g.Children.Add(b);

        var hair = new Border { Height = 1, Margin = new Thickness(0, 11, 0, 0) };
        hair.SetResourceReference(Border.BackgroundProperty, Theme.LineSoft);

        var sp = new StackPanel();
        sp.Children.Add(hair);
        sp.Children.Add(g);
        return sp;
    }

    private static string DbStat()
    {
        try
        {
            var fi = new FileInfo(Store.DbPath);
            if (!fi.Exists) return "还没生成（一条记录都还没落盘）";
            return $"{Human(fi.Length)} · 最后写入 {fi.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            return $"读不到（{ex.GetType().Name}）";
        }
    }

    private void CopyPath()
    {
        var text = Store.DbPath;
        try
        {
            System.Windows.Clipboard.SetText(text);
            Say($"已复制路径：\n{text}", false);
        }
        catch (Exception ex)
        {
            // 剪贴板被别的程序占着时会抛，不是「用户没点到」，要说清楚。
            Say($"复制失败：{ex.GetType().Name}：{ex.Message}\n请手动复制：\n{text}", true);
        }
    }

    private void OpenFolder()
    {
        var dir = Path.GetFullPath(Store.DataDir);
        try
        {
            if (!Directory.Exists(dir))
            {
                Say($"这个目录还不存在：\n{dir}", true);
                return;
            }
            _ = System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
            Say($"已在资源管理器里打开：\n{dir}", false);
        }
        catch (Exception ex)
        {
            Say($"打不开文件夹：{ex.GetType().Name}：{ex.Message}\n{dir}", true);
        }
    }

    /* ---------- 导入 / 导出 / 恢复 ---------- */

    /// <summary>
    /// 一排行动按钮，分三级：主（导入）/ 次（两个导出）/ 文字级危险（恢复）。
    /// 之前三个导出跟主按钮同权重（台账 P2）—— 主次不分等于没有主次。
    /// </summary>
    private UIElement ActionRow()
    {
        var wrap = new WrapPanel();

        wrap.Children.Add(Pri("选择 记录.md 导入", ChooseAndImport, "import"));
        wrap.Children.Add(Gap(8));
        wrap.Children.Add(Sec("导出 JSON", () => Save(asMarkdown: false), "export-json"));
        wrap.Children.Add(Gap(8));
        wrap.Children.Add(Sec("导出 Markdown", () => Save(asMarkdown: true), "export-md"));
        wrap.Children.Add(Gap(8));
        wrap.Children.Add(TextDanger("从 JSON 恢复", AskRestore, "restore"));

        return wrap;
    }

    private void ChooseAndImport()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Markdown 文件|*.md|所有文件|*.*",
            Title = "选择 记录.md",
        };
        if (dlg.ShowDialog() != true) return;

        // 文件选好之后的逻辑全在 DoImport 里，自检走的是同一条路径。
        // 之前自检自己复制了一遍流程，结果漏掉了确认框那个 bug。
        _importSource = Path.GetFileName(dlg.FileName);
        DoImport(() => File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
    }

    /// <summary>
    /// 导入的完整逻辑，与界面无关，便于自检直接调用。
    /// readSource 负责拿到 md 文本（界面上是刚选的文件）。
    ///
    /// 结果播报改到页内的「解析结果」块（RenderParse），确认框照旧：
    /// **这条路径就是 --import-test 测的那一条**，返回值语义与调用处都不能动。
    /// </summary>
    internal void DoImport(Func<string> readSource)
    {
        try
        {
            var md = readSource();
            var result = Importer.ParseMarkdown(md);
            if (result.Db.Events.Count == 0)
            {
                _parseCard.Visibility = Visibility.Collapsed;
                Say("没解析出任何记录，请确认内容是否为 记录.md 的表格。", true);
                return;
            }

            // 先把结果写在页上，再弹确认：确认的时候用户手上有完整数字，
            // 而不是只看到「解析出 N 条」这一句。
            RenderParse(result.Report);

            if (!Confirm($"解析出 {result.Report.Events} 条记录，覆盖 {result.Report.Weeks} 周。\n\n" +
                         $"这会覆盖当前 {N(_host.Db.Events.Count)} 条数据，继续？\n\n" +
                         "覆盖前会自动把当前数据备份到 backups 目录（最多留 10 份）。"))
            {
                Say("已取消，当前数据没有变动。上面的解析结果留着可以对着看。", false);
                return;
            }

            ApplyImport(result);
        }
        catch (Exception ex)
        {
            Say("导入失败：" + ex.Message, true);
        }
    }

    /// <summary>
    /// 确认之后的落盘与跳转。
    /// quiet=true 时不报完成提示，给自动化自检用（否则会卡在模态框上）。
    /// </summary>
    internal void ApplyImport(Core.ParseResult result, bool quiet = false)
    {
        _host.ReplaceDb(result.Db);

        if (!quiet)
        {
            // 数字主体已经在页内的「解析结果」块里；这一句是提示通路的收尾
            // （自检靠 MsgOverride 确认提示真的发出来了）。
            Say($"导入完成：{result.Report.Events} 条记录\n\n" +
                $"周表 {result.Report.Weeks} 张，周一校验 {result.Report.MondayOk}/{result.Report.Weeks} 通过\n" +
                $"笔误修正 {result.Report.Corrections.Count} 处，无时间条目 {result.Report.NoTimeCount} 条\n" +
                (result.Report.UnknownTokens.Count > 0
                    ? "未识别：" + string.Join("、", result.Report.UnknownTokens.Select(t => t.Token))
                    : "没有未识别的 token"), false);
        }

        // 直接跳统计页。数据页本来就不显示记录，导入完停在这里，
        // 用户会觉得"导入了但没数据"——要的就是立刻看见结果。
        _host.GoToDeferred("stats");
    }

    /// <summary>解析结果卡的空壳。没导入过时Collapsed，不占位。</summary>
    private static (Border, StackPanel) ParseCard()
    {
        var host = new StackPanel();
        var card = new Border
        {
            CornerRadius = M.RMd,
            Padding = new Thickness(14, 13, 14, 13),
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = host,
            Effect = M.CardShadow(),
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        return (card, host);
    }

    /// <summary>
    /// 把 Importer 的 report 原样列成页内的「解析结果」块。
    /// 每一个数都直接取 <see cref="ParseReport"/> 的字段，界面不另算一遍
    /// （自己算过一次就会有两套会各自漂移的口径）。
    /// </summary>
    private void RenderParse(Core.ParseReport r)
    {
        _parseHost.Children.Clear();

        // 标题行：解析结果 / 来源与周表数 / 右端一句总体判断。
        var head = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "解析结果",
            FontFamily = M.Serif,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        var src = new TextBlock
        {
            Text = $"  {(_importSource.Length > 0 ? _importSource : "导入的文本")}" +
                   $" · {r.Weeks} 张周表",
            FontFamily = M.Mono,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        src.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        Grid.SetColumn(src, 1);
        head.Children.Add(src);

        var ok = new TextBlock
        {
            Text = r.SkippedLines.Count == 0
                ? "✓ 0 条静默丢弃"
                : $"! {r.SkippedLines.Count} 行没解析出来",
            FontFamily = M.Mono,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(ok, 2);
        ok.SetResourceReference(TextBlock.ForegroundProperty,
            r.SkippedLines.Count == 0 ? Theme.AccentDeep : Theme.Danger);
        head.Children.Add(ok);

        _parseHost.Children.Add(head);

        Kv("识别事件", $"{N(r.Events)} 条", r.Events == 0);
        Kv("周表", $"{r.Weeks} 张", r.Weeks == 0);
        Kv("周一校验（年份靠它反推）", $"{r.MondayOk} / {r.Weeks}", r.MondayOk < r.Weeks);
        Kv("日期修正", r.Corrections.Count == 0
                ? "0 处"
                : "行 " + string.Join(" / ", r.Corrections.Select(c => c.Line)),
            r.Corrections.Count == 0);
        Kv("无时间条目 · precision=day", $"{r.NoTimeCount} 条", r.NoTimeCount == 0);
        Kv("未识别 token", $"{r.UnknownTokens.Count} 个", r.UnknownTokens.Count == 0);

        if (r.UnknownTokens.Count > 0)
            _parseHost.Children.Add(new TextBlock
            {
                Text = "未识别：" + string.Join("、",
                    r.UnknownTokens.Select(t => $"{t.Token}×{t.Count}")),
                FontFamily = M.Mono,
                FontSize = 10.5,
                LineHeight = 15,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            }.Ref(Theme.Danger, TextBlock.ForegroundProperty));

        if (r.SkippedLines.Count > 0)
            _parseHost.Children.Add(new TextBlock
            {
                Text = "没解析出来的行号：" + string.Join(" / ", r.SkippedLines.Take(24)),
                FontFamily = M.Mono,
                FontSize = 10.5,
                LineHeight = 15,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            }.Ref(Theme.Danger, TextBlock.ForegroundProperty));

        _parseCard.Visibility = Visibility.Visible;
    }

    /// <summary>结果块里的一行：左标签右数字（等宽 tabular）。warn=true 时数字用强调深色提示要看一眼。</summary>
    private void Kv(string k, string v, bool warn)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var l = new TextBlock
        {
            Text = k,
            FontFamily = M.Sans,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        l.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink2);
        Grid.SetColumn(l, 0);
        row.Children.Add(l);

        var r = new TextBlock
        {
            Text = v,
            FontFamily = M.Mono,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        r.SetResourceReference(TextBlock.ForegroundProperty, warn ? Theme.AccentDeep : Theme.Ink);
        Tone.SetTabular(r, true);
        Grid.SetColumn(r, 1);
        row.Children.Add(r);

        var wrap = new StackPanel();
        wrap.Children.Add(row);
        var hair = new Border { Height = 1, Margin = new Thickness(0, 7, 0, 0) };
        hair.SetResourceReference(Border.BackgroundProperty, Theme.LineSoft);
        wrap.Children.Add(hair);

        _parseHost.Children.Add(wrap);
    }

    private void Save(bool asMarkdown)
    {
        var dlg = new SaveFileDialog
        {
            FileName = asMarkdown
                ? $"服用记录-{DateTime.Now:yyyy-MM-dd}.md"
                : $"stackit-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = asMarkdown ? "Markdown|*.md" : "JSON|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _host.Export(dlg.FileName, asMarkdown);
            Say($"已导出到：\n{dlg.FileName}", false);
        }
        catch (Exception ex)
        {
            Say("导出失败：" + ex.Message, true);
        }
    }

    /// <summary>
    /// 行内二次确认条（危险动作）。默认收起，点了「从 JSON 恢复」并选好文件之后才出现。
    ///
    /// 不用 MessageBox：覆盖是不可逆的，「弹窗点确定」那种确认什么都没说清；
    /// 行内这条会把**要覆盖多少条、来源是哪个文件**写在按钮旁边。
    /// </summary>
    private (Border, TextBlock) DangerConfirm()
    {
        var text = new TextBlock
        {
            FontFamily = M.Sans,
            FontSize = 11.5,
            LineHeight = 17,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Danger);

        var yes = TextDanger("确认覆盖", DoRestore, "restore-yes");
        yes.Margin = new Thickness(0);
        var no = Btn("取消", CancelRestore, Interactions.ButtonText, "restore-no");
        no.Margin = new Thickness(6, 0, 0, 0);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        inner.Children.Add(text);
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btns.Children.Add(yes);
        btns.Children.Add(no);
        Grid.SetColumn(btns, 1);
        inner.Children.Add(btns);

        var b = new Border
        {
            Child = inner,
            CornerRadius = M.RSm,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        b.SetResourceReference(Border.BackgroundProperty, Theme.DangerSoft);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Danger);
        return (b, text);
    }

    /// <summary>取消：清掉待恢复的路径，收起这一条。什么都没发生过。</summary>
    private void CancelRestore()
    {
        _pendingRestore = "";
        _dangerConfirm.Visibility = Visibility.Collapsed;
    }

    /// <summary>「从 JSON 恢复」第一步：只选文件，选完才弹行内确认。覆盖是不可逆的，所以不一步到位。</summary>
    private void AskRestore()
    {
        var dlg = new OpenFileDialog { Filter = "JSON|*.json", Title = "选择导出的 JSON" };
        if (dlg.ShowDialog() != true) return;
        _pendingRestore = dlg.FileName;
        _dangerText.Text = $"这会覆盖当前 {N(_host.Db.Events.Count)} 条数据。" +
                           $"来源：{Path.GetFileName(_pendingRestore)}";
        _dangerConfirm.Visibility = Visibility.Visible;
    }

    /// <summary>行内确认里点「确认覆盖」之后才真落盘。</summary>
    private void DoRestore()
    {
        var path = _pendingRestore;
        _pendingRestore = "";
        _dangerConfirm.Visibility = Visibility.Collapsed;
        try
        {
            var db = Store.FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));
            _host.ReplaceDb(db);
            _host.Refresh();
            Say($"已恢复 {db.Events.Count} 条记录。", false);
        }
        catch (Exception ex)
        {
            Say("恢复失败：" + ex.Message, true);
        }
    }

    /* ---------- 小工具（排版件全部本地实现，不依赖别的页面） ---------- */

    /// <summary>
    /// 提示。自检钩子优先（--import-test 靠它确认提示真的发出去了）；
    /// 界面里改成**写在页上**，不再弹 MessageBox 报数；页面已经不在场时才退回弹窗。
    /// </summary>
    private static void Say(string msg, bool bad)
    {
        if (MsgOverride is not null) { MsgOverride(msg); return; }
        var p = _live;
        if (p is not null && p._attached) { p.ShowStatus(msg, bad); return; }
        if (MainWindow.SuppressDialogs) return;
        System.Windows.MessageBox.Show(msg, "StackIt",
            MessageBoxButton.OK, bad ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>页内提示条。错误走行内危险色，不弹窗。</summary>
    private void ShowStatus(string msg, bool bad)
    {
        _statusText.Text = msg;
        _statusText.SetResourceReference(TextBlock.ForegroundProperty,
            bad ? Theme.Danger : Theme.Ink2);
        _statusCard.SetResourceReference(Border.BackgroundProperty,
            bad ? Theme.DangerSoft : Theme.AccentSoft);
        _statusCard.SetResourceReference(Border.BorderBrushProperty,
            bad ? Theme.Danger : Theme.Accent);
        _statusCard.Visibility = Visibility.Visible;
    }

    private static (Border, TextBlock) StatusBar()
    {
        var t = new TextBlock
        {
            FontFamily = M.Sans,
            FontSize = 11.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink2);

        var b = new Border
        {
            Child = t,
            CornerRadius = M.RSm,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        b.SetResourceReference(Border.BackgroundProperty, Theme.AccentSoft);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        return (b, t);
    }

    /// <summary>节标题：宋体 17 + 一句斜体补语 + 右侧等宽小字。与统计页同一层级。</summary>
    private static UIElement Sect(string title, string sub, string? right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t = new TextBlock
        {
            Text = title,
            FontFamily = M.Serif,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);
        Grid.SetColumn(t, 0);
        g.Children.Add(t);

        var q = new TextBlock
        {
            Text = sub,
            FontFamily = M.Serif,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(9, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        q.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        Grid.SetColumn(q, 1);
        g.Children.Add(q);

        if (right is not null)
        {
            var r = new TextBlock
            {
                Text = right,
                FontFamily = M.Mono,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            r.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
            Grid.SetColumn(r, 3);
            g.Children.Add(r);
        }

        return g;
    }

    /// <summary>容器卡片：样式键走 StackItCard，但关掉整卡抬起 —— 卡片不是可点的东西。</summary>
    private static Border Plain(UIElement inner)
    {
        var b = new Border();
        Hover.SetShift(b, false);
        Interactions.ApplyStyle(b, Interactions.Card);
        b.Child = inner;
        return b;
    }

    private static Button Btn(string text, Action? onClick, string styleKey, string tag)
    {
        var b = new Button
        {
            Content = text,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = tag,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Interactions.ApplyStyle(b, styleKey);
        if (onClick is not null) b.Click += (_, _) => onClick();
        return b;
    }

    private static Button Pri(string text, Action onClick, string tag) =>
        Btn(text, onClick, Interactions.Button, tag);
    private static Button Sec(string text, Action onClick, string tag) =>
        Btn(text, onClick, Interactions.ButtonSecondary, tag);
    private static Button Txt(string text, Action onClick, string tag) =>
        Btn(text, onClick, Interactions.ButtonText, tag);

    private static Button TextDanger(string text, Action? onClick, string tag)
    {
        var b = Btn(text, onClick, Interactions.ButtonDanger, tag);
        b.MinWidth = 0;
        return b;
    }

    private static FrameworkElement Gap(double w) =>
        new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = 1,
            Fill = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static FrameworkElement Spacer(double h) => new HeightGrid(h);

    private static string N(int v) => v.ToString("N0");

    private static string Human(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} B";

    private static bool Same(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// eyebrow 的字距。WPF 没有 letter-spacing，而 0.2em 这档间距是设计稿里
    /// 区分 eyebrow 和正文的唯一线索 —— 在 ASCII 字符之间插一个空格
    /// （CJK 本来就有 1em 的固有间距，不动）。
    /// </summary>
    private static string Eyebrow(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && c is not ' ' && s[i - 1] is not ' '
                && c < 0x80 && s[i - 1] < 0x80)
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /* ---------- 自检契约：以下四个成员一个都不许动 ---------- */

    /// <summary>
    /// 自检用的确认覆盖。非 null 时 Confirm 直接返回它，不弹框，
    /// 让自检能走完整个导入流程而不卡在模态框上。
    ///
    /// 注意它能测到的边界：覆盖生效时**不会执行**真正的 ShowDialog 那行，
    /// 所以「按钮类型与判定不匹配」这类错误它测不出来。
    /// 那道防线靠类型（Confirm 返回 bool，比错枚举编译不过）
    /// 加 ConfirmButtons 常量断言，不靠这个覆盖。
    /// </summary>
    internal static Func<string, bool>? ConfirmOverride;

    /// <summary>自检用的提示覆盖。非 null 时提示只走它，不弹窗（页内提示也不写）。</summary>
    internal static Action<string>? MsgOverride;

    /// <summary>
    /// 确认框的按钮类型。必须是 YesNo —— 判定读的是 MessageBoxResult.Yes，
    /// 换成 OKCancel 后返回值只有 OK/Cancel，判定永远为假。
    /// 单独提出来是为了让自检能断言它（这正是曾经静默失败的那个点）。
    /// </summary>
    internal const MessageBoxButton ConfirmButtons = MessageBoxButton.YesNo;

    /// <summary>
    /// 需要用户确认时用。返回 bool 而不是 MessageBoxResult：
    /// 调用处只要写 if (!Confirm(...)) 就不可能比错。
    ///
    /// 之前返回 MessageBoxResult 而弹窗用 OKCancel，调用处却比 Yes，
    /// 比较永远为假，导入被静默当成"用户点了取消"——看着像是没反应。
    /// </summary>
    private static bool Confirm(string msg) =>
        ConfirmOverride?.Invoke(msg) ??
        System.Windows.MessageBox.Show(msg, "StackIt",
            ConfirmButtons, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>固定高度占位，比设 Margin 直观。</summary>
    private sealed class HeightGrid : FrameworkElement
    {
        public HeightGrid(double h)
        {
            Height = h;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }
}

/// <summary>
/// 把颜色设成 DynamicResource（代码里的等价物 = SetResourceReference）的小助手。
/// 用实例取到的那个 Brush 从此留在旧色上 —— 切强调色就会出现「一半新一半旧」。
/// 只做 SetResourceReference 和返回自己，不在这里做任何算色。
/// </summary>
internal static class DataEl
{
    public static T Ref<T>(this T el, string key, DependencyProperty dp)
        where T : FrameworkElement
    {
        el.SetResourceReference(dp, key);
        return el;
    }
}
