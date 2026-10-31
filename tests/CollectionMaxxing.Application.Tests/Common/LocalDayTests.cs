using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Application.Tests.Common;

public class LocalDayTests
{
    // 01:00 UTC on 31 October is 8 pm on the 30th in US Central daylight time.
    private static readonly DateTimeOffset EveningCentral = new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-300, 2026, 10, 30)] // US Central daylight time: still the 30th
    [InlineData(-360, 2026, 10, 30)] // US Central standard time
    [InlineData(0, 2026, 10, 31)]
    [InlineData(60, 2026, 10, 31)]
    [InlineData(-720, 2026, 10, 30)]
    [InlineData(840, 2026, 10, 31)]
    public void The_day_is_the_one_on_a_clock_at_the_offset(int offsetMinutes, int year, int month, int day)
    {
        LocalDay.Of(EveningCentral, offsetMinutes).ShouldBe(new DateOnly(year, month, day));
    }

    [Fact]
    public void An_offset_east_of_Greenwich_is_already_tomorrow_late_in_the_UTC_day()
    {
        LocalDay.Of(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero), 840).ShouldBe(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void Without_an_offset_the_day_is_the_UTC_day()
    {
        LocalDay.Of(EveningCentral, null).ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Theory]
    [InlineData(-721)]
    [InlineData(841)]
    [InlineData(int.MaxValue)]
    public void An_offset_no_time_zone_has_falls_back_to_the_UTC_day_instead_of_failing(int offsetMinutes)
    {
        LocalDay.Of(EveningCentral, offsetMinutes).ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Theory]
    [InlineData(-720, true)]
    [InlineData(840, true)]
    [InlineData(-721, false)]
    [InlineData(841, false)]
    public void Only_offsets_from_minus_12_to_plus_14_hours_are_real(int offsetMinutes, bool real)
    {
        LocalDay.IsRealOffset(offsetMinutes).ShouldBe(real);
    }

    [Fact]
    public void The_latest_day_a_phone_may_name_is_one_day_after_the_UTC_day()
    {
        LocalDay.Latest(EveningCentral).ShouldBe(new DateOnly(2026, 11, 1));
    }
}
