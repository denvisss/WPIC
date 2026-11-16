using System.Globalization;
using System.IO;
using System.Windows.Media;
using WPIC.Services;

namespace WPIC.ViewModels;

public class BuiltinAppVm : ObservableObject
{
    public BuiltinAppVm(BuiltinApp app)
    {
        App = app;
        Icon = LoadIcon(app.IconPath);
    }

    public BuiltinAppVm(RemovedAppRecord record) => Record = record;

    public BuiltinApp? App { get; }
    public RemovedAppRecord? Record { get; }
    public bool IsInstalled => App != null;
    public string PackageName => App?.Name ?? Record!.Name;
    public string Name => App?.DisplayName ?? BuiltinAppService.Prettify(Record!.Name);
    public string Risk => BuiltinAppService.RiskOf(PackageName);
    public bool IsSelectable => IsInstalled ? !App!.NonRemovable : true;
    public ImageSource? Icon { get; }

    public string DateText
    {
        get
        {
            var culture = CultureInfo.GetCultureInfo(L10n.CurrentLanguage == "ru" ? "ru-RU" : "en-US");
            if (IsInstalled)
                return App!.InstallDate is { } installed
                    ? L10n.T("bloat.date.installed", installed.ToString("d", culture))
                    : "";
            return L10n.T("bloat.date.removed", Record!.RemovedUtc.ToLocalTime().ToString("d", culture));
        }
    }

    public string Status => IsInstalled
        ? App!.NonRemovable ? L10n.T("bloat.status.nonRemovable") : L10n.T("bloat.status.installed")
        : L10n.T("bloat.status.removed");

    public string RiskLabel => L10n.T("tweaks.risk." + Risk);

    public string Group => IsInstalled ? L10n.T("bloat.group.installed") : L10n.T("bloat.group.removed");

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value && IsSelectable);
    }

    public void RefreshLanguage() => Raise(null);

    private static ImageSource? LoadIcon(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            return IconService.ToImageSource(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }
}