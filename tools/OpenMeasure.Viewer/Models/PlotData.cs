namespace OpenMeasure.Viewer.Models;

/// <summary>
/// Data transfer object for plot updates between ViewModel and View.
/// </summary>
public record PlotData(
    string Title,
    string XLabel,
    string YLabel,
    double[]? XData,
    double[] YData);
