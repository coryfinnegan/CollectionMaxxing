using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Recognition;

/// <summary>
/// What the phone fetches to recognise things (docs/02 section 1 and "The Apple model call"): the model's prompt, the
/// auto-snap gate, the engine's limits and how a wide shot is split. Served from <c>GET /api/v1/recognition/config</c>;
/// tuned by editing one file on the PC, so tuning needs no iOS build. The phone treats all of it as data and keeps a
/// built-in safe default when it cannot fetch or understand it.
/// </summary>
/// <param name="Version">A hash of the content below; it changes whenever the content does. It is also the response's ETag.</param>
public sealed record RecognitionConfig(
    string Version,
    RecognitionPrompt Prompt,
    RecognitionAutoSnap AutoSnap,
    RecognitionEngine Engine,
    RecognitionSplitting Splitting);

/// <summary>
/// The text the phone gives the model: <see cref="Template"/>, then <see cref="OutputRules"/> after a blank line. Each is at
/// most 4096 UTF-8 bytes of text, with line breaks and tabs but no other control or invisible characters. Between them they
/// must keep the two rules the app relies on (see <see cref="RecognitionConfigRules.RequiredPromptRules"/>).
/// </summary>
public sealed record RecognitionPrompt(string Template, string OutputRules);

/// <summary>
/// When the camera takes a picture by itself (docs/02 section 1, docs/05). The gate is open while no barcode is in view,
/// the device has been steady, the frame is sharp, there is something to read and it differs from the last snap.
/// </summary>
/// <param name="SteadyMs">How long the device must be steady first, in milliseconds. 100 to 5000.</param>
/// <param name="MinSharpness">The least focus score, from 0 (blurred) to 1 (sharp), the frame needs. 0 to 1.</param>
/// <param name="MinTextBoxes">How many title-sized text boxes Vision must find, unless a rectangle fills the frame. 1 to 50.</param>
/// <param name="MinRectangleCoverage">The share of the frame a cover, cartridge or sleeve rectangle must fill, as a fraction. 0.01 to 1.</param>
/// <param name="CooldownHashDistance">After a snap, the Hamming distance in bits between 64-bit perceptual hashes the frame must move before the next. 1 to 64.</param>
/// <param name="MisfireRaise">The relative rise applied to the sharpness, coverage and steady-time thresholds for each misfire (a snap that found nothing) in one session. 0 to 1.</param>
public sealed record RecognitionAutoSnap(
    int SteadyMs,
    double MinSharpness,
    int MinTextBoxes,
    double MinRectangleCoverage,
    int CooldownHashDistance,
    double MisfireRaise);

/// <summary>Limits of the Apple model call (docs/02 "Practical limits").</summary>
/// <param name="ModelTimeoutMs">How long one snap may wait for the model before the Vision text engine takes over, in milliseconds. 500 to 60000.</param>
/// <param name="MaxItemsPerSnap">The most items one snap may report; the rest are dropped. 1 to 200.</param>
public sealed record RecognitionEngine(int ModelTimeoutMs, int MaxItemsPerSnap);

/// <summary>How a wide shot is split (docs/02 section 4 and "Practical limits").</summary>
/// <param name="HalvesWhenItemsOver">A shot whose text columns number more than this is split into halves for the model's context budget. 2 to 200.</param>
/// <param name="MinSpineColumnWidth">The narrowest a spine column may be to count as an item, as a fraction of the picture's width. 0.005 to 0.25.</param>
public sealed record RecognitionSplitting(int HalvesWhenItemsOver, double MinSpineColumnWidth);

/// <summary>
/// The live recognition settings on the server: what the phone is served and the scoring options the identify endpoints read.
/// Both reflect the latest <em>valid</em> override file; a file that fails validation never replaces them.
/// </summary>
public interface IRecognitionSettings
{
    /// <summary>What the phone is served right now.</summary>
    RecognitionConfig Config { get; }

    /// <summary>The scoring weights and thresholds in force right now. Read it on every use: it changes while the app runs.</summary>
    ScoringOptions Scoring { get; }
}

/// <summary>The recognition config use case.</summary>
public static class GetRecognitionConfig
{
    public sealed class Handler(IRecognitionSettings settings)
    {
        public RecognitionConfig Handle() => settings.Config;
    }
}
