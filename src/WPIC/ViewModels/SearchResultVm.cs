namespace WPIC.ViewModels;

using System.Windows;
using System.Windows.Media;
using WPIC.Models;
using WPIC.Services;

/// <summary>Строка результата поиска в winget: плитка-заглушка/иконка, описание, кнопка установки.</summary>
public class SearchResultVm : ObservableObject
{
    public SearchResultVm(SearchResult result)
    {
        Source = result;
        TileLetter = AppTile.Letter(result.Name);
        TileBrush = AppTile.Brush(result.Id);
        VersionLabel = "v" + result.Version;
        SourceLabel = result.Source;
    }

    public SearchResult Source { get; }
    public string Name => Source.Name;
    public string Id => Source.Id;
    public string TileLetter { get; }
    public Brush TileBrush { get; }
    public string VersionLabel { get; }
    public string SourceLabel { get; }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value)) Raise(nameof(IconVisibility));
        }
    }

    public Visibility IconVisibility => _icon == null ? Visibility.Collapsed : Visibility.Visible;

    private string _description = L10n.T("apps.desc.loading");
    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    private string _installText = L10n.T("apps.btn.install");
    public string InstallText
    {
        get => _installText;
        set => Set(ref _installText, value);
    }

    private bool _canInstall = true;
    public bool CanInstall
    {
        get => _canInstall;
        set => Set(ref _canInstall, value);
    }
}
