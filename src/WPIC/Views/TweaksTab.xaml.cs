using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WPIC.Models;
using WPIC.Services;
using WPIC.ViewModels;

namespace WPIC.Views;

public partial class TweaksTab : UserControl
{
    private static readonly string[] CategoryOrder = { "explorer", "start", "privacy", "gaming", "system" };

    private static readonly Dictionary<string, (string Geometry, string Gradient)> CategoryIcons = new()
    {
        ["explorer"] = ("M3 7 A2 2 0 0 1 5 5 H9 L11 7 H19 A2 2 0 0 1 21 9 V17 A2 2 0 0 1 19 19 H5 A2 2 0 0 1 3 17 Z", "Grad.Blue"),
        ["start"] = ("M4 4 H20 V20 H4 Z M4 15 H20 M7 17.5 H11", "Grad.Violet"),
        ["privacy"] = ("M12 3 L20 6 V11 C20 16 16.5 19.5 12 21 C7.5 19.5 4 16 4 11 V6 Z M8.5 11.5 L11 14 L15.5 9.5", "Grad.Green"),
        ["gaming"] = ("M7 8 H17 A5 5 0 0 1 22 13 L21.2 16.8 A2.4 2.4 0 0 1 17 17.6 L15.5 16 H8.5 L7 17.6 A2.4 2.4 0 0 1 2.8 16.8 L2 13 A5 5 0 0 1 7 8 Z M8 11 V15 M6 13 H10 M15.5 11.5 H15.51 M18 13.5 H18.01", "Grad.Pink"),
        ["system"] = ("M4 7 H20 M4 17 H20 M9 4 V10 M15 14 V20", "Grad.Orange")
    };

    private List<TweakVm> _items = new();
    private readonly Dictionary<string, CheckBox> _selectAllBoxes = new();
    private readonly Dictionary<string, bool> _expanded = new();
    private bool _updatingSelection;
    private bool _loaded;
    private bool _busy;
    private readonly TweakService _service = new(LogService.DataDirectory);

    public TweaksTab()
    {
        InitializeComponent();
        L10n.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            foreach (var item in _items) item.RefreshLanguage();
            BuildGroups();
            if (!_busy) await RefreshStatusesAsync();
        });
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var catalog = await ConfigService.LoadTweaksAsync();
            _items = catalog.Tweaks.Select(t => new TweakVm(t)).ToList();
            foreach (var item in _items) item.PropertyChanged += Item_PropertyChanged;
            BuildGroups();
            await RefreshStatusesAsync();
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("tweaks.log.loadError", ex.Message));
        }
    }

    private void BuildGroups()
    {
        PanelGroups.Children.Clear();
        _selectAllBoxes.Clear();
        var template = (DataTemplate)Resources["TweakRowTemplate"];
        foreach (var category in CategoryOrder)
        {
            var group = _items.Where(i => i.Category == category).ToList();
            if (group.Count == 0) continue;

            var (geometry, gradient) = CategoryIcons[category];
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new System.Windows.Shapes.Path
            {
                Width = 18, Height = 18, Stretch = Stretch.Uniform,
                Data = Geometry.Parse(geometry),
                StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stroke = (Brush)FindResource(gradient), VerticalAlignment = VerticalAlignment.Center
            });
            title.Children.Add(new TextBlock
            {
                Text = L10n.T("tweak.cat." + category), FontWeight = FontWeights.SemiBold, FontSize = 14,
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            title.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = CreateBadgeText(group.Count.ToString())
            });
            var selectAll = new CheckBox
            {
                Content = L10n.T("tweaks.selectAll"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0)
            };
            selectAll.Click += (_, args) =>
            {
                args.Handled = true;
                var supported = _items.Where(i => i.Category == category && i.IsSupported).ToList();
                SetCategorySelection(category, supported.Any(i => !i.IsSelected));
            };
            Grid.SetColumn(title, 0);
            Grid.SetColumn(selectAll, 1);
            header.Children.Add(title);
            header.Children.Add(selectAll);
            _selectAllBoxes[category] = selectAll;

            var expander = new Expander
            {
                Header = header,
                Content = new ItemsControl { ItemsSource = group, ItemTemplate = template },
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            expander.Expanded += (_, _) => _expanded[category] = true;
            expander.Collapsed += (_, _) => _expanded[category] = false;
            expander.IsExpanded = !_expanded.TryGetValue(category, out var open) || open;
            PanelGroups.Children.Add(expander);
        }
        UpdateSelectionUi();
    }

    private TextBlock CreateBadgeText(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return block;
    }

    private void SetCategorySelection(string category, bool selected)
    {
        _updatingSelection = true;
        foreach (var item in _items.Where(i => i.Category == category && i.IsSupported))
            item.IsSelected = selected;
        _updatingSelection = false;
        UpdateSelectionUi();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TweakVm.IsSelected) && !_updatingSelection) UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        TxtSelected.Text = L10n.T("tweaks.selected", _items.Count(i => i.IsSelected));
        foreach (var (category, box) in _selectAllBoxes)
        {
            var supported = _items.Where(i => i.Category == category && i.IsSupported).ToList();
            box.IsChecked = supported.Count > 0 && supported.All(i => i.IsSelected);
        }
    }

    private async Task RefreshStatusesAsync()
    {
        try
        {
            foreach (var item in _items)
            {
                var tweak = item.T;
                item.Status = await Task.Run(() => _service.GetStatus(tweak));
            }
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("tweaks.log.statusError", ex.Message));
        }
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _items.Where(i => i.IsSelected && i.IsSupported).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("tweaks.err.none"), L10n.T("tweaks.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }

        var confirm = ThemedMessageBox.Show(L10n.T("tweaks.confirm.apply", selected.Count),
            L10n.T("tweaks.title.system"), MessageBoxButton.YesNo, MessageBoxImage.Warning, Window.GetWindow(this));
        if (confirm != MessageBoxResult.Yes) return;

        var backupDir = Path.Combine(_service.BackupDirectory,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N"));

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogService.Log);
            foreach (var item in selected)
            {
                LogService.Log(L10n.T("tweaks.log.header", item.Name));
                await Task.Run(() => _service.ApplyAsync(item.T, backupDir, progress));
            }
            LogService.Log(L10n.T("tweaks.log.applyDone", backupDir));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("tweaks.log.applyError", ex.Message));
            ThemedMessageBox.Show(L10n.T("tweaks.err.apply", ex.Message), L10n.T("apps.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            await RefreshStatusesAsync();
            SetBusy(false);
        }
    }

    private async void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _items.Where(i => i.IsSelected && i.IsSupported).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("tweaks.err.none"), L10n.T("tweaks.title.undo"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }

        var confirm = ThemedMessageBox.Show(L10n.T("tweaks.confirm.undo", selected.Count),
            L10n.T("tweaks.title.undo"), MessageBoxButton.YesNo, MessageBoxImage.Question, Window.GetWindow(this));
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogService.Log);
            foreach (var item in selected)
            {
                LogService.Log(L10n.T("tweaks.log.undoHeader", item.Name));
                await Task.Run(() => _service.UndoAsync(item.T, progress));
            }
            LogService.Log(L10n.T("tweaks.log.undoDone"));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("tweaks.log.undoError", ex.Message));
            ThemedMessageBox.Show(L10n.T("tweaks.err.undo", ex.Message), L10n.T("apps.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            await RefreshStatusesAsync();
            SetBusy(false);
        }
    }

    private async void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var backupRoot = _service.BackupDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = L10n.T("tweaks.restoreBackup.pick"),
            InitialDirectory = Directory.Exists(backupRoot) ? backupRoot : LogService.DataDirectory
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (!File.Exists(Path.Combine(dialog.FolderName, "registry-backup.json")))
        {
            ThemedMessageBox.Show(L10n.T("tweaks.restoreBackup.notFound"), L10n.T("tweaks.title.restore"),
                MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
            return;
        }

        var confirm = ThemedMessageBox.Show(L10n.T("tweaks.confirm.restoreBackup", dialog.FolderName),
            L10n.T("tweaks.title.restore"), MessageBoxButton.YesNo, MessageBoxImage.Warning, Window.GetWindow(this));
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogService.Log);
            var count = await Task.Run(() => _service.RestoreBackup(dialog.FolderName, progress));
            LogService.Log(L10n.T("tweaks.log.restoreDone", count));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("tweaks.log.restoreError", ex.Message));
            ThemedMessageBox.Show(L10n.T("tweaks.err.restore", ex.Message), L10n.T("apps.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            await RefreshStatusesAsync();
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnApply.IsEnabled = BtnUndo.IsEnabled = BtnRestoreBackup.IsEnabled = !busy;
        BtnRefresh.IsEnabled = BtnRestorePoint.IsEnabled = PanelGroups.IsEnabled = !busy;
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try { await RefreshStatusesAsync(); }
        finally { SetBusy(false); }
    }

    private async void BtnRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        var confirm = ThemedMessageBox.Show(L10n.T("tweaks.confirm.rp"), L10n.T("tweaks.title.rp"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, Window.GetWindow(this));
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogService.Log);
            await RestorePointService.CreateAsync(progress);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
