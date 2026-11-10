using System.Threading;

namespace DotaTikTok;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, @"Local\DotaTikTok.SingleInstance", out var createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm());
    }
}
