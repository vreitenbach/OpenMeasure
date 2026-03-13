namespace OpenMeasure;

/// <summary>
/// Main entry point for reading and writing OMX measurement data files.
/// </summary>
public static class OmxFile
{
    /// <summary>
    /// Create a new OMX file for writing.
    /// </summary>
    public static OmxWriter CreateWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024);
        return new OmxWriter(stream);
    }

    /// <summary>
    /// Open an existing OMX file for reading.
    /// </summary>
    public static OmxReader OpenRead(string path)
    {
        return OmxReader.Open(path);
    }
}
