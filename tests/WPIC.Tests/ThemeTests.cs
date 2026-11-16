using System.Windows;
using System.Windows.Media;
using Xunit;

namespace WPIC.Tests;

public class ThemeTests
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

    private static string Bg(Window w) =>
        (w.Background as SolidColorBrush)?.Color.ToString() ?? "null";

    [Fact]
    public void MainWindow_FollowsThemeDictionarySwaps()
    {
        var (dark, light, darkAgain) = RunSta(() =>
        {
            var app = new Application();
            var darkUri = new Uri("/WindowsPostInstallClient;component/Themes/Dark.xaml", UriKind.Relative);
            var lightUri = new Uri("/WindowsPostInstallClient;component/Themes/Light.xaml", UriKind.Relative);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = darkUri });

            var main = new WPIC.MainWindow();
            main.Measure(new Size(800, 600));
            var dark = Bg(main);

            app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = lightUri };
            main.Measure(new Size(800, 600));
            var light = Bg(main);

            app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = darkUri };
            main.Measure(new Size(800, 600));
            var darkAgain = Bg(main);

            app.Shutdown();
            return (dark, light, darkAgain);
        });

        Assert.Equal("#FF15171C", dark);
        Assert.Equal("#FFF2F3F5", light);
        Assert.Equal("#FF15171C", darkAgain);
    }
}