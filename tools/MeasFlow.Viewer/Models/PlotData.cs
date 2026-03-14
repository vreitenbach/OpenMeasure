namespace MeasFlow.Viewer.Models;

public record PlotSeries(string Name, double[]? XData, double[] YData);

public record PlotData(string Title, string XLabel, string YLabel, IReadOnlyList<PlotSeries> Series);
