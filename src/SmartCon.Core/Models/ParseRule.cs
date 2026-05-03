namespace SmartCon.Core.Models;

public sealed record ParseRule
{
    public ParseMode Mode { get; init; } = ParseMode.DelimiterSegment;

    public string Delimiter { get; init; } = "-";
    public int SegmentIndex { get; init; } = 1;
    public int SegmentCount { get; init; } = 1;

    public int CharOffset { get; init; }
    public int CharCount { get; init; }

    public string OpenMarker { get; init; } = "(";
    public int OpenMarkerIndex { get; init; } = 1;
    public string CloseMarker { get; init; } = ")";
    public int CloseMarkerIndex { get; init; } = 1;

    public string Marker { get; init; } = "-";
    public int MarkerIndex { get; init; } = 1;

    public static ParseRule DefaultDelimiter(string delimiter = "-", int segmentIndex = 1)
    {
        return new ParseRule
        {
            Mode = ParseMode.DelimiterSegment,
            Delimiter = delimiter,
            SegmentIndex = segmentIndex
        };
    }
}
