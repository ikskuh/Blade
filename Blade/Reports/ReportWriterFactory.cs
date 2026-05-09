namespace Blade.Reports;

/// <summary>
/// Creates report writers for explicit and implicit report output targets.
/// </summary>
internal static class ReportWriterFactory
{
    /// <summary>
    /// Creates the writer for the requested report format.
    /// </summary>
    public static IReportWriter CreateWriter(ReportFormat format)
    {
        return format switch
        {
            ReportFormat.Text => new TextReportWriter(false),
            ReportFormat.Assembly => new TextReportWriter(true ),
            ReportFormat.Html => new HtmlReportWriter(),
            ReportFormat.Json => new JsonReportWriter(),
            _ => Assert.UnreachableValue<IReportWriter>(), // pragma: force-coverage
        };
    }
}
