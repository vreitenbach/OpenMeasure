namespace MeasFlow.Viewer.Models;

public record ChannelStatisticsRow(
    string Name,
    string Unit,
    long Samples,
    string Min,
    string Max,
    string Mean,
    string StdDev,
    string Sum);
