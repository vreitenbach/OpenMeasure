namespace MeasFlow;

/// <summary>
/// Main entry point for reading and writing MEAS measurement data files.
/// </summary>
public static class MeasFile
{
    /// <summary>
    /// Create a new MEAS file for writing.
    /// </summary>
    public static MeasWriter CreateWriter(string path, MeasCompression compression = MeasCompression.None)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 64 * 1024);
        return new MeasWriter(stream) { Compression = compression };
    }

    /// <summary>
    /// Open an existing MEAS file for reading.
    /// </summary>
    public static MeasReader OpenRead(string path)
    {
        return MeasReader.Open(path);
    }
}
