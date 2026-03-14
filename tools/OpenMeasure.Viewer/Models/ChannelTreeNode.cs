using System.Collections.ObjectModel;

namespace OpenMeasure.Viewer.Models;

public class ChannelTreeNode
{
    public string Name { get; init; } = "";
    public bool IsGroup { get; init; }
    public bool IsBusGroup { get; init; }
    public OmxGroup? Group { get; init; }
    public OmxChannel? Channel { get; init; }
    public ObservableCollection<ChannelTreeNode> Children { get; init; } = [];

    public string Icon => IsGroup
        ? (IsBusGroup ? "\U0001F697" : "\U0001F4C1")  // car or folder
        : Channel?.DataType switch
        {
            OmxDataType.Binary => "\U0001F4E6",        // package
            OmxDataType.Timestamp => "\U0001F552",      // clock
            _ => "\U0001F4C8"                            // chart
        };

    public string DisplayName => IsGroup
        ? $"{Name} ({Children.Count} channels)"
        : $"{Name} [{Channel?.DataType}]";
}
