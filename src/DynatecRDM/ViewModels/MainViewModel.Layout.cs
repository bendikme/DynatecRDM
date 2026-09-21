using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>Where a dragged row lands, relative to the row under the pointer.</summary>
public enum DropPlacement
{
    Before = 0,
    After = 1,
    Into = 2,
}

/// <summary>Moving and reordering the library by drag and drop.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// True when <paramref name="dragged"/> may land there. A null target is the empty space
    /// below the tree: the end of the top level.
    /// </summary>
    public bool CanMove(TreeNodeViewModel dragged, TreeNodeViewModel? target, DropPlacement placement) =>
        TryResolveDrop(dragged, target, placement, out _, out _);

    /// <summary>
    /// Moves <paramref name="dragged"/> and renumbers its new siblings, so the order on screen is
    /// exactly the order stored - the quick-launch list reads the same numbers.
    /// </summary>
    public async Task MoveAsync(TreeNodeViewModel dragged, TreeNodeViewModel? target, DropPlacement placement)
    {
        if (!TryResolveDrop(dragged, target, placement, out var parent, out var anchor)) return;

        // Groups and the rest are numbered separately: groups always come first in a group.
        var run = ChildrenOf(parent)
            .Where(n => n.IsGroup == dragged.IsGroup && !ReferenceEquals(n, dragged))
            .ToList();

        var index = anchor is null
            ? run.Count
            : run.IndexOf(anchor) + (placement == DropPlacement.After ? 1 : 0);
        run.Insert(Math.Clamp(index, 0, run.Count), dragged);

        var parentId = parent?.Id;
        var changes = new List<LayoutChange>(run.Count);
        for (var i = 0; i < run.Count; i++)
        {
            var node = run[i];
            var moved = ReferenceEquals(node, dragged) && node.Parent?.Id != parentId;
            if (moved || node.SortOrder != i) changes.Add(new LayoutChange(KindOf(node), node.Id, parentId, i));
        }

        if (changes.Count == 0) return;

        if (!await RunStoreAsync(
                "move an item in the library", Strings.Main_Error_Move,
                () => _services.Store.UpdateLayoutAsync(changes)))
            return;

        // Open the group it went into, or the moved item would seem to vanish.
        if (parent is not null) parent.IsExpanded = true;

        await LoadAsync().ConfigureAwait(true);
        SelectById(dragged.Id);
    }

    private bool TryResolveDrop(
        TreeNodeViewModel dragged,
        TreeNodeViewModel? target,
        DropPlacement placement,
        out TreeNodeViewModel? parent,
        out TreeNodeViewModel? anchor)
    {
        parent = null;
        anchor = null;

        if (ReferenceEquals(target, dragged)) return false;

        if (target is null)
        {
            // The empty space below the tree: the end of the top level.
        }
        else if (placement == DropPlacement.Into || (target.IsGroup && !dragged.IsGroup))
        {
            // A group row only takes connections and multi-configs into itself; they cannot
            // stand between groups, which always come first.
            if (!target.IsGroup) return false;
            parent = target;
        }
        else if (dragged.IsGroup && !target.IsGroup)
        {
            // A group dropped by a connection joins that connection's group, after its subgroups.
            parent = target.Parent;
        }
        else
        {
            parent = target.Parent;
            anchor = target;
        }

        // A group cannot go inside itself or anything it holds.
        if (dragged.IsGroup)
        {
            for (var walker = parent; walker is not null; walker = walker.Parent)
            {
                if (ReferenceEquals(walker, dragged)) return false;
            }
        }

        return true;
    }

    private IEnumerable<TreeNodeViewModel> ChildrenOf(TreeNodeViewModel? parent) =>
        parent is null ? Nodes : parent.Children;

    private static LibraryItemKind KindOf(TreeNodeViewModel node) => node.Kind switch
    {
        TreeNodeKind.Group => LibraryItemKind.Group,
        TreeNodeKind.Connection => LibraryItemKind.Connection,
        _ => LibraryItemKind.MultiConfig,
    };

    /// <summary>The position after the last connection or multi-config in a group, for a new one.</summary>
    private int NextLeafPosition(Guid? groupId) => NextPosition(groupId, groups: false);

    /// <summary>The position after the last subgroup of a group, for a new one.</summary>
    private int NextGroupPosition(Guid? parentId) => NextPosition(parentId, groups: true);

    private int NextPosition(Guid? containerId, bool groups)
    {
        var children = containerId is { } id && _index.TryGetValue(id, out var container)
            ? container.Children
            : Nodes;

        var next = 0;
        foreach (var child in children)
        {
            if (child.IsGroup == groups) next = Math.Max(next, child.SortOrder + 1);
        }
        return next;
    }
}
