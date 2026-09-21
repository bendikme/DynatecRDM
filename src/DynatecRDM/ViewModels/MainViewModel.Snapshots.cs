using System.Windows.Threading;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// The last picture of each connection, in the tree and in the detail pane. The pictures outlive
/// their sessions (see <see cref="SnapshotService.LastKnownDirectory"/>), and a running session
/// replaces its connection's picture with every capture, which shows up here as it happens.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _snapshotsSubscribed;
    private string? _detailSnapshotPath;
    private DateTime? _detailSnapshotTakenUtc;

    /// <summary>The selected connection's last picture; null shows the question mark.</summary>
    public string? DetailSnapshotPath
    {
        get => _detailSnapshotPath;
        private set => SetProperty(ref _detailSnapshotPath, value);
    }

    /// <summary>When that picture was taken, or null when there is none.</summary>
    public DateTime? DetailSnapshotTakenUtc
    {
        get => _detailSnapshotTakenUtc;
        private set => SetProperty(ref _detailSnapshotTakenUtc, value);
    }

    /// <summary>Gives every connection in a freshly loaded tree its last picture.</summary>
    private void ApplyLastKnownSnapshots()
    {
        if (!_snapshotsSubscribed)
        {
            SnapshotService.LastKnownChanged += OnLastKnownChanged;
            _snapshotsSubscribed = true;
        }

        var known = SnapshotService.LastKnownSnapshots();
        foreach (var node in _flat)
        {
            if (!node.IsConnection) continue;

            if (known.TryGetValue(node.Id, out var taken))
            {
                node.SnapshotPath = SnapshotService.LastKnownPath(node.Id);
                node.SnapshotTakenUtc = taken;
            }
            else
            {
                node.SnapshotPath = null;
                node.SnapshotTakenUtc = null;
            }
        }
    }

    private void RefreshDetailSnapshot(TreeNodeViewModel? node)
    {
        var connection = node is { IsConnection: true } ? node : null;
        DetailSnapshotPath = connection?.SnapshotPath;
        DetailSnapshotTakenUtc = connection?.SnapshotTakenUtc;
    }

    /// <summary>Raised on the capture's thread whenever a running session took a new picture.</summary>
    private void OnLastKnownChanged(object? sender, Guid connectionId)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_detached || !_index.TryGetValue(connectionId, out var node)) return;

            node.SnapshotPath = SnapshotService.LastKnownPath(connectionId);
            node.SnapshotTakenUtc = DateTime.UtcNow;
            if (ReferenceEquals(_selectedNode, node)) RefreshDetailSnapshot(node);
        }));
    }

    private void DetachSnapshots()
    {
        if (!_snapshotsSubscribed) return;
        SnapshotService.LastKnownChanged -= OnLastKnownChanged;
        _snapshotsSubscribed = false;
    }
}
