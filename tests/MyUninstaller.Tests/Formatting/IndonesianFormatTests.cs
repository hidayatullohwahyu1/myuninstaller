using MyUninstaller.Core.Formatting;

namespace MyUninstaller.Tests.Formatting;

public class IndonesianFormatTests
{
    private const long KB = 1024;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;

    [Theory]
    [InlineData(0, "0")]
    [InlineData(142, "142")]
    [InlineData(1250, "1.250")]
    [InlineData(1234567, "1.234.567")]
    public void Count_uses_dot_as_thousands_separator(long value, string expected) =>
        IndonesianFormat.Count(value).Should().Be(expected);

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(9 * MB + MB / 2, "9,5 MB")]
    [InlineData(412 * MB, "412 MB")]
    [InlineData(1023 * MB + 1000 * KB, "1 GB")]
    public void Size_uses_comma_decimal(long bytes, string expected) =>
        IndonesianFormat.Size(bytes).Should().Be(expected);

    [Fact]
    public void Size_in_gigabytes_keeps_one_decimal()
    {
        long bytes = (long)(38.2 * GB);
        IndonesianFormat.Size(bytes).Should().Be("38,2 GB");
    }

    [Fact]
    public void Size_rejects_negative() =>
        FluentActions.Invoking(() => IndonesianFormat.Size(-1)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Date_uses_indonesian_month_names() =>
        IndonesianFormat.Date(new DateOnly(2026, 9, 2)).Should().Be("2 September 2026");

    [Theory]
    [InlineData(1, "Januari")]
    [InlineData(5, "Mei")]
    [InlineData(8, "Agustus")]
    [InlineData(12, "Desember")]
    public void Date_month_names(int month, string name) =>
        IndonesianFormat.Date(new DateOnly(2026, month, 14)).Should().Be($"14 {name} 2026");

    [Fact]
    public void Time_uses_dot_separator() =>
        IndonesianFormat.Time(new TimeOnly(14, 30)).Should().Be("14.30");

    [Theory]
    [InlineData("20260902", 2026, 9, 2)]
    [InlineData(" 20240115 ", 2024, 1, 15)]
    public void ParseRegistryDate_reads_yyyyMMdd(string value, int y, int m, int d) =>
        IndonesianFormat.ParseRegistryDate(value).Should().Be(new DateOnly(y, m, d));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-09-02")]
    [InlineData("20261340")]
    [InlineData("kemarin")]
    public void ParseRegistryDate_returns_null_for_bad_values(string? value) =>
        IndonesianFormat.ParseRegistryDate(value).Should().BeNull();
}
