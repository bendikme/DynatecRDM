using DynatecRDM.Models;

namespace DynatecRDM.ViewModels;

/// <summary>
/// The one ordering rule of the connection library, shared by the manager's tree and the
/// quick-launch list so both show things in the same order.
///
/// Inside every group - and at the top level - subgroups come first, then connections and
/// multi-configs together. Each run is ordered by its position (SortOrder), which dragging in the
/// manager sets, and by name where positions are equal.
/// </summary>
public static class LibraryOrder
{
    public static int Compare(bool groupA, int sortA, string nameA, bool groupB, int sortB, string nameB)
    {
        if (groupA != groupB) return groupA ? -1 : 1;

        var order = sortA.CompareTo(sortB);
        return order != 0 ? order : string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>The name a connection is listed under: its own, or its address when it has none.</summary>
    public static string DisplayName(RdpConnection connection) =>
        string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name;

    /// <summary>
    /// Every item's place in the manager's tree read top to bottom with all groups open: groups
    /// depth first, each followed by what it holds. Items whose group no longer exists sit at the
    /// top level, as the tree shows them.
    /// </summary>
    public static Dictionary<Guid, int> Positions(
        IEnumerable<ConnectionGroup> groups,
        IEnumerable<RdpConnection> connections,
        IEnumerable<MultiConfig> multiConfigs)
    {
        var groupList = groups.ToList();
        var known = groupList.Select(g => g.Id).ToHashSet();

        // Parent -> children, with null standing for the top level.
        var subgroups = new Dictionary<Guid, List<ConnectionGroup>>();
        var topGroups = new List<ConnectionGroup>();
        foreach (var group in groupList)
        {
            if (group.ParentId is { } parent && parent != group.Id && known.Contains(parent))
            {
                if (!subgroups.TryGetValue(parent, out var list)) subgroups[parent] = list = new();
                list.Add(group);
            }
            else
            {
                topGroups.Add(group);
            }
        }

        var leaves = new Dictionary<Guid, List<(Guid Id, int Sort, string Name)>>();
        var topLeaves = new List<(Guid Id, int Sort, string Name)>();

        void AddLeaf(Guid id, Guid? groupId, int sort, string name)
        {
            if (groupId is { } g && known.Contains(g))
            {
                if (!leaves.TryGetValue(g, out var list)) leaves[g] = list = new();
                list.Add((id, sort, name));
            }
            else
            {
                topLeaves.Add((id, sort, name));
            }
        }

        foreach (var c in connections) AddLeaf(c.Id, c.GroupId, c.SortOrder, DisplayName(c));
        foreach (var m in multiConfigs) AddLeaf(m.Id, m.GroupId, m.SortOrder, m.Name);

        var positions = new Dictionary<Guid, int>();
        var visited = new HashSet<Guid>();

        void Walk(List<ConnectionGroup> groupRun, List<(Guid Id, int Sort, string Name)> leafRun)
        {
            groupRun.Sort((a, b) => Compare(true, a.SortOrder, a.Name, true, b.SortOrder, b.Name));
            foreach (var group in groupRun)
            {
                // A parent chain that loops back is shown once, never walked forever.
                if (!visited.Add(group.Id)) continue;

                positions[group.Id] = positions.Count;
                Walk(subgroups.TryGetValue(group.Id, out var inner) ? inner : new(),
                     leaves.TryGetValue(group.Id, out var held) ? held : new());
            }

            leafRun.Sort((a, b) => Compare(false, a.Sort, a.Name, false, b.Sort, b.Name));
            foreach (var leaf in leafRun) positions[leaf.Id] = positions.Count;
        }

        Walk(topGroups, topLeaves);

        // Groups caught in a loop were never reached from the top; list them after everything.
        foreach (var group in groupList)
        {
            if (visited.Contains(group.Id)) continue;
            visited.Add(group.Id);
            positions[group.Id] = positions.Count;
            if (leaves.TryGetValue(group.Id, out var held))
            {
                held.Sort((a, b) => Compare(false, a.Sort, a.Name, false, b.Sort, b.Name));
                foreach (var leaf in held) positions[leaf.Id] = positions.Count;
            }
        }

        return positions;
    }
}
