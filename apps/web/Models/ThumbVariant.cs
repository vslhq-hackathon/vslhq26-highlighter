namespace Highlighter.Web.Models;

/// <summary>A long-form thumbnail concept as the picker renders it. Fill is a
/// CSS background — the hosted image when one exists, a gradient placeholder
/// while a variant is still rendering.</summary>
public record ThumbVariant(
    int Index, string Direction, string OverlayText, string Fill, string? Error = null);
