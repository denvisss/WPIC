using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using WPIC.Models;
using WPIC.Services;
using WPIC.ViewModels;

namespace WPIC.Views;

public partial class TweaksTab : UserControl
{
    private static readonly string[] CategoryOrder = { "explorer", "start", "privacy", "system" };
    private List<TweakVm> _items = new();
    private string? _filter;
    private bool _loaded;
    private bool _busy;
    private readonly TweakService _service = new(LogService.DataDirectory);

    public TweaksTab()
    {
        InitializeComponent();
        L10n.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            foreach (var item in _items) item.RefreshLanguage();
            BuildChips();
            ApplyView();
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
            BuildChips();
            ApplyView();
            await RefreshStatusesAsync();
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("tweaks.log.loadError", ex.Message));
        }
    }

    private void BuildChips()
    {
        PanelCategories.Children.Clear();
        AddChip($"{L10n.T("tweaks.cat.all")} ({_items.Count})", null);
        foreach (var category in CategoryOrder)
        {
            var count = _items.Count(i => i.Category == category);
            if (count == 0) continue;
            AddChip($"{L10n.T("tweak.cat." + category)} ({count})", category);
        }
    }

    private void AddChip(string text, string? category)
    {
        var chip = new Button
        {
            Content = text,
            Tag = category,
            Margin = new Thickness(0, 0, 8, 8)
        };
        if (category == _filter) chip.Style = (Style)FindResource("PrimaryButton");
        chip.Click += Chip_Click;
        PanelCategories.Children.Add(chip);
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        _filter = (string?)((Button)sender).Tag;
        BuildChips();
        ApplyView();
    }

    private void ApplyView()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        view.GroupDescriptions.Clear();
        if (_filter == null)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakVm.CategoryName)));
        view.Filter = _filter == null ? null : new Predicate<object>(o => ((TweakVm)o).Category == _filter);
        ItemsTweaks.ItemsSource = view;
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
        BtnRefresh.IsEnabled = BtnRestorePoint.IsEnabled = ItemsTweaks.IsEnabled = PanelCategories.IsEnabled = !busy;
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