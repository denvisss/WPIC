using System.Windows;
using Xunit;

namespace WPIC.Tests;

public class AppIconTests
{
    [Fact]
    public void MainWindowUsesBundledIconAndNewTitle()
    {
        var (title, iconWidth) = RunSta(() =>
        {
            var window = new WPIC.MainWindow();
            window.Measure(new Size(100, 100));
            var width = window.Icon?.Width ?? 0;
            window.Close();
            return (window.Title, width);
        });
        Assert.Equal("Windows Post-Install Client", title);
        Assert.True(iconWidth >= 16, "window icon is bundled");
    }

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
}
