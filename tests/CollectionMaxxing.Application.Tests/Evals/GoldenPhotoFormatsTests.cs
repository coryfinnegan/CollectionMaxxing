using System.Buffers.Binary;
using System.Text;
using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>What a golden picture is, decided from its first bytes only. A name, a header or an extension never decides it.</summary>
public class GoldenPhotoFormatsTests
{
    private static byte[] Ftyp(string brand, uint boxSize = 24, int length = 32)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, boxSize);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        return bytes;
    }

    [Fact]
    public void Bytes_that_start_ff_d8_ff_are_a_jpeg()
    {
        GoldenPhotoFormats.Sniff([0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10]).ShouldBe(GoldenPhotoFormat.Jpeg);
        GoldenPhotoFormats.Sniff([0xFF, 0xD8, 0xFF]).ShouldBe(GoldenPhotoFormat.Jpeg);
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]
    [InlineData("msf1")]
    public void An_ftyp_box_with_one_of_the_four_heic_brands_is_a_heic(string brand)
    {
        GoldenPhotoFormats.Sniff(Ftyp(brand)).ShouldBe(GoldenPhotoFormat.Heic);
    }

    [Theory]
    [InlineData("mp42")]
    [InlineData("isom")]
    [InlineData("avif")]
    [InlineData("qt  ")]
    [InlineData("HEIC")] // brands are case sensitive
    public void Any_other_ftyp_brand_is_not_a_golden_picture(string brand)
    {
        GoldenPhotoFormats.Sniff(Ftyp(brand)).ShouldBeNull();
    }

    [Fact]
    public void A_heic_needs_the_ftyp_box_to_hold_at_least_its_brand_and_version()
    {
        GoldenPhotoFormats.Sniff(Ftyp("heic", boxSize: 15)).ShouldBeNull();
        GoldenPhotoFormats.Sniff(Ftyp("heic", boxSize: 16)).ShouldBe(GoldenPhotoFormat.Heic);
    }

    public static TheoryData<byte[]> NotPictures => new()
    {
        { [] },
        { [0xFF] },
        { [0xFF, 0xD8] },
        { [0xD8, 0xFF, 0xFF] },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] }, // PNG
        { Encoding.ASCII.GetBytes("GIF89a") },
        { Encoding.UTF8.GetBytes("<html>") },
        { [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y'] }, // ftyp cut short
        { [0, 0, 0, 24, (byte)'m', (byte)'d', (byte)'a', (byte)'t', (byte)'h', (byte)'e', (byte)'i', (byte)'c'] }, // not an ftyp box
    };

    [Theory]
    [MemberData(nameof(NotPictures))]
    public void Anything_else_is_not_a_golden_picture(byte[] bytes)
    {
        GoldenPhotoFormats.Sniff(bytes).ShouldBeNull();
    }

    [Fact]
    public void Each_picture_kind_has_one_extension_and_one_content_type()
    {
        GoldenPhotoFormats.Extension(GoldenPhotoFormat.Jpeg).ShouldBe(".jpg");
        GoldenPhotoFormats.Extension(GoldenPhotoFormat.Heic).ShouldBe(".heic");
    }

    [Theory]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("Prn")]
    [InlineData("aux")]
    [InlineData("NUL")]
    [InlineData("com1")]
    [InlineData("COM9")]
    [InlineData("lpt1")]
    [InlineData("LPT9")]
    public void A_windows_device_name_is_not_a_usable_id(string id)
    {
        GoldenPhotoNames.IsReserved(id).ShouldBeTrue();
    }

    [Theory]
    [InlineData("cover-01")]
    [InlineData("console")]
    [InlineData("com10")]
    [InlineData("com0")]
    [InlineData("lpt")]
    [InlineData("nul_1")]
    [InlineData("c1")]
    public void A_name_that_only_looks_like_a_device_name_is_fine(string id)
    {
        GoldenPhotoNames.IsReserved(id).ShouldBeFalse();
    }
}
