using System.Runtime.CompilerServices;
using System.Windows.Controls;
using DynatecRDM.Rdp;

public static partial class Program
{
    private static void SessionLifetime()
    {
        var references = new List<WeakReference>();
        for (var i = 0; i < 8; i++) references.AddRange(OpenAndCloseSession());

        // Pump deferred WPF/native teardown before checking reachability. Forced collection belongs
        // only in this test; it cannot repair a production object held by a COM event subscription.
        for (var i = 0; i < 5; i++)
        {
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        var retained = references.Count(reference => reference.IsAlive);
        Check(retained == 0, $"{retained}/{references.Count} closed session objects are still rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] OpenAndCloseSession()
    {
        using var window = new RdpSessionWindow("Memory regression")
        {
            ShowInTaskbar = false,
            Location = new System.Drawing.Point(-20000, -20000),
        };
        var overlay = new Grid { Background = System.Windows.Media.Brushes.Black };
        window.ShowOverlay(overlay);
        ShowWithoutActivation(window);
        window.HideOverlay();
        window.ShowOverlay(overlay);
        Pump();
        var references = new[] { new WeakReference(window), new WeakReference(window.Host.WinFormsControl), new WeakReference(overlay) };
        window.Close();
        return references;
    }
}
