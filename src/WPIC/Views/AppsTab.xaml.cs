using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WPIC.Models;
using WPIC.Services;
using WPIC.ViewModels;

namespace WPIC.Views;

public partial class AppsTab : UserControl
{
    private List<AppItemVm> _items = new();
    private AppCatalog? _catalog;
    private Dictionary<AppPackage, AppItemVm> _vmByApp = new();
    private List<UpgradeItemVm> _upgrades = new();
    private bool _loaded;
    private bool _updatesChecked;
    private bool _updatesChecking;
    private bool _busy;
    private bool _bootstrapping;
    private bool _wingetInstalled;
    private bool _wingetChecking;
    private bool _wingetInstalling;
    private CancellationTokenSource? _searchCts;

    private readonly Progress<string> _progress;

    public AppsTab()
    {
        InitializeComponent();
        _progress = new Progress<string>(LogService.Log);
        TxtSearch.TextChanged += (_, _) =>
            TxtSearchHint.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        L10n.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_catalog == null) return;
            ItemsCategories.ItemsSource = BuildGroups();
            foreach (var item in _items)
            {
                item.Status = item.Installed ? L10n.T("apps.state.installed") : L10n.T("apps.state.notInstalled");
                item.RefreshLocalization();
            }
            UpdateCatalogSelectionCount();
            if (_updatesChecked)
                TxtUpdatesHeader.Text = _upgrades.Count > 0
                    ? $"{L10n.T("apps.tab.updates")} ({_upgrades.Count})"
                    : L10n.T("apps.tab.updates");
        });
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadCatalogAsync();
            await RefreshWingetStateAsync();
            if (_wingetInstalled) _ = RefreshCatalogVersionsAsync();
            await CheckUpdatesAsync(auto: true);

            if (!string.IsNullOrEmpty(AppArgs.SearchQuery))
            {
                SubTabs.SelectedItem = TabSearch;
                TxtSearch.Text = AppArgs.SearchQuery;
                DoSearch();
            }
        };
    }

    // ---------- каталог ----------

    private async Task LoadCatalogAsync()
    {
        try
        {
            _catalog = await ConfigService.LoadAppsAsync();
            foreach (var category in _catalog.Categories)
            foreach (var app in category.Apps)
                _vmByApp[app] = new AppItemVm(app);

            _items = _vmByApp.Values.ToList();
            foreach (var vm in _items)
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(AppItemVm.IsSelected)) UpdateCatalogSelectionCount();
                };
            UpdateCatalogSelectionCount();

            ItemsCategories.ItemsSource = BuildGroups();

            var cache = CatalogVersionService.LoadCache();
            foreach (var item in _items)
                if (item.Pkg.WingetId.Length > 0 && cache.TryGetValue(item.Pkg.WingetId, out var version))
                    item.VersionRaw = version;

            await RefreshStatusesAsync();
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("apps.log.loadError", ex.Message));
        }
    }

    private async Task RefreshStatusesAsync()
    {
        try
        {
            var installedIds = await WingetService.GetInstalledIdsAsync();
            var installedNames = await Task.Run(WingetService.GetInstalledDisplayNames);
            foreach (var item in _items)
            {
                item.Installed = WingetService.IsInstalled(item.Pkg, installedIds, installedNames);
                item.Status = item.Installed ? L10n.T("apps.state.installed") : L10n.T("apps.state.notInstalled");
            }
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("apps.log.statusError", ex.Message));
        }
    }

    private async Task RefreshCatalogVersionsAsync()
    {
        try
        {
            var ids = _items.Select(i => i.Pkg.WingetId).Where(id => id.Length > 0).ToList();
            await CatalogVersionService.RefreshAsync(ids, (id, version) =>
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var item in _items.Where(i =>
                        string.Equals(i.Pkg.WingetId, id, StringComparison.OrdinalIgnoreCase)))
                        item.VersionRaw = version;
                }));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("apps.log.versionsError", ex.Message));
        }
    }

    private List<CategoryGroup> BuildGroups() =>
        _catalog!.Categories
            .Where(c => c.Apps.Count > 0)
            .Select(c => new CategoryGroup(
                string.IsNullOrEmpty(c.Key) ? c.Name : L10n.T("apps.cat." + c.Key),
                c.Apps.Select(a => _vmByApp[a]).ToList()))
            .ToList();

    private void UpdateCatalogSelectionCount() =>
        TxtCatalogStatus.Text = L10n.T("apps.catalog.selected", _items.Count(i => i.IsSelected));

    private async void BtnInstallSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _items.Where(i => i.IsSelected && !i.Installed).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("apps.err.noneSelected"), L10n.T("apps.info.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }
        var installable = selected.Where(i =>
            i.Pkg.InstallTargets.Count > 0 || !string.IsNullOrWhiteSpace(i.Pkg.WindowsFeature)).ToList();
        if (installable.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("apps.err.noWinget"), L10n.T("apps.info.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }

        if (!await EnsureWingetAsync()) return;
        if (!Confirm(L10n.T("apps.confirm.install", installable.Count,
                     string.Join("\n", installable.Select(i => "• " + i.Pkg.Name))))) return;

        _busy = true;
        BtnInstallSelected.IsEnabled = false;
        try
        {
            var installedIds = await WingetService.GetInstalledIdsAsync();
            foreach (var item in installable)
            {
                item.Status = L10n.T("apps.state.installing");
                foreach (var id in item.Pkg.InstallTargets)
                {
                    if (installedIds.Contains(id))
                    {
                        LogService.Log(L10n.T("winget.log.skipInstalled", item.Pkg.Name, id));
                        continue;
                    }
                    await WingetService.InstallByIdAsync(item.Pkg.Name, id, _progress);
                }
                if (!string.IsNullOrWhiteSpace(item.Pkg.WindowsFeature))
                    await WingetService.EnableWindowsFeatureAsync(item.Pkg.WindowsFeature!, _progress);
            }
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("apps.log.installError", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnInstallSelected.IsEnabled = true;
            await RefreshStatusesAsync();
        }
    }

    // ---------- обновления ----------

    private async Task CheckUpdatesAsync(bool auto)
    {
        if (_updatesChecking) return;
        if (auto && _updatesChecked) return;
        _updatesChecking = true;
        BtnCheckUpdates.IsEnabled = false;
        TxtUpdatesStatus.Text = L10n.T("apps.updates.checking");
        try
        {
            if (auto && !await WingetService.IsAvailableAsync())
            {
                TxtUpdatesStatus.Text = L10n.T("winget.boot.available");
                return;
            }
            if (!auto && !await EnsureWingetAsync())
            {
                TxtUpdatesStatus.Text = L10n.T("winget.boot.available");
                return;
            }
            var entries = await Task.Run(() => WingetService.ListUpgradesAsync());
            _upgrades = entries.Select(e => new UpgradeItemVm(e)).ToList();
            ItemsUpdates.ItemsSource = _upgrades;
            TxtUpdatesHeader.Text = _upgrades.Count > 0
                ? $"{L10n.T("apps.tab.updates")} ({_upgrades.Count})"
                : L10n.T("apps.tab.updates");
            TxtUpdatesStatus.Text = _upgrades.Count == 0
                ? L10n.T("apps.updates.upToDate")
                : L10n.T("apps.updates.available", _upgrades.Count);
            _updatesChecked = true;
            LogService.Log(L10n.T("apps.log.updatesChecked", _upgrades.Count));
            if (_upgrades.Count > 0) _ = EnrichUpgradeIconsAsync(_upgrades);
        }
        catch (Exception ex)
        {
            TxtUpdatesStatus.Text = L10n.T("apps.updates.error");
            LogService.Log(L10n.T("apps.log.updatesError", ex.Message));
        }
        finally
        {
            _updatesChecking = false;
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private async Task EnrichUpgradeIconsAsync(List<UpgradeItemVm> vms)
    {
        var dispatcher = Application.Current.Dispatcher;
        using var gate = new SemaphoreSlim(6);

        var tasks = vms.Take(40).Select(async vm =>
        {
            await gate.WaitAsync();
            try
            {
                string? homepage = null;
                try { homepage = (await PackageMetaService.GetAsync(vm.Id))?.Homepage; } catch { }
                var bytes = await IconService.FetchIconBytesAsync(homepage, packageId: vm.Id);
                if (bytes == null) return;
                await dispatcher.InvokeAsync(() => vm.Icon = IconService.ToImageSource(bytes));
            }
            catch
            {
                // иконка не критична — останется цветная плитка
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdatesAsync(auto: false);

    private async void BtnUpgradeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _upgrades.Where(u => u.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show(L10n.T("apps.err.noneSelectedUpd"), L10n.T("apps.upgrade.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }
        if (!Confirm(L10n.T("apps.confirm.upgrade", selected.Count,
                     string.Join("\n", selected.Select(i => $"• {i.Name}  {i.VersionLabel} → {i.AvailableLabel}"))))) return;

        _busy = true;
        BtnUpgradeSelected.IsEnabled = false;
        try
        {
            foreach (var item in selected)
                await WingetService.UpgradeAsync(item.Source, _progress);
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("apps.log.upgradeError", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnUpgradeSelected.IsEnabled = true;
            await RefreshStatusesAsync();
            await CheckUpdatesAsync(auto: false);
        }
    }

    private void BtnUpSelAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var u in _upgrades) u.IsSelected = true;
    }

    private void BtnUpSelNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var u in _upgrades) u.IsSelected = false;
    }

    // ---------- поиск ----------

    private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoSearch();
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e) => DoSearch();

    private async void DoSearch()
    {
        var query = TxtSearch.Text.Trim();
        if (query.Length == 0) return;

        SubTabs.SelectedItem = TabSearch;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        TxtSearchStatus.Text = L10n.T("apps.searching");
        ItemsSearchResults.ItemsSource = null;
        try
        {
            if (!await EnsureWingetAsync())
            {
                TxtSearchStatus.Text = L10n.T("winget.boot.available");
                return;
            }
            ct.ThrowIfCancellationRequested();
            var results = await Task.Run(() => WingetService.SearchAsync(query, ct), ct);
            ct.ThrowIfCancellationRequested();

            var vms = results.Select(r => new SearchResultVm(r)).ToList();
            ItemsSearchResults.ItemsSource = vms;
            TxtSearchStatus.Text = vms.Count == 0
                ? L10n.T("apps.search.none")
                : L10n.T("apps.search.found.loading", vms.Count);

            if (vms.Count > 0) _ = EnrichResultsAsync(vms, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TxtSearchStatus.Text = L10n.T("apps.search.error");
            LogService.Log(L10n.T("apps.log.searchError", ex.Message));
        }
    }

    private async Task EnrichResultsAsync(List<SearchResultVm> vms, CancellationToken ct)
    {
        var dispatcher = Application.Current.Dispatcher;
        using var gate = new SemaphoreSlim(6);
        var uiLang = L10n.CurrentLanguage;

        var tasks = vms.Take(25).Select(async vm =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    // 1) Локализованный манифест из winget-pkgs
                    ManifestInfo? info = null;
                    try { info = await PackageMetaService.GetAsync(vm.Id, ct); } catch { }

                    var description = info?.Description;
                    var homepage = info?.Homepage;
                    var descLang = LocaleToLang(info?.Locale);

                    // 2) Fallback: `winget show` (описание на языке ОС)
                    if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(homepage))
                    {
                        PackageDetails? fallback = null;
                        try { fallback = await WingetService.ShowDetailsAsync(vm.Id, ct); } catch { }
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = fallback?.Description;
                            descLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                        }
                        homepage ??= fallback?.Homepage;
                    }

                    // 3) Описание на языке интерфейса: при несовпадении — перевод
                    if (!string.IsNullOrWhiteSpace(description) && descLang != null && descLang != uiLang)
                    {
                        try { description = await TranslationService.TranslateAsync(description!, descLang, uiLang, ct); }
                        catch { }
                    }

                    byte[]? iconBytes = null;
                    try { iconBytes = await IconService.FetchIconBytesAsync(homepage, ct, vm.Id); } catch { }

                    await dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        vm.Description = string.IsNullOrWhiteSpace(description)
                            ? L10n.T("apps.desc.unavailable")
                            : description!;
                        vm.Icon = IconService.ToImageSource(iconBytes);
                    });
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.WhenAll(tasks);

        if (!ct.IsCancellationRequested)
            _ = dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(ItemsSearchResults.ItemsSource, vms))
                    TxtSearchStatus.Text = L10n.T("apps.search.found", vms.Count);
            });
    }

    private static string? LocaleToLang(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var dash = locale.IndexOf('-');
        return (dash > 0 ? locale[..dash] : locale).ToLowerInvariant();
    }

    private async void BtnInstallSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not SearchResultVm vm) return;
        if (!vm.CanInstall || _busy) return;

        if (!await EnsureWingetAsync()) return;

        vm.CanInstall = false;
        vm.InstallText = L10n.T("apps.btn.installing");
        try
        {
            var code = await WingetService.InstallByIdAsync(vm.Name, vm.Id, _progress);
            if (code == 0)
            {
                vm.InstallText = L10n.T("apps.btn.installed");
            }
            else
            {
                vm.InstallText = L10n.T("apps.btn.retryCode", code);
                vm.CanInstall = true;
            }
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("apps.log.installError", ex.Message));
            vm.InstallText = L10n.T("apps.btn.retry");
            vm.CanInstall = true;
        }
        await RefreshStatusesAsync();
    }

    // ---------- winget ----------

    private async Task RefreshWingetStateAsync()
    {
        if (_wingetChecking) return;
        _wingetChecking = true;
        try
        {
            _wingetInstalled = await WingetService.IsAvailableAsync();
        }
        finally
        {
            _wingetChecking = false;
        }
        UpdateWingetUi();
    }

    private void UpdateWingetUi()
    {
        var missing = !_wingetInstalled;
        TxtWingetBadge.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        WingetBanner.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        WingetProgressPanel.Visibility = Visibility.Collapsed;
        BtnWingetInstall.Visibility = missing && !_wingetInstalling ? Visibility.Visible : Visibility.Collapsed;
        BtnWingetInstall.IsEnabled = !_wingetInstalling;
    }

    private async void BtnWingetInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_wingetInstalling) return;

        _wingetInstalling = true;
        BtnWingetInstall.IsEnabled = false;
        BtnWingetInstall.Visibility = Visibility.Collapsed;
        WingetProgressPanel.Visibility = Visibility.Visible;
        TxtWingetProgress.Text = L10n.T("winget.ui.installing");
        var progress = new Progress<string>(line =>
        {
            LogService.Log(line);
            TxtWingetProgress.Text = line.Length > 110 ? line[..110] + "…" : line;
        });
        try
        {
            if (await WingetBootstrapService.InstallAsync(progress))
            {
                _wingetInstalled = true;
                TxtWingetProgress.Text = L10n.T("winget.ui.done");
                LogService.Log(L10n.T("winget.ui.done"));
                await Task.Delay(2500);
            }
            else
            {
                TxtWingetProgress.Text = L10n.T("winget.ui.failed");
            }
        }
        catch (Exception ex)
        {
            TxtWingetProgress.Text = L10n.T("winget.ui.failed");
            LogService.Log(ex.Message);
        }
        finally
        {
            _wingetInstalling = false;
            UpdateWingetUi();
        }
    }

    // ---------- прочее ----------

    private async Task<bool> EnsureWingetAsync()
    {
        if (_bootstrapping) return false;
        _bootstrapping = true;
        try
        {
            if (await WingetService.IsAvailableAsync()) return true;
            if (ThemedMessageBox.Show(L10n.T("winget.boot.confirm"), L10n.T("winget.boot.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question, Window.GetWindow(this)) != MessageBoxResult.Yes)
                return false;
            if (await WingetBootstrapService.InstallAsync(_progress)) return true;
        }
        catch (Exception ex) { LogService.Log(ex.Message); }
        finally { _bootstrapping = false; }
        ThemedMessageBox.Show(L10n.T("apps.err.wingetMissing"), L10n.T("apps.err.title"),
            MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
        return false;
    }

    private bool Confirm(string message) =>
        ThemedMessageBox.Show(message, L10n.T("apps.confirm.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, Window.GetWindow(this)) == MessageBoxResult.Yes;
}