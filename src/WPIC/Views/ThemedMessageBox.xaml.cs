using System.Windows;
using System.Windows.Controls;
using WPIC.Services;

namespace WPIC.Views;

/// <summary>
/// Тематическое окно сообщений (замена системному MessageBox — чтобы диалоги
/// соответствовали выбранной теме оформления).
/// </summary>
public partial class ThemedMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public ThemedMessageBox()
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeService.ApplyTitleBar(this, ThemeService.Resolved == ThemeService.Dark);
    }

    public static MessageBoxResult Show(
        string text,
        string title = "",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        Window? owner = null)
    {
        var box = new ThemedMessageBox
        {
            Title = string.IsNullOrWhiteSpace(title) ? L10n.T("app.name") : title
        };
        box.Initialize(text, buttons, image);

        if (owner != null)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        box.ShowDialog();
        return box._result;
    }

    /// <summary>Диалог с дополнительным чекбоксом-опцией (например, удаление provisioned-пакетов).</summary>
    public static MessageBoxResult ShowWithOption(
        string text,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        Window? owner,
        string optionText,
        out bool optionChecked)
    {
        var box = new ThemedMessageBox
        {
            Title = string.IsNullOrWhiteSpace(title) ? L10n.T("app.name") : title
        };
        box.Initialize(text, buttons, image);
        ((TextBlock)box.ChkOption.Content).Text = optionText;
        box.ChkOption.Visibility = Visibility.Visible;

        if (owner != null)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        box.ShowDialog();
        optionChecked = box.ChkOption.IsChecked == true;
        return box._result;
    }

    private void Initialize(string text, MessageBoxButton buttons, MessageBoxImage image)
    {
        TxtMessage.Text = text;
        TxtIcon.Text = image switch
        {
            MessageBoxImage.Information => "ℹ️",
            MessageBoxImage.Warning => "⚠️",
            MessageBoxImage.Error => "❌",
            MessageBoxImage.Question => "❓",
            _ => ""
        };

        var affirmative = new[] { MessageBoxButton.OK, MessageBoxButton.YesNo, MessageBoxButton.OKCancel }.Contains(buttons);
        var defaultTitle = L10n.T("app.name");

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton(L10n.T("msg.ok"), MessageBoxResult.OK, affirmative, isCancel: false);
                break;
            case MessageBoxButton.OKCancel:
                AddButton(L10n.T("msg.ok"), MessageBoxResult.OK, affirmative, isCancel: false);
                AddButton(L10n.T("msg.cancel"), MessageBoxResult.Cancel, false, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton(L10n.T("msg.yes"), MessageBoxResult.Yes, affirmative, isCancel: false);
                AddButton(L10n.T("msg.no"), MessageBoxResult.No, false, isCancel: false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton(L10n.T("msg.yes"), MessageBoxResult.Yes, affirmative, isCancel: false);
                AddButton(L10n.T("msg.no"), MessageBoxResult.No, false, isCancel: false);
                AddButton(L10n.T("msg.cancel"), MessageBoxResult.Cancel, false, isCancel: true);
                break;
        }
    }

    private void AddButton(string caption, MessageBoxResult result, bool isPrimary, bool isCancel)
    {
        var button = new Button
        {
            Content = caption,
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = isCancel
        };
        if (isPrimary)
            button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) =>
        {
            _result = result;
            Close();
        };
        ButtonsPanel.Children.Add(button);
    }
}
