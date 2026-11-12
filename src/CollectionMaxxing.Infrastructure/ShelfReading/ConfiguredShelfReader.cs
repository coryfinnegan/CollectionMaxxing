using CollectionMaxxing.Application.ShelfReading;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary>A shelf reader is set up when <c>ShelfReader:Endpoint</c> names an address and the settings are valid. Read live, so a changed setting shows without a restart where the host reloads configuration.</summary>
public sealed class ConfiguredShelfReader(IOptionsMonitor<ShelfReaderOptions> options) : IShelfReaderSetup
{
    public bool IsSetUp
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(options.CurrentValue.Endpoint);
            }
            catch (OptionsValidationException)
            {
                return false; // bad settings: the reader would answer "unavailable", so the phone is told there is none
            }
        }
    }
}
