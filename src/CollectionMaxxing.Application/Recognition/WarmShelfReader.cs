using CollectionMaxxing.Application.ShelfReading;

namespace CollectionMaxxing.Application.Recognition;

/// <summary>
/// Asks the shelf reader to load its model without reading anything. Implemented in Infrastructure. <see cref="RequestWarm"/> returns at once and never
/// throws: the call to the reader runs in the background, one at a time, and a failure is only logged.
/// </summary>
public interface IShelfReaderWarmer
{
    void RequestWarm();
}

/// <summary>The "wake the reader" use case: the phone calls it when the scanner opens, so the model is loaded by the time a shelf is snapped.</summary>
public static class WarmShelfReader
{
    public enum Outcome
    {
        /// <summary>A reader is set up; it is being woken in the background (or already is).</summary>
        Started,

        /// <summary>No reader is set up on this host.</summary>
        NoReader,
    }

    public sealed class Handler(IShelfReaderSetup setup, IShelfReaderWarmer warmer)
    {
        public Outcome Handle()
        {
            if (!setup.IsSetUp)
            {
                return Outcome.NoReader;
            }

            warmer.RequestWarm();
            return Outcome.Started;
        }
    }
}
