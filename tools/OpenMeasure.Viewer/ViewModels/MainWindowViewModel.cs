using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenMeasure.Bus;
using OpenMeasure.Viewer.Models;
using OpenMeasure.Viewer.Services;

namespace OpenMeasure.Viewer.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private OmxReader? _reader;

    [ObservableProperty] private string _title = "OpenMeasure Viewer";
    [ObservableProperty] private bool _isFileOpen;
    [ObservableProperty] private string _statusText = "Drop an .omx file or use File > Open";

    // Tree
    [ObservableProperty] private ObservableCollection<ChannelTreeNode> _treeNodes = [];
    [ObservableProperty] private ChannelTreeNode? _selectedNode;

    // File info
    [ObservableProperty] private string _fileInfo = "";

    // Statistics
    [ObservableProperty] private string _statisticsText = "";
    [ObservableProperty] private bool _hasStatistics;

    // Plot
    [ObservableProperty] private bool _hasPlot;

    // Frames
    [ObservableProperty] private ObservableCollection<FrameRow> _frameRows = [];
    [ObservableProperty] private bool _hasFrames;

    // Signals
    [ObservableProperty] private ObservableCollection<string> _signalNames = [];
    [ObservableProperty] private string? _selectedSignal;
    [ObservableProperty] private bool _hasSignals;

    /// <summary>
    /// Raised when the plot needs to be updated. The View subscribes to this
    /// and updates the ScottPlot control directly (ScottPlot doesn't support MVVM binding).
    /// </summary>
    public event Action<PlotData>? PlotRequested;

    partial void OnSelectedNodeChanged(ChannelTreeNode? value)
    {
        UpdateViews();
    }

    partial void OnSelectedSignalChanged(string? value)
    {
        if (value != null) PlotDecodedSignal(value);
    }

    public void OpenFile(string path)
    {
        try
        {
            _reader?.Dispose();
            _reader = OmxFile.OpenRead(path);
            Title = $"OpenMeasure Viewer — {Path.GetFileName(path)}";
            IsFileOpen = true;
            BuildTree();
            BuildFileInfo(path);
            StatusText = $"Loaded {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseFile()
    {
        _reader?.Dispose();
        _reader = null;
        TreeNodes.Clear();
        IsFileOpen = false;
        HasPlot = false;
        HasStatistics = false;
        HasFrames = false;
        HasSignals = false;
        FileInfo = "";
        Title = "OpenMeasure Viewer";
        StatusText = "Drop an .omx file or use File > Open";
    }

    private void BuildTree()
    {
        TreeNodes.Clear();
        if (_reader == null) return;

        foreach (var group in _reader.Groups)
        {
            var groupNode = new ChannelTreeNode
            {
                Name = group.Name,
                IsGroup = true,
                IsBusGroup = group.BusDefinition != null,
                Group = group,
            };

            foreach (var channel in group.Channels)
            {
                groupNode.Children.Add(new ChannelTreeNode
                {
                    Name = channel.Name,
                    IsGroup = false,
                    Group = group,
                    Channel = channel,
                });
            }

            TreeNodes.Add(groupNode);
        }
    }

    private void BuildFileInfo(string path)
    {
        if (_reader == null) return;

        var fi = new FileInfo(path);
        int totalChannels = _reader.Groups.Sum(g => g.Channels.Count);
        long totalSamples = _reader.Groups.SelectMany(g => g.Channels).Sum(c => c.SampleCount);
        int busGroups = _reader.Groups.Count(g => g.BusDefinition != null);

        FileInfo = $"""
            File: {fi.Name}
            Size: {FormatSize(fi.Length)}
            Created: {_reader.CreatedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss.fff}
            Groups: {_reader.Groups.Count} ({busGroups} bus)
            Channels: {totalChannels}
            Total Samples: {totalSamples:N0}
            """;
    }

    private void UpdateViews()
    {
        if (_reader == null || SelectedNode == null) return;

        UpdateStatistics();
        UpdatePlot();
        UpdateFrames();
        UpdateSignals();
    }

    private void UpdateStatistics()
    {
        var channel = SelectedNode?.Channel;
        if (channel?.Statistics is { } stats)
        {
            HasStatistics = true;
            string unit = channel.Properties.TryGetValue("Unit", out var u)
                ? $" {u.AsString()}" : "";
            StatisticsText = $"""
                Samples: {stats.Count:N0}
                Min: {stats.Min:G6}{unit}
                Max: {stats.Max:G6}{unit}
                Mean: {stats.Mean:G6}{unit}
                StdDev: {stats.StdDev:G6}{unit}
                Sum: {stats.Sum:G6}
                First: {stats.First:G6}{unit}
                Last: {stats.Last:G6}{unit}
                """;
        }
        else
        {
            HasStatistics = false;
            StatisticsText = "";
        }
    }

    private void UpdatePlot()
    {
        var channel = SelectedNode?.Channel;
        var group = SelectedNode?.Group;

        if (channel == null || group == null || !ChannelDataLoader.IsPlottable(channel.DataType))
        {
            HasPlot = false;
            return;
        }

        try
        {
            var yData = ChannelDataLoader.LoadAsDouble(channel);
            if (yData == null || yData.Length == 0)
            {
                HasPlot = false;
                return;
            }

            var xData = ChannelDataLoader.LoadTimestampsAsSeconds(group);
            string unit = channel.Properties.TryGetValue("Unit", out var u) ? u.AsString() : "";

            string xLabel = xData != null ? "Time [s]" : "Sample";
            string yLabel = string.IsNullOrEmpty(unit) ? channel.Name : $"{channel.Name} [{unit}]";

            PlotRequested?.Invoke(new PlotData(channel.Name, xLabel, yLabel, xData, yData));
            HasPlot = true;
            StatusText = $"Plotted {channel.Name}: {yData.Length:N0} samples";
        }
        catch (Exception ex)
        {
            StatusText = $"Plot error: {ex.Message}";
            HasPlot = false;
        }
    }

    private void UpdateFrames()
    {
        var group = SelectedNode?.Group;
        FrameRows.Clear();

        if (group?.BusDefinition == null)
        {
            HasFrames = false;
            return;
        }

        try
        {
            var rawChannel = group.Channels.FirstOrDefault(c => c.Name == group.BusDefinition.RawFrameChannelName);
            var tsChannel = group.Channels.FirstOrDefault(c => c.Name == group.BusDefinition.TimestampChannelName);

            if (rawChannel == null)
            {
                HasFrames = false;
                return;
            }

            var frames = rawChannel.ReadFrames();
            OmxTimestamp[]? timestamps = null;
            if (tsChannel != null)
                timestamps = tsChannel.ReadAll<OmxTimestamp>();

            var busType = group.BusDefinition.BusConfig.BusType;
            int maxFrames = Math.Min(frames.Count, 10_000); // Limit for UI performance

            for (int i = 0; i < maxFrames; i++)
            {
                var frame = frames[i];
                uint frameId = BusFrameParser.GetFrameId(frame, busType);
                var (payloadStart, payloadEnd) = BusFrameParser.GetPayloadRange(frame, busType);
                var payload = frame.AsSpan(payloadStart, payloadEnd - payloadStart);

                string tsStr = timestamps != null && i < timestamps.Length
                    ? timestamps[i].ToDateTimeOffset().ToString("HH:mm:ss.ffffff")
                    : i.ToString();

                string frameName = group.BusDefinition.FindFrame(frameId)?.Name ?? "?";

                FrameRows.Add(new FrameRow(
                    Index: i,
                    Timestamp: tsStr,
                    FrameId: $"0x{frameId:X3}",
                    Dlc: payloadEnd - payloadStart,
                    FrameName: frameName,
                    PayloadHex: FormatHexPayload(payload)
                ));
            }

            HasFrames = FrameRows.Count > 0;
            if (frames.Count > maxFrames)
                StatusText = $"Showing {maxFrames:N0} of {frames.Count:N0} frames";
        }
        catch (Exception ex)
        {
            StatusText = $"Frame parse error: {ex.Message}";
            HasFrames = false;
        }
    }

    private void UpdateSignals()
    {
        var group = SelectedNode?.Group;
        SignalNames.Clear();

        if (group?.BusDefinition == null)
        {
            HasSignals = false;
            return;
        }

        foreach (var (frame, pdu, sig) in group.BusDefinition.AllSignals())
        {
            SignalNames.Add(sig.Name);
        }

        HasSignals = SignalNames.Count > 0;
    }

    private void PlotDecodedSignal(string signalName)
    {
        var group = SelectedNode?.Group;
        if (group?.BusDefinition == null) return;

        try
        {
            var decoded = group.DecodeSignal(signalName);
            if (decoded.Length == 0) return;

            var sigEntry = group.BusDefinition.AllSignals().FirstOrDefault(s => s.Signal.Name == signalName);
            string unit = sigEntry.Signal?.Unit ?? "";

            var xData = ChannelDataLoader.LoadTimestampsAsSeconds(group);
            double[]? xAxis = xData?.Length == decoded.Length ? xData : null;

            string xLabel = xAxis != null ? "Time [s]" : "Sample";
            string yLabel = string.IsNullOrEmpty(unit) ? signalName : $"{signalName} [{unit}]";

            PlotRequested?.Invoke(new PlotData(signalName, xLabel, yLabel, xAxis, decoded));
            HasPlot = true;
            StatusText = $"Decoded signal {signalName}: {decoded.Length:N0} values";
        }
        catch (Exception ex)
        {
            StatusText = $"Signal decode error: {ex.Message}";
        }
    }

    private static string FormatHexPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return "";
        var hex = Convert.ToHexString(payload);
        // Insert spaces between byte pairs: "AABB" -> "AA BB"
        var parts = new string[(hex.Length + 1) / 2];
        for (int i = 0; i < parts.Length; i++)
        {
            int start = i * 2;
            int len = Math.Min(2, hex.Length - start);
            parts[i] = hex.Substring(start, len);
        }
        return string.Join(' ', parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
