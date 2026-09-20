using System.Collections.ObjectModel;
using System.Windows.Input;
using DynatecRDM.Models;

namespace DynatecRDM.ViewModels;

/// <summary>What a <see cref="TreeNodeViewModel"/> stands for in the library tree.</summary>
public enum TreeNodeKind
{
    Group = 0,
    Connection = 1,
    MultiConfig = 2,
}

/// <summary>
/// One row of the connection library. A single node type covers groups, connections and
/// multi-configs so the tree can be built in one pass off the UI thread and bound with a
/// single hierarchical template.
/// </summary>
public sealed class TreeNodeViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _subtitle = string.Empty;
    private string? _color;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _isRunning;
    private bool _favorite;

    public TreeNodeViewModel(TreeNodeKind kind, Guid id, object model)
    {
        Kind = kind;
        Id = id;
        Model = model;
    }

    /// <summary>Which of the three entity types this node carries.</summary>
    public TreeNodeKind Kind { get; }

    /// <summary>Identifier of the underlying entity.</summary>
    public Guid Id { get; }

    /// <summary>The group, connection or multi-config behind this row.</summary>
    public object Model { get; }

    /// <summary>Owning group node, or null at the root.</summary>
    public TreeNodeViewModel? Parent { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public ConnectionGroup? AsGroup => Model as ConnectionGroup;
    public RdpConnection? AsConnection => Model as RdpConnection;
    public MultiConfig? AsMultiConfig => Model as MultiConfig;

    public bool IsGroup => Kind == TreeNodeKind.Group;
    public bool IsConnection => Kind == TreeNodeKind.Connection;
    public bool IsMultiConfig => Kind == TreeNodeKind.MultiConfig;

    /// <summary>Ordering key taken from the entity's SortOrder.</summary>
    public int SortOrder { get; set; }

    /// <summary>Lower-cased haystack used by the search box; built once when the tree loads.</summary>
    public string SearchText { get; set; } = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    /// <summary>Secondary line: host:port for a connection, a count for a group or set.</summary>
    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value ?? string.Empty);
    }

    /// <summary>
    /// A TreeViewItem whose header is a template takes its accessible name from the bound item's
    /// ToString(). Without this a screen reader announces the type name for every row.
    /// </summary>
    public override string ToString() => _name;

    /// <summary>Accent colour as #RRGGBB, or null when the item has none.</summary>
    public string? Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, value)) OnPropertyChanged(nameof(HasColor));
        }
    }

    public bool HasColor => !string.IsNullOrWhiteSpace(_color);

    public bool Favorite
    {
        get => _favorite;
        set => SetProperty(ref _favorite, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Two-way bound to the container's IsSelected. <see cref="SelectionChanged"/> lets the
    /// owning view model track the selection without a code-behind event handler.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            try
            {
                SelectionChanged?.Invoke(this, value);
            }
            catch (Exception ex)
            {
                Services.AppLog.Warn("Tree selection handler failed.", ex);
            }
        }
    }

    /// <summary>False hides the row while a search term or a filter chip is active.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>True when this item - or, for a group, something inside it - has a live session.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    /// <summary>Raised whenever <see cref="IsSelected"/> changes, in either direction.</summary>
    public Action<TreeNodeViewModel, bool>? SelectionChanged { get; set; }

    /// <summary>
    /// The owning view model's activate command, held on the node so a double-click inside
    /// the item template can reach it: an InputBinding sees only its own data context.
    /// </summary>
    public ICommand? ActivateCommand { get; set; }

    /// <summary>Expands every ancestor so this node can be realised and selected.</summary>
    public void ExpandAncestors()
    {
        var parent = Parent;
        while (parent is not null)
        {
            parent.IsVisible = true;
            parent.IsExpanded = true;
            parent = parent.Parent;
        }
    }

    /// <summary>Depth-first walk over this node and everything below it.</summary>
    public IEnumerable<TreeNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants()) yield return node;
        }
    }
}
