using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeasFlow.Bus;
using MeasFlow.Viewer.Models;
using MeasFlow.Viewer.Services;

namespace MeasFlow.Viewer.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private MeasReader? _reader;

    [ObservableProperty] private string _title = "MeasFlow Viewer";
    [ObservableProperty] private bool _isFileOpen;
    [ObservableProperty] private string _statusText = "Drop an .meas file or use File > Open";

    // Tree
    [ObservableProperty] private ObservableCollection<ChannelTreeNode> _treeNodes = [];
    [ObservableProperty] private ChannelTreeNode? _selectedNode;

    // File info
    [ObservableProperty] private string _fileInfo = "";

    // Statistics
    [ObservableProperty] private ObservableCollection<ChannelStatisticsRow> _statisticsRows = [];
    [ObservableProperty] private bool _hasStatistics;

    // Plot
    [ObservableProperty] private bool _hasPlot;

    // Frames
    [ObservableProperty] private ObservableCollection<FrameRow> _frameRows = [];
    [ObservableProperty] private bool _hasFrames;

    // Signals
    [ObservableProperty] private bool _hasSignals;
    [ObservableProperty] private bool _hasSignalPlot;

    /// Raised when the main (channel) plot needs to be updated.
    public event Action<PlotData>? PlotRequested;

    /// Raised when the decoded-signal plot needs to be updated.
    public event Action<PlotData>? SignalPlotRequested;

    partial void OnSelectedNodeChanged(ChannelTreeNode? value)
    {
        UpdateViews();
    }

    public void OpenFile(string path)
    {
        try
        {
            _reader?.Dispose();
            _reader = MeasFile.OpenRead(path);
            Title = $"MeasFlow Viewer — {Path.GetFileName(path)}";
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

        foreach (var group in TreeNodes)
            foreach (var child in group.Children)
            {
                child.PropertyChanged -= OnNodePropertyChanged;
                foreach (var grandchild in child.Children)
                    grandchild.PropertyChanged -= OnNodePropertyChanged;
            }

        TreeNodes.Clear();
        IsFileOpen = false;
        HasPlot = false;
        HasStatistics = false;
        StatisticsRows.Clear();
        HasFrames = false;
        HasSignals = false;
        HasSignalPlot = false;
        FileInfo = "";
        Title = "MeasFlow Viewer";
        StatusText = "Drop an .meas file or use File > Open";
    }

    private void BuildTree()
    {
        // Unsubscribe from existing nodes before clearing
        foreach (var group in TreeNodes)
            foreach (var child in group.Children)
            {
                child.PropertyChanged -= OnNodePropertyChanged;
                foreach (var grandchild in child.Children)
                    grandchild.PropertyChanged -= OnNodePropertyChanged;
            }

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
                var node = new ChannelTreeNode
                {
                    Name = channel.Name,
                    IsGroup = false,
                    Group = group,
                    Channel = channel,
                };
                node.PropertyChanged += OnNodePropertyChanged;
                groupNode.Children.Add(node);
            }

            if (group.BusDefinition != null)
            {
                var signalsGroup = new ChannelTreeNode
                {
                    Name = "Signals",
                    IsGroup = true,
                    Group = group,
                };
                foreach (var (_, _, sig) in group.BusDefinition.AllSignals())
                {
                    var sigNode = new ChannelTreeNode
                    {
                        Name = sig.Name,
                        IsGroup = false,
                        IsSignal = true,
                        Group = group,
                        Signal = sig,
                    };
                    sigNode.PropertyChanged += OnNodePropertyChanged;
                    signalsGroup.Children.Add(sigNode);
                }
                if (signalsGroup.Children.Count > 0)
                    groupNode.Children.Add(signalsGroup);
            }

            TreeNodes.Add(groupNode);
        }

        HasSignals = GetAllSignalNodes().Any();
    }

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelTreeNode.IsChecked))
        {
            if (sender is ChannelTreeNode node && node.IsSignal)
                RebuildSignalPlot();
            else
            {
                RebuildMultiPlot();
                UpdateStatistics();
            }
        }
    }

    private IEnumerable<ChannelTreeNode> GetAllChannelNodes()
    {
        foreach (var group in TreeNodes)
            foreach (var child in group.Children)
                if (!child.IsGroup)
                    yield return child;
    }

    private IEnumerable<ChannelTreeNode> GetAllSignalNodes()
    {
        foreach (var group in TreeNodes)
            foreach (var child in group.Children)
                if (child.IsGroup)
                    foreach (var grandchild in child.Children)
                        if (grandchild.IsSignal)
                            yield return grandchild;
    }

    private void RebuildMultiPlot()
    {
        if (_reader == null) return;

        var checkedNodes = GetAllChannelNodes().Where(n => n.IsChecked).ToList();
        if (checkedNodes.Count == 0)
        {
            HasPlot = false;
            return;
        }

        var series = new List<PlotSeries>();
        foreach (var node in checkedNodes)
        {
            if (node.Channel == null || node.Group == null) continue;
            if (!ChannelDataLoader.IsPlottable(node.Channel.DataType)) continue;

            var yData = ChannelDataLoader.LoadAsDouble(node.Channel);
            if (yData == null || yData.Length == 0) continue;

            var xData = ChannelDataLoader.LoadTimestampsAsSeconds(node.Group);
            double[]? xAxis = xData?.Length == yData.Length ? xData : null;

            string unit = node.Channel.Properties.TryGetValue("Unit", out var u) ? u.AsString() : "";
            string name = string.IsNullOrEmpty(unit) ? node.Channel.Name : $"{node.Channel.Name} [{unit}]";
            series.Add(new PlotSeries(name, xAxis, yData));
        }

        HasPlot = series.Count > 0;
        if (HasPlot)
        {
            bool allSameX = series.All(s => s.XData != null);
            string xLabel = allSameX ? "Time [s]" : "Sample";
            PlotRequested?.Invoke(new PlotData("", xLabel, "Value", series));
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
        UpdateFrames();
    }

    private void UpdateStatistics()
    {
        StatisticsRows.Clear();

        var checkedNodes = GetAllChannelNodes().Where(n => n.IsChecked).ToList();
        var sources = checkedNodes.Count > 0
            ? checkedNodes
            : (SelectedNode?.Channel != null ? [SelectedNode] : []);

        foreach (var node in sources)
        {
            var ch = node.Channel;
            if (ch?.Statistics is not { } stats) continue;

            string unit = ch.Properties.TryGetValue("Unit", out var u) ? u.AsString() : "";
            string fmt(double v) => v.ToString("G6");

            StatisticsRows.Add(new ChannelStatisticsRow(
                Name:    ch.Name,
                Unit:    unit,
                Samples: stats.Count,
                Min:     fmt(stats.Min),
                Max:     fmt(stats.Max),
                Mean:    fmt(stats.Mean),
                StdDev:  fmt(stats.StdDev),
                Sum:     fmt(stats.Sum)));
        }

        HasStatistics = StatisticsRows.Count > 0;
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
            MeasTimestamp[]? timestamps = null;
            if (tsChannel != null)
                timestamps = tsChannel.ReadAll<MeasTimestamp>();

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

    private void RebuildSignalPlot()
    {
        var checkedSignals = GetAllSignalNodes().Where(n => n.IsChecked).ToList();
        if (checkedSignals.Count == 0)
        {
            HasSignalPlot = false;
            return;
        }

        // Determine global time base: minimum first-frame timestamp across all involved groups
        long globalMinNanos = long.MaxValue;
        foreach (var node in checkedSignals)
        {
            if (node.Group == null) continue;
            var tsChannel = node.Group.Channels.FirstOrDefault(c => c.DataType == MeasDataType.Timestamp);
            if (tsChannel == null || tsChannel.SampleCount == 0) continue;
            var first = tsChannel.ReadAll<MeasTimestamp>();
            if (first.Length > 0 && first[0].Nanoseconds < globalMinNanos)
                globalMinNanos = first[0].Nanoseconds;
        }

        var series = new List<PlotSeries>();
        foreach (var node in checkedSignals)
        {
            if (node.Group == null || node.Signal == null) continue;
            try
            {
                var (decoded, frameNanos) = node.Group.DecodeSignalWithTimestamps(node.Name);
                if (decoded.Length == 0) continue;

                double[]? xAxis = null;
                if (frameNanos != null && frameNanos.Length == decoded.Length && globalMinNanos != long.MaxValue)
                {
                    xAxis = new double[frameNanos.Length];
                    for (int i = 0; i < frameNanos.Length; i++)
                        xAxis[i] = (frameNanos[i] - globalMinNanos) / 1_000_000_000.0;
                }

                string unit = node.Signal.Unit ?? "";
                string name = string.IsNullOrEmpty(unit) ? node.Name : $"{node.Name} [{unit}]";
                series.Add(new PlotSeries(name, xAxis, decoded));
            }
            catch { /* skip broken signal */ }
        }

        HasSignalPlot = series.Count > 0;
        if (HasSignalPlot)
        {
            bool allHaveTime = series.All(s => s.XData != null);
            SignalPlotRequested?.Invoke(new PlotData("", allHaveTime ? "Time [s]" : "Sample", "Value", series));
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
