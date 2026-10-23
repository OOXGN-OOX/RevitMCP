namespace RevitMCP;

internal sealed record RevitFailureDetails(string FailureDefinitionId, string Severity, string Message, long[] FailingElementIds, long[] AdditionalElementIds);
internal sealed class RevitOperationException(string message, object details) : InvalidOperationException(message)
{
    public object Details { get; } = details;
}
