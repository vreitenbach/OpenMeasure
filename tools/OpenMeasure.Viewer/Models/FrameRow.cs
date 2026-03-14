namespace OpenMeasure.Viewer.Models;

public record FrameRow(
    int Index,
    string Timestamp,
    string FrameId,
    int Dlc,
    string FrameName,
    string PayloadHex);
