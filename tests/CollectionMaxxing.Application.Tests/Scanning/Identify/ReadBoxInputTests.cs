using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A box is a hint about where a read sat, never a reason to refuse the read. A value only just outside 0 to 1 is moved into
// range; a box that cannot be used (a member missing, not a number, far out, no size) is read as no box at all.
public class ReadBoxInputTests
{
    private static ReadBox? Of(double? x, double? y, double? width, double? height) => new ReadBoxInput(x, y, width, height).Clamped();

    private static void ShouldBeBox(ReadBox? actual, double x, double y, double width, double height)
    {
        actual.ShouldNotBeNull();
        actual.Value.X.ShouldBe(x, 1e-9);
        actual.Value.Y.ShouldBe(y, 1e-9);
        actual.Value.Width.ShouldBe(width, 1e-9);
        actual.Value.Height.ShouldBe(height, 1e-9);
    }

    [Fact]
    public void A_box_inside_the_picture_is_kept_as_it_is()
    {
        ShouldBeBox(Of(0.1, 0.2, 0.05, 0.6), 0.1, 0.2, 0.05, 0.6);
    }

    [Fact]
    public void A_box_that_fills_the_picture_is_kept()
    {
        ShouldBeBox(Of(0, 0, 1, 1), 0, 0, 1, 1);
    }

    [Fact]
    public void A_start_a_hair_below_zero_is_moved_to_zero()
    {
        ShouldBeBox(Of(-0.005, -0.009, 0.05, 0.6), 0, 0, 0.05, 0.6);
    }

    [Fact]
    public void A_box_that_runs_a_hair_past_the_edge_is_trimmed_to_the_edge()
    {
        ShouldBeBox(Of(0.95, 0.4, 0.0505, 0.6), 0.95, 0.4, 0.05, 0.6);
        ShouldBeBox(Of(0.5, 0.95, 0.1, 0.0599), 0.5, 0.95, 0.1, 0.05);
    }

    [Fact]
    public void A_size_a_hair_above_one_is_trimmed_to_the_picture()
    {
        ShouldBeBox(Of(0, 0, 1.005, 1.009), 0, 0, 1, 1);
    }

    [Theory]
    [InlineData(null, 0.1, 0.05, 0.8)]
    [InlineData(0.1, null, 0.05, 0.8)]
    [InlineData(0.1, 0.1, null, 0.8)]
    [InlineData(0.1, 0.1, 0.05, null)]
    [InlineData(null, null, null, null)]
    public void A_box_with_a_member_missing_is_no_box(double? x, double? y, double? width, double? height)
    {
        Of(x, y, width, height).ShouldBeNull();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_box_with_a_value_that_is_not_a_finite_number_is_no_box(double bad)
    {
        Of(bad, 0.1, 0.05, 0.8).ShouldBeNull();
        Of(0.1, bad, 0.05, 0.8).ShouldBeNull();
        Of(0.1, 0.1, bad, 0.8).ShouldBeNull();
        Of(0.1, 0.1, 0.05, bad).ShouldBeNull();
    }

    [Theory]
    [InlineData(1.5, 0.1, 0.05, 0.8)]
    [InlineData(-0.1, 0.1, 0.05, 0.8)]
    [InlineData(0.1, 1.2, 0.05, 0.8)]
    [InlineData(0.1, -0.5, 0.05, 0.8)]
    [InlineData(0.0, 0.1, 1.5, 0.8)]
    [InlineData(0.1, 0.0, 0.05, 1.2)]
    public void A_value_further_out_of_range_than_the_tolerance_is_no_box(double x, double y, double width, double height)
    {
        Of(x, y, width, height).ShouldBeNull();
    }

    [Theory]
    [InlineData(0.9, 0.1, 0.2, 0.8)]
    [InlineData(0.1, 0.9, 0.05, 0.3)]
    public void A_box_that_runs_well_past_the_edge_is_no_box(double x, double y, double width, double height)
    {
        Of(x, y, width, height).ShouldBeNull();
    }

    [Theory]
    [InlineData(0.1, 0.1, 0.0, 0.8)]
    [InlineData(0.1, 0.1, 0.05, 0.0)]
    [InlineData(0.5, 0.1, -0.1, 0.8)]
    [InlineData(0.1, 0.5, 0.05, -0.2)]
    [InlineData(0.1, 0.1, -0.005, 0.8)] // moved to zero, which is no size
    public void A_box_with_no_width_or_no_height_is_no_box(double x, double y, double width, double height)
    {
        Of(x, y, width, height).ShouldBeNull();
    }

    [Fact]
    public void A_start_at_the_far_edge_with_a_width_trimmed_to_nothing_is_no_box()
    {
        Of(1.0, 0.1, 0.005, 0.8).ShouldBeNull();
    }
}
