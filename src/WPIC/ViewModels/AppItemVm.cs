namespace WPIC.ViewModels;

using System.Windows;
using WPIC.Models;
using WPIC.Services;

public class AppItemVm : ObservableObject
{
    public AppItemVm(AppPackage pkg) => Pkg = pkg;

    public AppPackage Pkg { get; }
    public string Name => Pkg.Name;

    public string Description => L10n.CurrentLanguage == "en"
        ? (Pkg.DescriptionEn ?? Pkg.Description ?? "")
        : (Pkg.Description ?? Pkg.DescriptionEn ?? "");

    private string _versionRaw = "";
    public string VersionRaw
    {
        get => _versionRaw;
        set
        {
            if (Set(ref _versionRaw, value))
            {
                Raise(nameof(VersionLabel));
                Raise(nameof(VersionVisibility));
            }
        }
    }

    public string VersionLabel => _versionRaw.Length == 0 ? ""
        : string.Equals(_versionRaw, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? L10n.T("apps.version.store")
            : "v" + _versionRaw;

    public Visibility VersionVisibility => _versionRaw.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void RefreshLocalization()
    {
        Raise(nameof(Description));
        Raise(nameof(VersionLabel));
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set => Set(ref _installed, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed record CategoryGroup(string Name, IReadOnlyList<AppItemVm> Apps);