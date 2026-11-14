using MyUninstaller.Core.Listing;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Settings;

namespace MyUninstaller.Tests.Listing;

public class ProgramListRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static InstalledProgram P(string name, string? publisher = null, long? size = null, DateOnly? date = null, DateOnly? lastUsed = null) => new()
    {
        Id = name,
        DisplayName = name,
        Publisher = publisher,
        SizeBytes = size,
        InstallDate = date,
        LastUsed = lastUsed,
        Source = ProgramSource.Registry64,
    };

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("chrome", true)]
    [InlineData("GOOGLE", true)]
    [InlineData("google chrome", true)]
    [InlineData("chrome llc", true)]
    [InlineData("firefox", false)]
    [InlineData("chrome firefox", false)]
    public void Search_matches_all_words_in_name_or_publisher(string? query, bool expected) =>
        ProgramSearch.Matches(P("Google Chrome", "Google LLC"), query).Should().Be(expected);

    [Fact]
    public void Search_handles_missing_publisher() =>
        ProgramSearch.Matches(P("7-Zip"), "igor").Should().BeFalse();

    [Fact]
    public void Sort_by_name_is_case_insensitive()
    {
        var list = new[] { P("zoom"), P("Audacity"), P("7-Zip"), P("chrome") };
        list.Order(new ProgramComparer(ProgramSortKey.Name, false))
            .Select(p => p.DisplayName).Should().Equal("7-Zip", "Audacity", "chrome", "zoom");
    }

    [Fact]
    public void Sort_by_size_descending_puts_unknown_last()
    {
        var list = new[] { P("A", size: 10), P("B"), P("C", size: 300), P("D", size: 10) };
        list.Order(new ProgramComparer(ProgramSortKey.Size, true))
            .Select(p => p.DisplayName).Should().Equal("C", "A", "D", "B");
    }

    [Fact]
    public void Sort_by_size_ascending_still_puts_unknown_last()
    {
        var list = new[] { P("B"), P("C", size: 300), P("A", size: 10) };
        list.Order(new ProgramComparer(ProgramSortKey.Size, false))
            .Select(p => p.DisplayName).Should().Equal("A", "C", "B");
    }

    [Fact]
    public void Sort_by_publisher_treats_blank_as_unknown()
    {
        var list = new[] { P("X", "  "), P("Y", "Mozilla"), P("Z", "Adobe") };
        list.Order(new ProgramComparer(ProgramSortKey.Publisher, false))
            .Select(p => p.DisplayName).Should().Equal("Z", "Y", "X");
    }

    [Fact]
    public void Sort_by_install_date_newest_first()
    {
        var list = new[] { P("Lama", date: Today.AddDays(-100)), P("Baru", date: Today), P("Tanpa") };
        list.Order(new ProgramComparer(ProgramSortKey.InstallDate, true))
            .Select(p => p.DisplayName).Should().Equal("Baru", "Lama", "Tanpa");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(30, false)]
    public void IsNew_within_seven_days(int daysAgo, bool expected) =>
        P("X", date: Today.AddDays(-daysAgo)).IsNew(Today).Should().Be(expected);

    [Fact]
    public void IsNew_false_for_future_or_unknown_date()
    {
        P("X", date: Today.AddDays(3)).IsNew(Today).Should().BeFalse();
        P("X").IsNew(Today).Should().BeFalse();
    }

    [Theory]
    [InlineData(89, false)]
    [InlineData(90, true)]
    [InlineData(400, true)]
    public void IsRarelyUsed_after_ninety_days(int daysAgo, bool expected) =>
        P("X", lastUsed: Today.AddDays(-daysAgo)).IsRarelyUsed(Today).Should().Be(expected);

    [Fact]
    public void IsRarelyUsed_false_when_unknown() =>
        P("X").IsRarelyUsed(Today).Should().BeFalse();
}
