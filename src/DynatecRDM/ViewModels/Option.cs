namespace DynatecRDM.ViewModels;

/// <summary>A labelled value for a combo box bound through SelectedValuePath.</summary>
public sealed class Option
{
    public Option(object? value, string label)
    {
        Value = value;
        Label = label;
    }

    public object? Value { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
