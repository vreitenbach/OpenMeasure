using System.Buffers.Binary;
using MeasFlow.Bus;

namespace MeasFlow;

/// <summary>
/// A group of related channels within an MEAS file (read-side).
/// </summary>
public sealed class MeasGroup
{
    public string Name { get; }
    public IReadOnlyDictionary<string, MeasValue> Properties => _properties;
    public IReadOnlyList<MeasChannel> Channels => _channels;

    /// <summary>
    /// Bus channel definition if this group represents bus data. Null for non-bus groups.
    /// </summary>
    public BusChannelDefinition? BusDefinition { get; internal set; }

    private readonly Dictionary<string, MeasValue> _properties;
    private readonly List<MeasChannel> _channels;
    private readonly Dictionary<string, MeasChannel> _channelsByName;

    internal MeasGroup(string name, Dictionary<string, MeasValue> properties, List<MeasChannel> channels)
    {
        Name = name;
        _properties = properties;
        _channels = channels;
        _channelsByName = new Dictionary<string, MeasChannel>(channels.Count);
        foreach (var ch in channels)
            _channelsByName[ch.Name] = ch;
    }

    public MeasChannel this[string channelName] =>
        _channelsByName.TryGetValue(channelName, out var ch)
            ? ch
            : throw new KeyNotFoundException($"Channel '{channelName}' not found in group '{Name}'.");

    public bool TryGetChannel(string name, out MeasChannel? channel)
        => _channelsByName.TryGetValue(name, out channel);

    /// <summary>
    /// Decode all values of a named signal from the raw frames.
    /// Requires BusDefinition to be present.
    /// </summary>
    public double[] DecodeSignal(string signalName)
    {
        if (BusDefinition == null)
            throw new InvalidOperationException($"Group '{Name}' has no bus definition.");

        var signalInfo = BusDefinition.FindSignal(signalName)
            ?? throw new KeyNotFoundException($"Signal '{signalName}' not found in bus definition.");

        var (frameDef, pduDef, signalDef) = signalInfo;

        // Get raw frames
        var rawChannel = this[BusDefinition.RawFrameChannelName];
        var rawFrames = rawChannel.ReadFrames();

        var results = new List<double>();

        var busType = BusDefinition.BusConfig.BusType;

        foreach (var rawFrame in rawFrames)
        {
            uint frameId = BusFrameParser.GetFrameId(rawFrame, busType);
            if (frameId != frameDef.FrameId)
                continue;

            var (payloadOffset, payloadLength) = BusFrameParser.GetPayloadRange(rawFrame, busType);
            ReadOnlySpan<byte> payload = rawFrame.AsSpan(payloadOffset, payloadLength);

            // If signal is in a PDU, offset into the PDU
            ReadOnlySpan<byte> signalData = payload;
            if (pduDef != null && pduDef.ByteOffset + pduDef.Length <= payload.Length)
                signalData = payload.Slice(pduDef.ByteOffset, pduDef.Length);

            // Check multiplexing condition
            if (signalDef.MultiplexCondition != null)
            {
                var muxSignalName = signalDef.MultiplexCondition.MultiplexerSignalName;
                var allSignals = pduDef?.Signals ?? frameDef.Signals;
                var muxSig = allSignals.FirstOrDefault(s => s.Name == muxSignalName);
                if (muxSig != null)
                {
                    long muxValue = SignalDecoder.ExtractBits(signalData,
                        muxSig.StartBit, muxSig.BitLength, muxSig.ByteOrder);
                    if (!SignalDecoder.IsSignalActive(signalDef, muxValue))
                        continue;
                }
            }

            double value = SignalDecoder.DecodeSignal(signalData, signalDef);
            results.Add(value);
        }

        return results.ToArray();
    }

    /// <summary>
    /// Decode all values of a named signal from the raw frames, also returning the nanosecond
    /// timestamp of each matching frame (or null if no timestamp channel is present).
    /// </summary>
    public (double[] Values, long[]? FrameNanoseconds) DecodeSignalWithTimestamps(string signalName)
    {
        if (BusDefinition == null)
            throw new InvalidOperationException($"Group '{Name}' has no bus definition.");

        var signalInfo = BusDefinition.FindSignal(signalName)
            ?? throw new KeyNotFoundException($"Signal '{signalName}' not found in bus definition.");

        var (frameDef, pduDef, signalDef) = signalInfo;

        var rawChannel = this[BusDefinition.RawFrameChannelName];
        var rawFrames = rawChannel.ReadFrames();

        var tsChannel = Channels.FirstOrDefault(c => c.DataType == MeasDataType.Timestamp);
        MeasTimestamp[]? timestamps = tsChannel != null ? tsChannel.ReadAll<MeasTimestamp>() : null;

        var busType = BusDefinition.BusConfig.BusType;

        var values = new List<double>();
        var nanos = timestamps != null ? new List<long>() : null;

        for (int i = 0; i < rawFrames.Count; i++)
        {
            var rawFrame = rawFrames[i];
            uint frameId = BusFrameParser.GetFrameId(rawFrame, busType);
            if (frameId != frameDef.FrameId)
                continue;

            var (payloadOffset, payloadLength) = BusFrameParser.GetPayloadRange(rawFrame, busType);
            ReadOnlySpan<byte> payload = rawFrame.AsSpan(payloadOffset, payloadLength);

            ReadOnlySpan<byte> signalData = payload;
            if (pduDef != null && pduDef.ByteOffset + pduDef.Length <= payload.Length)
                signalData = payload.Slice(pduDef.ByteOffset, pduDef.Length);

            if (signalDef.MultiplexCondition != null)
            {
                var muxSignalName = signalDef.MultiplexCondition.MultiplexerSignalName;
                var allSignals = pduDef?.Signals ?? frameDef.Signals;
                var muxSig = allSignals.FirstOrDefault(s => s.Name == muxSignalName);
                if (muxSig != null)
                {
                    long muxValue = SignalDecoder.ExtractBits(signalData,
                        muxSig.StartBit, muxSig.BitLength, muxSig.ByteOrder);
                    if (!SignalDecoder.IsSignalActive(signalDef, muxValue))
                        continue;
                }
            }

            values.Add(SignalDecoder.DecodeSignal(signalData, signalDef));
            if (timestamps != null && i < timestamps.Length)
                nanos!.Add(timestamps[i].Nanoseconds);
        }

        return (values.ToArray(), nanos?.ToArray());
    }
}
