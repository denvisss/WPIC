using System.Windows.Controls;
using System.Windows.Markup;
using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

[Collection("L10n")]
public class L10nExtensionTests
{
    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }

    [Fact]
    public void L10nExtension_SetsValueAndUpdatesOnLanguageChange()
    {
        var (initial, afterSwitch) = RunSta(() =>
        {
            L10n.Initialize("ru");
            const string xaml =
                "<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:loc=\"clr-namespace:WPIC.Services;assembly=WindowsPostInstallClient\" " +
                "Text=\"{loc:L10n main.ready}\"/>";
            var block = (TextBlock)XamlReader.Parse(xaml);
            var initial = block.Text;
            L10n.SetLanguage("en");
            return (initial, block.Text);
        });

        Assert.Equal("Готов", initial);
        Assert.Equal("Ready", afterSwitch);
    }
}