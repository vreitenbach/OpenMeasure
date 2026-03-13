namespace OpenMeasure;

/// <summary>
/// A group of related channels within an OMX file (read-side).
/// </summary>
public sealed class OmxGroup
{
    public string Name { get; }
    public IReadOnlyDictionary<string, OmxValue> Properties => _properties;
    public IReadOnlyList<OmxChannel> Channels => _channels;

    private readonly Dictionary<string, OmxValue> _properties;
    private readonly List<OmxChannel> _channels;
    private readonly Dictionary<string, OmxChannel> _channelsByName;

    internal OmxGroup(string name, Dictionary<string, OmxValue> properties, List<OmxChannel> channels)
    {
        Name = name;
        _properties = properties;
        _channels = channels;
        _channelsByName = new Dictionary<string, OmxChannel>(channels.Count);
        foreach (var ch in channels)
            _channelsByName[ch.Name] = ch;
    }

    public OmxChannel this[string channelName] =>
        _channelsByName.TryGetValue(channelName, out var ch)
            ? ch
            : throw new KeyNotFoundException($"Channel '{channelName}' not found in group '{Name}'.");

    public bool TryGetChannel(string name, out OmxChannel? channel)
        => _channelsByName.TryGetValue(name, out channel);
}
