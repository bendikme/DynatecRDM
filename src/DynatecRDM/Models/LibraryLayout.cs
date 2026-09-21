namespace DynatecRDM.Models;

/// <summary>The three kinds of item the connection library holds.</summary>
public enum LibraryItemKind
{
    Group = 0,
    Connection = 1,
    MultiConfig = 2,
}

/// <summary>
/// Where one library item sits: the group that holds it (null at the top level) and its position
/// among its siblings. A drag in the manager produces one of these for every item it moved.
/// </summary>
public readonly record struct LayoutChange(LibraryItemKind Kind, Guid Id, Guid? ParentId, int SortOrder);
