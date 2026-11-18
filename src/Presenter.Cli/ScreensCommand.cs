using Presenter.Core;
using Presenter.Windows;

namespace Presenter.Cli;

internal static class ScreensCommand
{
    public static int Run()
    {
        var all = Monitors.All();
        for (int i = 0; i < all.Count; i++)
        {
            var m = all[i];
            var vp = SlideGeometry.PredictedViewport(m.WorkArea.Size, m.Dpi);
            Console.WriteLine($"{i + 1}. {m}: Paint's viewport {vp} (predicted); slides "
                              + $"16:9 {SlideGeometry.SlideSize(vp, 16.0 / 9)}, 4:3 {SlideGeometry.SlideSize(vp, 4.0 / 3)}");
        }
        return 0;
    }
}
