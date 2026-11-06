using System.Text.RegularExpressions;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Gives the Vision reads of a run that predates the phone sending each spine's lines (<c>lines</c> on the item) the lines they were
/// made from. The <c>vision_pipeline</c> photo of such a run still carries the OCR lines and pieces (<see cref="EvalPhotoInput.OcrLines"/>,
/// <see cref="EvalPhotoInput.OcrPieces"/>), and on every photo measured the Vision item k is piece k, its text the piece's lines joined by
/// spaces. The phone caps the lines at 1000 and drops empty pieces, so the rebuild is trusted only when it can be checked: the same number
/// of reads as pieces, every index inside the lines, and every read's text equal to its piece's lines joined. Anything less leaves the photo
/// exactly as it came. Pure; <see cref="EvalRunScoring.GroundPhotoAsync"/> asks it before a photo is grounded, so a re-ground of an
/// old run measures what the live path would do.
/// </summary>
public static partial class EvalSpineLines
{
    public static EvalPhotoInput Rebuild(EvalPhotoInput photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        if (photo.Engine != Domain.Scanning.ScanEngine.VisionPipeline
            || photo.Items is not { Count: > 0 } items
            || photo.OcrLines is not { Count: > 0 } lines
            || photo.OcrPieces is not { } pieces
            || items.Count != pieces.Count
            || items.Any(item => item?.Read is null || item.Read.Lines is { Count: > 0 }))
        {
            return photo;
        }

        var rebuilt = new List<IReadOnlyList<string>>(items.Count);
        for (var k = 0; k < items.Count; k++)
        {
            var piece = pieces[k];
            if (piece is null || piece.Count == 0 || piece.Any(index => index < 0 || index >= lines.Count || lines[index]?.Text is null))
            {
                return photo;
            }

            var pieceLines = piece.Select(index => lines[index].Text!).ToList();
            if (Collapse(items[k].Read.Text) != Collapse(string.Join(' ', pieceLines)))
            {
                return photo;
            }

            rebuilt.Add(pieceLines);
        }

        return photo with
        {
            Items = [.. items.Select((item, k) => item with { Read = item.Read with { Lines = rebuilt[k] } })],
        };
    }

    private static string Collapse(string? text) => Whitespace().Replace(text ?? string.Empty, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
