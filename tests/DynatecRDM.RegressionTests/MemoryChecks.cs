using System.Runtime.CompilerServices;
using System.Windows.Controls;
using DynatecRDM.Rdp;

public static partial class Program
{
    private static void SessionOverlayAllocations()
    {
        using var window = new RdpSessionWindow("Overlay allocation regression")
        {
            ShowInTaskbar = false,
            Location = new System.Drawing.Point(-20000, -20000),
        };
        var overlay = new Grid { Background = System.Windows.Media.Brushes.Black };
        window.ShowOverlay(overlay);
        ShowWithoutActivation(window);
        Pump();

        // Warm up WPF/ActiveX first, then measure real resize and hide/show operations. A solid
        // connecting screen must not allocate a desktop-sized bitmap each time it changes size.
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 6; i++)
        {
            window.ClientSize = i % 2 == 0
                ? new System.Drawing.Size(1440, 2560)
                : new System.Drawing.Size(2560, 1440);
            Pump();
            window.HideOverlay();
            window.ShowOverlay(overlay);
            Pump();
            Check(overlay.IsVisible && overlay.ActualWidth > 0 && overlay.ActualHeight > 0,
                "Overlay stopped rendering after resizing or being shown again.");
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"MEMORY overlay resize/show allocated {allocated / 1048576.0:F1} MiB.");
        Check(allocated < 16 * 1024 * 1024,
            $"Solid overlay allocated {allocated / 1048576.0:F1} MiB during six resize/show cycles.");
        window.Close();
    }

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
