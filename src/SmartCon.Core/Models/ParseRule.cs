namespace SmartCon.Core.Models;

public sealed record ParseRule
{
    public ParseMode Mode { get; init; } = ParseMode.DelimiterSegment;

    public string Delimiter { get; init; } = "-";
    public int SegmentIndex { get; init; }
    public int SegmentCount { get; init; } = 1;

    public int CharCount { get; init; }

    public string OpenMarker { get; init; } = "(";
    public string CloseMarker { get; init; } = ")";

    public string Marker { get; init; } = "-";

    public static ParseRule DefaultDelimiter(string delimiter = "-", int segmentIndex = 0)
    {
        return new ParseRule
        {
            Mode = ParseMode.DelimiterSegment,
            Delimiter = delimiter,
            SegmentIndex = segmentIndex
        };
    }
}
