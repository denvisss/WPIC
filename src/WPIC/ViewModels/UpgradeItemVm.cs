namespace WPIC.ViewModels;

using System.Windows;
using System.Windows.Media;
using WPIC.Models;

/// <summary>Доступное обновление установленного приложения.</summary>
public class UpgradeItemVm : ObservableObject
{
    public UpgradeItemVm(UpgradeEntry entry)
    {
        Source = entry;
        VersionLabel = $"v{entry.Version}";
        AvailableLabel = $"v{entry.Available}";
        TileLetter = AppTile.Letter(entry.Name);
        TileBrush = AppTile.Brush(entry.Id);
    }

    public UpgradeEntry Source { get; }
    public string Name => Source.Name;
    public string Id => Source.Id;
    public string VersionLabel { get; }
    public string AvailableLabel { get; }
    public string TileLetter { get; }
    public Brush TileBrush { get; }

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

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}