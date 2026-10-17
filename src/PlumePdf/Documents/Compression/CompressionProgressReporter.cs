namespace PlumePdf.Documents.Compression;

/// <summary>
/// Reports one <c>Compress</c> call's progress to the caller's <see cref="IProgress{T}"/>,
/// dropping a report identical to the one before it, so a stage's closing report never repeats
/// its last step. Created only when the caller passed a progress sink; every caller holds a
/// nullable reference and skips reporting when it is null.
/// </summary>
internal sealed class CompressionProgressReporter(IProgress<CompressionProgress> sink)
{
    private CompressionProgress? _last;

    /// <summary>A reporter for <paramref name="sink"/>, or null when there is none.</summary>
    public static CompressionProgressReporter? For(IProgress<CompressionProgress>? sink) =>
        sink is null ? null : new CompressionProgressReporter(sink);

    /// <summary>Reports <paramref name="completed"/> of <paramref name="total"/> for <paramref name="stage"/>, synchronously.</summary>
    public void Report(CompressionStage stage, long completed, long? total)
    {
        var report = new CompressionProgress(stage, completed, total);
        if (_last == report)
        {
            return;
        }

        _last = report;
        sink.Report(report);
    }
}
