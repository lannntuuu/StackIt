using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>
/// 品类清单、增删改与归档。每次写操作都经 ItemStore 立即落盘。
///
/// 视觉沿用数据页 / 统计页那套：暖纸底 + 宋体品名 + 等宽元信息 + 卡片描边 +
/// 状态徽标 + 行间发丝线。刻意不用原生 DataGrid —— 它的蓝灰网格跟纸感设计
/// 是两套语言，摆在同一屏里像从别的软件贴过来的。
/// </summary>
public sealed class ItemsView : ScrollViewer
{
    private static readonly string[] Groups = { "B族", "维C", "脂溶性", "矿物质", "脂肪酸", "外用", "其他" };
    private static readonly GridLength Star = new(1, GridUnitType.Star);

    private readonly MainWindow _host;
    private readonly List<Item> _items;
    private readonly StackPanel _root;
    private readonly TextBox _filter = new()
    {
        Width = 200, FontSize = 12, FontFamily = M.Sans,
        Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 5, 6, 5),
    };
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = new() { FontFamily = M.Mono, FontSize = 10.5 };
    private readonly Button _edit;
    private readonly Button _archive;
    private readonly Button _delete;
    private readonly Button _aliases;
    private readonly TextBlock _status = new() { FontSize = 11.5, FontFamily = M.Sans, TextWrapping = TextWrapping.Wrap };
    private Item? _selected;
    private readonly Dictionary<Item, Border> _rowOf = new();

    public ItemsView(MainWindow host)
    {
        _host = host;
        _items = ItemStore.Load();
        _root = new StackPanel { Margin = new Thickness(20, 18, 20, 24) };
        Content = _root;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ApplyScrollbarSkin();

        // —— 抬头：与统计页 / 数据页同一套（eyebrow + 宋体大标题 + 等宽元信息）——
        _root.Children.Add(Head());

        // —— 常驻提示：改动立即落盘 ——
        _root.Children.Add(Note());

        // —— 节标题 + 工具行 ——
        _root.Children.Add(new ItemsHeightGrid(24));
        _root.Children.Add(Sect("品类清单", "名称、别名、分组与默认剂量", null));

        var tools = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filterLabel = new TextBlock
        {
            Text = "筛选", FontSize = 11.5, FontFamily = M.Sans,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty);
        Grid.SetColumn(filterLabel, 0);
        tools.Children.Add(filterLabel);
        // 输入框皮：Border 提供纸感底 + 描边 + 圆角，TextBox 透明嵌在里面
        var filterSkin = new Border
        {
            CornerRadius = M.RSm, BorderThickness = new Thickness(1),
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Height = 30, Child = _filter,
        };
        filterSkin.SetResourceReference(Border.BackgroundProperty, Theme.Surface);
        filterSkin.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        _filter.TextChanged += (_, _) => RenderList();
        Grid.SetColumn(filterSkin, 1);
        tools.Children.Add(filterSkin);
        var add = Button("＋ 新增品类", OnAdd, Interactions.Button, "item-add");
        Grid.SetColumn(add, 3);
        tools.Children.Add(add);
        _root.Children.Add(tools);

        // —— 操作条：紧挨工具行，首屏可见 ——
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        _edit = SecBtn("编辑", OnEdit, "item-edit");
        _archive = SecBtn("归档", OnArchive, "item-archive");
        _aliases = SecBtn("别名管理", OnAliases, "item-aliases");
        _delete = Button("删除", OnDelete, Interactions.ButtonDanger, "item-delete");
        foreach (var b in new[] { _edit, _archive, _aliases, _delete })
        {
            b.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(b);
        }
        _root.Children.Add(actions);
        _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        _status.Margin = new Thickness(1, 2, 0, 10);
        _status.Text = "选择一行以编辑或管理别名；有历史记录的品类不能删除。";
        _root.Children.Add(_status);

        // —— 列表容器卡片 ——
        _root.Children.Add(Plain(_list));
        // 底部给固定底栏的投影留安全区，避免阴影压住最后一行
        _root.Children.Add(new ItemsHeightGrid(28));
        RenderList();
        UpdateButtons();
    }

    /* ---------- 抬头 ---------- */

    private UIElement Head()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(new TextBlock
        {
            Text = "STACKIT · ITEM LIBRARY",
            FontFamily = M.Mono, FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        sp.Children.Add(new TextBlock
        {
            Text = "品类管理", FontFamily = M.Serif, FontSize = 27,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 0),
        }.Ref(Theme.Ink, TextBlock.ForegroundProperty));
        string meta;
        try
        {
            var fi = new System.IO.FileInfo(ItemStore.ItemsPath);
            meta = fi.Exists
                ? $"配置 {System.IO.Path.GetFileName(ItemStore.ItemsPath)} · {fi.Length} B · 上次写入 {fi.LastWriteTime:yyyy-MM-dd HH:mm}"
                : $"配置 {System.IO.Path.GetFileName(ItemStore.ItemsPath)} · 还没写过（{Store.DataDir}）";
        }
        catch (Exception ex) { meta = $"配置 items.json · 读不到（{ex.GetType().Name}）"; }
        sp.Children.Add(new TextBlock
        {
            Text = meta, FontFamily = M.Mono, FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        return sp;
    }

    private UIElement Note()
    {
        // 不要用 Horizontal StackPanel 包可换行文本：水平栈给无限宽，Wrap 永不触发，
        // 长句溢出后被 Border 裁掉（曾把「重名。」两个字吃掉）。用两列 Grid。
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        var diamond = new TextBlock
        {
            Text = "◆", FontSize = 11.5, Margin = new Thickness(0, 1, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
        }.Ref(Theme.Accent, TextBlock.ForegroundProperty);
        Grid.SetColumn(diamond, 0);
        row.Children.Add(diamond);
        var doc = new TextBlock
        {
            FontSize = 11.5, FontFamily = M.Sans, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
        };
        doc.Inlines.Add(new System.Windows.Documents.Run("改动立即写进 items.json。")
        { FontWeight = FontWeights.Bold });
        doc.Inlines.Add(new System.Windows.Documents.Run(" 别名用于识别记录里的写法，不同品类不能重名。"));
        doc.SetResourceReference(TextBlock.ForegroundProperty, Theme.AccentDeep);
        Grid.SetColumn(doc, 1);
        row.Children.Add(doc);
        var b = new Border
        {
            Child = row, CornerRadius = M.RSm,
            BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 10, 12, 10),
        };
        b.SetResourceReference(Border.BackgroundProperty, Theme.AccentSoft);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        return b;
    }

    /* ---------- 列表 ---------- */

    private IEnumerable<Item> VisibleItems()
    {
        var k = _filter.Text.Trim();
        return _items.Where(i => k.Length == 0
            || i.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
            || i.Group.Contains(k, StringComparison.OrdinalIgnoreCase)
            || i.Aliases.Any(a => a.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    private void RenderList()
    {
        _rowOf.Clear();
        _list.Children.Clear();
        var visible = VisibleItems().ToList();
        _count.Text = $"{visible.Count} / {_items.Count}";

        if (visible.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "没有匹配的品类。", FontFamily = M.Sans, FontSize = 12,
                Margin = new Thickness(14, 14, 14, 14),
            }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
            return;
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var item = visible[i];
            var row = ItemRow(item, i > 0);
            _rowOf[item] = row;
            _list.Children.Add(row);
        }
        // 选中项若被筛掉，清掉选中态
        if (_selected is not null && !_rowOf.ContainsKey(_selected)) Select(null);
        else if (_selected is not null) PaintRow(_rowOf[_selected], true);
    }

    private Border ItemRow(Item item, bool withHair)
    {
        // 行 = 一条发丝线 + 一行内容，不再套一个带描边的圆角盒。
        // 参考页（数据页 RowRow / 统计页）都是"只有发丝线、没有行框"，
        // 之前"外卡片 → 行盒 → 内发丝线"四层框把版面做脏了。
        var cell = new StackPanel();
        if (withHair)
        {
            cell.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }.Ref(Theme.LineSoft, Border.BackgroundProperty));
        }

        var g = new Grid { Height = 52 }; // 固定行高，发丝线间距不跳动
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        // 组名列给固定宽并右对齐，消除右侧悬空槽，与卡片内边距对齐
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Margin = new Thickness(2, 0, 8, 0) };
        left.Children.Add(new TextBlock
        {
            Text = item.Name, FontFamily = M.Serif, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
        }.Ref(Theme.Ink, TextBlock.ForegroundProperty));
        var aliasText = item.Aliases.Count > 0
            ? "别名  " + string.Join(" · ", item.Aliases)
            : "别名  —";
        left.Children.Add(new TextBlock
        {
            Text = aliasText, FontFamily = M.Mono, FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = aliasText,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        Grid.SetColumn(left, 0);
        g.Children.Add(left);

        var group = new TextBlock
        {
            Text = item.Group, FontFamily = M.Sans, FontSize = 11,
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        }.Ref(Theme.Ink2, TextBlock.ForegroundProperty);
        Grid.SetColumn(group, 1);
        g.Children.Add(group);

        // 只有归档品类才显示徽标 —— "使用中"信息量为 0，8 枚绿胶囊排成一列反而压过品名。
        if (item.Archived)
        {
            var badge = new Border
            {
                CornerRadius = M.RXs, Padding = new Thickness(7, 3, 7, 3),
                BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = "已归档", FontFamily = M.Sans, FontSize = 10.5,
                },
            };
            PaintBadge(badge);
            Grid.SetColumn(badge, 2);
            g.Children.Add(badge);
        }

        cell.Children.Add(g);
        var row = new Border
        {
            Child = cell, CornerRadius = M.RXs,
            Padding = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand,
            Tag = item, BorderThickness = new Thickness(0),
        };
        Hover.SetShift(row, false); // 选中态自己管，不跟通用 hover 抬起
        PaintRow(row, ReferenceEquals(item, _selected));
        row.MouseLeftButtonUp += (_, e) =>
        {
            Select(item);
            if (e.ClickCount == 2) OpenEdit(item);
        };
        return row;
    }

    private void PaintRow(Border row, bool selected)
    {
        if (selected)
        {
            row.SetResourceReference(Border.BackgroundProperty, Theme.AccentSoft);
            row.BorderThickness = new Thickness(1);
            row.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.BorderThickness = new Thickness(0);
        }
    }

    private static void PaintBadge(Border badge)
    {
        badge.SetResourceReference(Border.BackgroundProperty, Theme.Surface2);
        badge.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        if (badge.Child is TextBlock t)
            t.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
    }

    private void Select(Item? item)
    {
        if (_selected is not null && _rowOf.TryGetValue(_selected, out var old)) PaintRow(old, false);
        _selected = item;
        if (item is not null && _rowOf.TryGetValue(item, out var now)) PaintRow(now, true);
        UpdateButtons();
    }

    private Item? Selected => _selected;
    private bool HasHistory(Item item) => _host.Db.Events.Any(e => string.Equals(e.ItemId, item.Id, StringComparison.Ordinal));

    private void UpdateButtons()
    {
        // 不用禁用态：主题的 StackItButtonSecondary/Danger 禁用色对比度只有 ~2.9:1，
        // 四颗按钮里三颗灰掉、唯独删除带红描边，读起来像「三个死了、删除活着」。
        // 全部保持可用，未选中时点一下给页内提示，层级一致。
        var item = Selected;
        _archive.Content = item?.Archived == true ? "取消归档" : "归档";
        _delete.ToolTip = item is not null && HasHistory(item) ? "此品类已有历史记录，只能归档" : null;
    }

    private bool RequireSelection()
    {
        if (Selected is not null) return true;
        Say("请先点选一行品类，再进行编辑 / 归档 / 别名 / 删除。", true);
        return false;
    }

    private void OnAdd(object s, RoutedEventArgs e) => OpenEdit(null);
    private void OnEdit(object s, RoutedEventArgs e) { if (RequireSelection() && Selected is Item item) OpenEdit(item); }

    private void OpenEdit(Item? existing)
    {
        var original = existing is null ? null : Clone(existing);
        var dialog = new ItemEditWindow(_host, existing, _items, original);
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        Save(() =>
        {
            if (existing is null) _items.Add(dialog.Result);
            else _items[_items.IndexOf(existing)] = dialog.Result;
            if (_host.Db.Items is not null)
            {
                var index = _host.Db.Items.FindIndex(i => i.Id == dialog.Result.Id);
                if (index >= 0) _host.Db.Items[index] = dialog.Result;
                else if (existing is null) _host.Db.Items.Add(dialog.Result);
            }
        });
        if (dialog.Result is not null) Select(dialog.Result);
    }

    private void OnArchive(object s, RoutedEventArgs e)
    {
        if (!RequireSelection() || Selected is not Item item) return;
        var archived = !item.Archived;
        if (archived && !Confirm($"归档“{item.Name}”？\n\n历史记录会保留，可随时取消归档。")) return;
        Save(() => item.Archived = archived);
    }

    private void OnDelete(object s, RoutedEventArgs e)
    {
        if (!RequireSelection() || Selected is not Item item) return;
        if (HasHistory(item)) { Say("此品类已有历史记录，只能归档，不能删除。", true); return; }
        if (!Confirm($"永久删除“{item.Name}”及其别名？\n\n此操作不可撤销。")) return;
        Save(() =>
        {
            _items.Remove(item);
            _host.Db.Items?.RemoveAll(i => i.Id == item.Id);
        });
        Select(null);
    }

    private void OnAliases(object s, RoutedEventArgs e)
    {
        if (!RequireSelection() || Selected is not Item item) return;
        var dialog = new AliasWindow(_host, item);
        if (dialog.ShowDialog() != true || dialog.Aliases is null) return;
        Save(() => item.Aliases = dialog.Aliases);
    }

    private void Save(Action change)
    {
        var backup = _items.Select(Clone).ToList();
        var dbBackup = _host.Db.Items?.Select(Clone).ToList();
        try
        {
            change();
            ItemStore.Save(_items);
            RenderList();
            Say("已保存到 items.json", false);
        }
        catch (Exception ex)
        {
            _items.Clear(); _items.AddRange(backup);
            _host.Db.Items = dbBackup;
            RenderList();
            Say("保存失败，已撤销本次修改：\n" + ex.Message, true);
        }
        UpdateButtons();
    }

    private void Say(string message, bool bad)
    {
        _status.Text = message;
        _status.SetResourceReference(TextBlock.ForegroundProperty, bad ? Theme.Danger : Theme.Ink3);
    }

    private bool Confirm(string text) => MainWindow.SuppressDialogs || MessageBox.Show(_host, text, "StackIt", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    internal static Item Clone(Item item) => new() { Id = item.Id, Name = item.Name, Aliases = new List<string>(item.Aliases), Route = item.Route, Group = item.Group, DefaultDose = item.DefaultDose, DoseUnit = item.DoseUnit, ActiveWindows = item.ActiveWindows.Select(w => new ActiveWindow { From = w.From, To = w.To, Note = w.Note }).ToList(), Archived = item.Archived, Color = item.Color, Icon = item.Icon, Notes = item.Notes };
    internal static IReadOnlyList<string> GroupChoices => Groups;

    /* ---------- 设计系统小助手（与 DataView 同一套）---------- */

    internal static Button Button(string text, RoutedEventHandler click, string style, string tag)
    {
        var b = new Button
        {
            Content = text, Cursor = Cursors.Hand, Tag = tag,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Interactions.ApplyStyle(b, style);
        UI.Focus.SetRing(b, true);
        b.Click += click;
        return b;
    }

    private static Button SecBtn(string text, RoutedEventHandler click, string tag) =>
        Button(text, click, Interactions.ButtonSecondary, tag);

    private UIElement Sect(string title, string sub, string? right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock
        {
            Text = title, FontFamily = M.Serif, FontSize = 17,
            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom,
        }.Ref(Theme.Ink, TextBlock.ForegroundProperty);
        Grid.SetColumn(t, 0); g.Children.Add(t);
        var q = new TextBlock
        {
            Text = sub, FontFamily = M.Serif, FontSize = 11, FontStyle = FontStyles.Italic,
            Margin = new Thickness(9, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        }.Ref(Theme.Ink3, TextBlock.ForegroundProperty);
        Grid.SetColumn(q, 1); g.Children.Add(q);
        // 右侧品类计数
        _count.FontFamily = M.Mono;
        _count.VerticalAlignment = VerticalAlignment.Bottom;
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        _count.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        Grid.SetColumn(_count, 3); g.Children.Add(_count);
        return g;
    }

    private static Border Plain(UIElement inner)
    {
        var b = new Border();
        Hover.SetShift(b, false);
        Interactions.ApplyStyle(b, Interactions.Card);
        b.Child = inner;
        return b;
    }

    /// <summary>
    /// 本页滚动条换皮：全项目没有 ScrollBar 样式，裸的 WPF 滚动条冷灰轨道闯进暖纸里
    /// 是全屏最刺眼的异物。用局部 Style 把轨道/滑块换成纸色与强调色，并去掉上下箭头。
    /// 只作用在ItemsView 自己，不动其它页面。
    /// </summary>
    private void ApplyScrollbarSkin()
    {
        const string xaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type ScrollBar}">
            <Setter Property="Width" Value="9"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="{x:Type ScrollBar}">
                        <Grid x:Name="Bg" Background="Transparent">
                            <Track x:Name="PART_Track" IsDirectionReversed="True">
                                <Track.Thumb>
                                    <Thumb>
                                        <Thumb.Template>
                                            <ControlTemplate TargetType="{x:Type Thumb}">
                                                <Border CornerRadius="4" Margin="1,0,1,0"
                                                        Background="{DynamicResource AccentSoft}"
                                                        BorderBrush="{DynamicResource Accent}"
                                                        BorderThickness="1"/>
                                            </ControlTemplate>
                                        </Thumb.Template>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;
        try
        {
            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            // ScrollViewer 内部生成的 ScrollBar 会沿逻辑树查资源，登记成默认样式即可吃到
            Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
        }
        catch
        {
            // 换皮失败就用系统默认，不影响功能
        }
    }
}

/// <summary>新增/编辑表单。Result 只在显式点保存后产生。</summary>
public sealed class ItemEditWindow : Window
{
    private readonly Item? _existing;
    private readonly List<Item> _all;
    private readonly TextBox _name = new();
    private readonly ComboBox _group = new();
    private readonly ComboBox _route = new();
    private readonly TextBox _dose = new();
    private readonly TextBox _unit = new();
    private readonly TextBox _color = new();
    private readonly TextBox _icon = new();
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 55 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
    public Item? Result { get; private set; }

    public ItemEditWindow(MainWindow host, Item? existing, List<Item> all, Item? original)
    {
        _existing = existing; _all = all;
        Title = existing is null ? "新增品类" : "编辑品类";
        Width = 420; SizeToContent = SizeToContent.Height; MinHeight = 450; MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = host; ResizeMode = ResizeMode.NoResize;
        this.SetResourceReference(BackgroundProperty, Theme.Paper);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = Title, FontFamily = M.Serif, FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 16) }.Ref(Theme.Ink, TextBlock.ForegroundProperty));
        var id = new TextBox { Text = existing?.Id ?? "保存时自动生成", IsReadOnly = true, Opacity = .65 };
        Field(root, "ID", id);
        Field(root, "名称 *", _name);
        _group.ItemsSource = ItemsView.GroupChoices; _group.IsEditable = false;
        Field(root, "分组 *", _group);
        _route.ItemsSource = new[] { "oral", "topical" }; _route.SelectedIndex = 0;
        Field(root, "Route *", _route);
        Field(root, "默认剂量", _dose);
        Field(root, "单位 *", _unit);
        Field(root, "颜色（如 #8A9A5B）", _color);
        Field(root, "图标", _icon);
        Field(root, "备注", _notes);
        _error.SetResourceReference(TextBlock.ForegroundProperty, Theme.Danger); root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(ItemsView.Button("取消", (_, _) => DialogResult = false, Interactions.ButtonSecondary, "item-cancel"));
        var save = ItemsView.Button("保存", OnSave, Interactions.Button, "item-save"); save.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(save);
        root.Children.Add(actions);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var item = original;
        if (item is not null)
        {
            _name.Text = item.Name; _group.SelectedItem = item.Group; _route.SelectedItem = item.Route;
            _dose.Text = item.DefaultDose?.ToString(CultureInfo.CurrentCulture) ?? ""; _unit.Text = item.DoseUnit;
            _color.Text = item.Color ?? ""; _icon.Text = item.Icon ?? ""; _notes.Text = item.Notes ?? "";
        }
        else { _group.SelectedItem = "其他"; }
    }

    private static void Field(Panel root, string label, Control field)
    {
        root.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 8, 0, 4) }.Ref(Theme.Ink2, TextBlock.ForegroundProperty));
        root.Children.Add(field);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = _name.Text.Trim(); var unit = _unit.Text.Trim();
        if (name.Length == 0 || unit.Length == 0) { _error.Text = "名称和单位不能为空。"; return; }
        double? dose = null;
        if (_dose.Text.Trim().Length > 0)
        {
            if (!double.TryParse(_dose.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) && !double.TryParse(_dose.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) { _error.Text = "默认剂量必须是数字。"; return; }
            if (!double.IsFinite(parsed) || parsed < 0) { _error.Text = "默认剂量必须是大于或等于 0 的有效数字。"; return; }
            dose = parsed;
        }
        var color = _color.Text.Trim();
        if (color.Length > 0) { try { _ = ColorConverter.ConvertFromString(color); } catch { _error.Text = "颜色格式无效，请使用 #RRGGBB 或 #AARRGGBB。"; return; } }
        Result = _existing is null ? new Item { Id = ItemStore.GenerateId(name, _all), ActiveWindows = new List<ActiveWindow>() } : ItemsView.Clone(_existing);
        Result.Name = name; Result.Group = _group.SelectedItem as string ?? ""; Result.Route = _route.SelectedItem as string ?? "";
        Result.DefaultDose = dose; Result.DoseUnit = unit; Result.Color = Null(_color.Text); Result.Icon = Null(_icon.Text); Result.Notes = Null(_notes.Text);
        var next = _all.Where(i => !ReferenceEquals(i, _existing)).Concat(new[] { Result }).ToList();
        var errors = ItemStore.Validate(next);
        if (errors.Count > 0) { _error.Text = string.Join("\n", errors); return; }
        DialogResult = true;
    }
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>以列表增删别名，保存时由 ItemStore 检查全局冲突。</summary>
public sealed class AliasWindow : Window
{
    private readonly Item _item;
    private readonly ListBox _list = new() { Height = 190, FontSize = 13 };
    private readonly TextBox _input = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
    public List<string>? Aliases { get; private set; }

    public AliasWindow(MainWindow host, Item item)
    {
        _item = item;
        Title = $"别名管理 · {item.Name}"; Width = 370; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = host;
        this.SetResourceReference(BackgroundProperty, Theme.Paper);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = $"{item.Name} 的别名", FontFamily = M.Serif, FontSize = 21, FontWeight = FontWeights.Bold }.Ref(Theme.Ink, TextBlock.ForegroundProperty));
        root.Children.Add(new TextBlock { Text = "别名用于识别记录中的名称；不同品类的别名不能重复。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 12) }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        foreach (var alias in item.Aliases) _list.Items.Add(alias);
        root.Children.Add(_list);
        var addRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.Children.Add(_input);
        var add = ItemsView.Button("添加", OnAdd, Interactions.ButtonSecondary, "alias-add"); Grid.SetColumn(add, 1); add.Margin = new Thickness(8, 0, 0, 0); addRow.Children.Add(add);
        root.Children.Add(addRow);
        var remove = ItemsView.Button("删除选中别名", OnRemove, Interactions.ButtonDanger, "alias-remove"); remove.Margin = new Thickness(0, 8, 0, 0); root.Children.Add(remove);
        _error.SetResourceReference(TextBlock.ForegroundProperty, Theme.Danger); root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(ItemsView.Button("取消", (_, _) => DialogResult = false, Interactions.ButtonSecondary, "alias-cancel"));
        var save = ItemsView.Button("保存", (_, _) => { Aliases = _list.Items.Cast<string>().ToList(); DialogResult = true; }, Interactions.Button, "alias-save"); save.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(save); root.Children.Add(actions);
        Content = root;
    }

    private static readonly GridLength Star = new(1, GridUnitType.Star);

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var alias = _input.Text.Trim(); _error.Text = "";
        if (alias.Length == 0) { _error.Text = "别名不能为空。"; return; }
        if (_list.Items.Cast<string>().Contains(alias, StringComparer.Ordinal)) { _error.Text = $"此品类已存在别名“{alias}”。"; return; }
        var conflict = ItemStore.Load().FirstOrDefault(i => i.Id != _item.Id && i.Aliases.Contains(alias, StringComparer.Ordinal));
        if (conflict is not null) { _error.Text = $"别名“{alias}”已被品类“{conflict.Name}”使用。"; return; }
        _list.Items.Add(alias); _input.Clear(); _input.Focus();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is string alias) _list.Items.Remove(alias);
        else _error.Text = "请先选中要删除的别名。";
    }
}

/// <summary>固定高度占位，比设 Margin 直观（与 DataView 的 HeightGrid 同款）。</summary>
internal sealed class ItemsHeightGrid : FrameworkElement
{
    public ItemsHeightGrid(double h)
    {
        Height = h;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}
