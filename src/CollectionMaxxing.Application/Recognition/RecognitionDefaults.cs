namespace CollectionMaxxing.Application.Recognition;

/// <summary>The checked-in defaults (<c>recognition.defaults.json</c>, compiled into the assembly). An override file merges over these.</summary>
public static class RecognitionDefaults
{
    private const string ResourceName = "CollectionMaxxing.Application.Recognition.recognition.defaults.json";

    private static readonly Lazy<string> Text = new(Read);

    /// <summary>The defaults file's text.</summary>
    public static string Json => Text.Value;

    private static string Read()
    {
        using var stream = typeof(RecognitionDefaults).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("recognition.defaults.json is not compiled into the Application assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
