using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeasFlow.Bus;

namespace MeasFlow.Viewer.Models;

public partial class ChannelTreeNode : ObservableObject
{
    [ObservableProperty] private bool _isChecked;

    public string Name { get; init; } = "";
    public bool IsGroup { get; init; }
    public bool IsBusGroup { get; init; }
    public bool IsSignal { get; init; }
    public MeasGroup? Group { get; init; }
    public MeasChannel? Channel { get; init; }
    public SignalDefinition? Signal { get; init; }
    public ObservableCollection<ChannelTreeNode> Children { get; init; } = [];

    public string Icon => IsGroup
        ? (IsBusGroup ? "\U0001F697" : "\U0001F4C1")  // car or folder
        : IsSignal
            ? "\U0001F4F6"  // signal bars
            : Channel?.DataType switch
            {
                MeasDataType.Binary    => "\U0001F4E6",  // package
                MeasDataType.Timestamp => "\U0001F552",  // clock
                _                     => "\U0001F4C8"   // chart
            };

    public string DisplayName => IsGroup
        ? $"{Name} ({Children.Count} channels)"
        : IsSignal
            ? (string.IsNullOrEmpty(Signal?.Unit)
                ? Name
                : $"{Name} [{Signal.Unit}]")
            : $"{Name} [{Channel?.DataType}]";
}
