namespace StreamForge.Core.Models;

public sealed class IdentificationStepUpdate
{
    public required string Key { get; init; }

    public int Order { get; init; }

    public required string Title { get; init; }

    public IdentificationStepStatus Status { get; init; }

    public string Detail { get; init; } = string.Empty;

    public string StatusDisplay => Status.ToString();

    public string StatusGlyph => Status switch
    {
        IdentificationStepStatus.Identifying => "…",
        IdentificationStepStatus.Success => "✓",
        IdentificationStepStatus.Skipped => "–",
        IdentificationStepStatus.Failed => "!",
        _ => "○"
    };
}
