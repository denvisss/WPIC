namespace WPIC.ViewModels;

using WPIC.Models;
using WPIC.Services;

public class TweakVm : ObservableObject
{
    public TweakVm(Tweak tweak) => T = tweak;

    public Tweak T { get; }
    public string Category => T.Category;
    public string CategoryName => L10n.T("tweak.cat." + T.Category);
    public string Name => Localized("name", T.Name);
    public string Description => Localized("description", T.Description ?? "");
    public string Risk => T.Risk;
    public bool IsSupported => TweakService.IsSupported(T, TweakService.CurrentBuild, TweakService.CurrentEdition);
    public string Compatibility => TweakService.Compatibility(T);
    public string RestartHint => T.SignOutRequired ? L10n.T("tweaks.signOut") : "";

    private string Localized(string field, string fallback)
    {
        var key = $"tweak.{T.Id}.{field}";
        var text = L10n.T(key);
        return text == key ? fallback : text;
    }

    public void RefreshLanguage() => Raise(null);

    private string _status = "";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value && IsSupported);
    }
}
