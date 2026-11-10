using System.Windows.Input;
using System.Windows.Media;

namespace MirrorDeck.ViewModels;

/// <summary>One line of the set-up check list.</summary>
public sealed class DiagnosticItemViewModel
{
    public required string Glyph { get; init; }

    public required Brush Brush { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public string? FixLabel { get; init; }

    public ICommand? FixCommand { get; init; }

    public bool HasFix => FixCommand is not null;
}
