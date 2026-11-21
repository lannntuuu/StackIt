using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StackIt.App.UI;
using StackIt.Core;

namespace StackIt.App.Views;

/// <summary>品类清单、增删改与归档。每次写操作都经 ItemStore 立即落盘。</summary>
public sealed class ItemsView : Grid
{
    private static readonly string[] Groups = { "B族", "维C", "脂溶性", "矿物质", "脂肪酸", "外用", "其他" };
    private readonly MainWindow _host;
    private readonly List<Item> _items;
    private readonly TextBox _filter = new() { Width = 160, FontSize = 12, Margin = new Thickness(10, 0, 10, 0) };
    private readonly DataGrid _table = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 40, FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _edit;
    private readonly Button _archive;
    private readonly Button _delete;
    private readonly Button _aliases;
    private readonly TextBlock _status = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public ItemsView(MainWindow host)
    {
        _host = host;
        _items = ItemStore.Load();
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(20, 18, 20, 15) };
        top.Children.Add(new TextBlock { Text = "STACKIT · ITEM LIBRARY", FontFamily = M.Mono, FontSize = 10 }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        top.Children.Add(new TextBlock { Text = "品类管理", FontFamily = M.Serif, FontSize = 27, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) }.Ref(Theme.Ink, TextBlock.ForegroundProperty));
        top.Children.Add(new TextBlock { Text = "维护名称、别名与默认剂量 · 改动立即保存", FontSize = 11.5 }.Ref(Theme.Ink3, TextBlock.ForegroundProperty));
        var tools = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(new TextBlock { Text = "筛选", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 }.Ref(Theme.Ink2, TextBlock.ForegroundProperty));
        Grid.SetColumn(_filter, 1);
        _filter.TextChanged += (_, _) => _table.Items.Filter = Filter;
        tools.Children.Add(_filter);
        var add = Button("＋ 新增品类", OnAdd, Interactions.Button, "item-add");
        Grid.SetColumn(add, 2);
        tools.Children.Add(add);
        top.Children.Add(tools);
        Children.Add(top);

        _table.Columns.Add(TextColumn("名称", "Name", new DataGridLength(1.5, DataGridLengthUnitType.Star)));
        _table.Columns.Add(TextColumn("分组", "Group", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _table.Columns.Add(TextColumn("别名数", "Aliases.Count", new DataGridLength(65, DataGridLengthUnitType.Pixel)));
        _table.Columns.Add(new DataGridTemplateColumn { Header = "状态", Width = new DataGridLength(65, DataGridLengthUnitType.Pixel), CellTemplate = StatusTemplate() });
        _table.SelectionChanged += (_, _) => UpdateButtons();
        _table.MouseDoubleClick += (_, _) => { if (_table.SelectedItem is Item item) OpenEdit(item); };
        _table.SetResourceReference(BackgroundProperty, Theme.Surface);
        _table.SetResourceReference(Control.ForegroundProperty, Theme.Ink);
        _table.SetResourceReference(Control.BorderBrushProperty, Theme.Line);
        _table.BorderThickness = new Thickness(1);
        var list = new Border { Margin = new Thickness(20, 0, 20, 0), Child = _table };
        Grid.SetRow(list, 1);
        Children.Add(list);

        var bottom = new StackPanel { Margin = new Thickness(20, 14, 20, 20) };
        var actions = new WrapPanel();
        _edit = Button("编辑", OnEdit, Interactions.ButtonSecondary, "item-edit");
        _archive = Button("归档", OnArchive, Interactions.ButtonSecondary, "item-archive");
        _delete = Button("删除", OnDelete, Interactions.ButtonDanger, "item-delete");
        _aliases = Button("别名管理", OnAliases, Interactions.ButtonSecondary, "item-aliases");
        foreach (var b in new[] { _edit, _archive, _aliases, _delete }) { b.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(b); }
        bottom.Children.Add(actions);
        _status.Text = "选择一行以编辑或管理别名；有历史记录的品类不能删除。";
        _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 2);
        Children.Add(bottom);

        _table.ItemsSource = _items;
        _table.Items.Filter = Filter;
        UpdateButtons();
    }

    private bool Filter(object value)
    {
        var keyword = _filter.Text.Trim();
        return value is Item item && (keyword.Length == 0 || item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) || item.Group.Contains(keyword, StringComparison.OrdinalIgnoreCase) || item.Aliases.Any(a => a.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private Item? Selected => _table.SelectedItem as Item;
    private bool HasHistory(Item item) => _host.Db.Events.Any(e => string.Equals(e.ItemId, item.Id, StringComparison.Ordinal));

    private void UpdateButtons()
    {
        var item = Selected;
        _edit.IsEnabled = _archive.IsEnabled = _aliases.IsEnabled = item is not null;
        _delete.IsEnabled = item is not null && !HasHistory(item);
        _archive.Content = item?.Archived == true ? "取消归档" : "归档";
        if (item is not null && HasHistory(item)) _delete.ToolTip = "此品类已有历史记录，只能归档";
        else _delete.ToolTip = null;
    }

    private void OnAdd(object s, RoutedEventArgs e) => OpenEdit(null);
    private void OnEdit(object s, RoutedEventArgs e) { if (Selected is Item item) OpenEdit(item); }

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
    }

    private void OnArchive(object s, RoutedEventArgs e)
    {
        if (Selected is not Item item) return;
        var archived = !item.Archived;
        if (archived && !Confirm($"归档“{item.Name}”？\n\n历史记录会保留，可随时取消归档。")) return;
        Save(() => item.Archived = archived);
    }

    private void OnDelete(object s, RoutedEventArgs e)
    {
        if (Selected is not Item item) return;
        if (HasHistory(item)) { Say("此品类已有历史记录，只能归档，不能删除。", true); return; }
        if (!Confirm($"永久删除“{item.Name}”及其别名？\n\n此操作不可撤销。")) return;
        Save(() =>
        {
            _items.Remove(item);
            _host.Db.Items?.RemoveAll(i => i.Id == item.Id);
        });
    }

    private void OnAliases(object s, RoutedEventArgs e)
    {
        if (Selected is not Item item) return;
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
            _table.Items.Refresh();
            _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
            Say("已保存到 items.json", false);
        }
        catch (Exception ex)
        {
            _items.Clear(); _items.AddRange(backup);
            _host.Db.Items = dbBackup;
            _table.ItemsSource = null;
            _table.ItemsSource = _items;
            _table.Items.Refresh();
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

    private static DataGridTextColumn TextColumn(string header, string binding, DataGridLength width) => new() { Header = header, Binding = new Binding(binding), Width = width };

    private static DataTemplate StatusTemplate()
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Archived") { Converter = new ArchivedText() });
        template.VisualTree = text;
        return template;
    }

    private sealed class ArchivedText : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "已归档" : "使用中";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    internal static Button Button(string text, RoutedEventHandler click, string style, string tag)
    {
        var button = new Button { Content = text, Cursor = Cursors.Hand, Tag = tag };
        Interactions.ApplyStyle(button, style);
        button.Click += click;
        UI.Focus.SetRing(button, true);
        return button;
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
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
