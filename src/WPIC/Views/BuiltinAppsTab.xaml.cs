using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WPIC.Services;
using WPIC.ViewModels;

namespace WPIC.Views;

public partial class BuiltinAppsTab : UserControl
{
    private List<BuiltinAppVm> _items = new();
    private bool _loaded;
    private bool _busy;

    public BuiltinAppsTab()
    {
        InitializeComponent();
        L10n.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in _items) item.RefreshLanguage();
            ApplyView();
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
        SetBusy(true);
        try
        {
            var apps = await Task.Run(() => BuiltinAppService.ListAsync());
            var installedFamilies = new HashSet<string>(apps.Select(a => a.PackageFamilyName), StringComparer.OrdinalIgnoreCase);
            var removed = BuiltinAppService.LoadRemoved()
                .Where(r => !installedFamilies.Contains(r.PackageFamilyName))
                .ToList();
            _items = apps.Select(a => new BuiltinAppVm(a))
                .Concat(removed.Select(r => new BuiltinAppVm(r)))
                .ToList();
            ApplyView();
            LogService.Log(L10n.T("bloat.log.loaded", apps.Count, removed.Count));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("bloat.log.loadError", ex.Message));
            ThemedMessageBox.Show(L10n.T("bloat.err.load", ex.Message), L10n.T("bloat.title"),
                MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyView()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BuiltinAppVm.Group)));
        ItemsApps.ItemsSource = view;
    }

    private async void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _items.Where(i => i.IsSelected && i.IsInstalled && i.IsSelectable).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("bloat.err.noneRemove"), L10n.T("bloat.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }

        var confirm = ThemedMessageBox.Show(L10n.T("bloat.confirm.remove", selected.Count),
            L10n.T("bloat.title.remove"), MessageBoxButton.YesNo, MessageBoxImage.Warning, Window.GetWindow(this));
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogService.Log);
            foreach (var item in selected)
            {
                try
                {
                    await Task.Run(() => BuiltinAppService.RemoveAsync(item.App!, progress));
                }
                catch (Exception ex)
                {
                    LogService.Log(L10n.T("bloat.log.removeError", item.Name, ex.Message));
                }
            }
        }
        finally
        {
            await LoadAsync();
        }
    }

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _items.Where(i => i.IsSelected && !i.IsInstalled).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("bloat.err.noneRestore"), L10n.T("bloat.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }

        var confirm = ThemedMessageBox.Show(L10n.T("bloat.confirm.restore", selected.Count),
            L10n.T("bloat.title.restore"), MessageBoxButton.YesNo, MessageBoxImage.Question, Window.GetWindow(this));
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogService.Log);
            foreach (var item in selected)
            {
                try
                {
                    await Task.Run(() => BuiltinAppService.RestoreAsync(item.Record!, progress));
                }
                catch (Exception ex)
                {
                    LogService.Log(L10n.T("bloat.log.restoreError", item.Name, ex.Message));
                }
            }
        }
        finally
        {
            await LoadAsync();
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await LoadAsync();
    }

    private async void BtnRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnRemove.IsEnabled = BtnRestore.IsEnabled = BtnRefresh.IsEnabled = BtnRestorePoint.IsEnabled = !busy;
        ItemsApps.IsEnabled = !busy;
    }
}