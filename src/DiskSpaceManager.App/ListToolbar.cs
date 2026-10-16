using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using DiskSpaceManager.Core;

namespace DiskSpaceManager.App;

/// <summary>
/// Toolbar shared by every list tab: filter box, delete buttons, CSV export and a live
/// "N selected · X GB" summary. Tab-specific controls go in <see cref="Extra"/>.
/// </summary>
[ContentProperty(nameof(Extra))]
public sealed class ListToolbar : DockPanel
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(DataGrid), typeof(ListToolbar), new PropertyMetadata(null, (d, e) => ((ListToolbar)d).Attach()));

    private readonly TextBox _filter = new() { Width = 220, ToolTip = "Filter by path, name or type" };
    private readonly TextBlock _placeholder = new()
    {
        Text = "Filter…", Foreground = Brushes.Gray, IsHitTestVisible = false, Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _summary = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _extra = new() { VerticalAlignment = VerticalAlignment.Center };

    public ListToolbar()
    {
        Margin = new Thickness(0, 0, 0, 8);
        LastChildFill = true;

        var filterHost = new Grid { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        filterHost.Children.Add(_filter);
        filterHost.Children.Add(_placeholder);
        _filter.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        };
        SetDock(filterHost, Dock.Left);
        Children.Add(filterHost);

        SetDock(_extra, Dock.Left);
        Children.Add(_extra);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var recycle = new Button { Content = "Move to Recycle Bin", Style = (Style)Application.Current.Resources["Primary"], ToolTip = "Del" };
        recycle.Click += (_, _) => Host?.DeleteSelected(Target, permanent: false);
        var nuke = new Button { Content = "Delete permanently", Style = (Style)Application.Current.Resources["Danger"], ToolTip = "Shift+Del — frees space immediately, cannot be undone" };
        nuke.Click += (_, _) => Host?.DeleteSelected(Target, permanent: true);
        var export = new Button { Content = "Export CSV" };
        export.Click += (_, _) => Host?.ExportCsv(Target);
        right.Children.Add(_summary);
        right.Children.Add(new Border { Width = 10 });
        right.Children.Add(export);
        right.Children.Add(recycle);
        right.Children.Add(nuke);
        SetDock(right, Dock.Right);
        Children.Add(right);
        Children.Add(new Border()); // filler
    }

    public DataGrid? Target
    {
        get => (DataGrid?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public object? Extra
    {
        get => _extra.Content;
        set => _extra.Content = value;
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private void Attach()
    {
        if (Target is null) return;
        Target.SelectionChanged += (_, _) => UpdateSummary();
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
            .AddValueChanged(Target, (_, _) => { ApplyFilter(); UpdateSummary(); });
    }

    private void ApplyFilter()
    {
        if (Target?.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(Target.ItemsSource);
        var text = _filter.Text.Trim();
        if (text.Length == 0) view.Filter = null;
        else view.Filter = o => o switch
        {
            ItemRow r => r.Matches(text),
            ExtRow x => x.Extension.Contains(text, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
        UpdateSummary();
    }

    public void SetFilter(string text) => _filter.Text = text;

    private void UpdateSummary()
    {
        if (Target is null) return;
        var sel = Target.SelectedItems.OfType<ItemRow>().ToList();
        int shown = Target.Items.Count;
        if (sel.Count > 0)
        {
            // Avoid double counting a folder and something inside it.
            long total = sel.Count > 500 ? sel.Sum(r => r.Size) : sel.Where(r => !sel.Any(o => o != r && o.IsFolder && TreeOps.IsUnder(r.Path, o.Path) && r.Path.Length > o.Path.Length))
                .Sum(r => r.Size);
            _summary.Text = $"{sel.Count:N0} selected · {Fmt.Bytes(total)}";
        }
        else _summary.Text = $"{shown:N0} items";
    }
}
