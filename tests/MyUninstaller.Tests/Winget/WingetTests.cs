using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Winget;
using MyUninstaller.Tests.Ui;

namespace MyUninstaller.Tests.Winget;

public class WingetRulesTests
{
    // Format sama dengan keluaran `winget list --source winget` yang dialihkan (tanpa kolom Sumber).
    private const string English = """
        Name                          Id                        Version       Available
        ---------------------------------------------------------------------------------
        7-Zip 24.09 (x64)             7zip.7zip                 24.09         25.01
        Notepad++ (64-bit x64)        Notepad++.Notepad++       8.6.4
        Program dengan nama yang sa…  Contoh.NamaPanjang        1.0.0.0       1.0.1.0
        2 upgrades available.
        """;

    [Fact]
    public void Parses_columns_from_header_positions()
    {
        var rows = WingetRules.ParseList(English);

        rows.Should().Equal(
            new WingetPackage("7-Zip 24.09 (x64)", "7zip.7zip", "24.09", "25.01"),
            new WingetPackage("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.6.4", null),
            new WingetPackage("Program dengan nama yang sa", "Contoh.NamaPanjang", "1.0.0.0", "1.0.1.0"));
        rows.Count(r => r.HasUpdate).Should().Be(2);
    }

    [Fact]
    public void Localized_header_and_progress_residue()
    {
        string output = "  - \r  \\ \r" + """
            Nama               Id                 Versi
            -------------------------------------------
            VLC media player   VideoLAN.VLC       3.0.21
            """;

        WingetRules.ParseList(output).Should().Equal(new WingetPackage("VLC media player", "VideoLAN.VLC", "3.0.21", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tidak ada paket terpasang yang cocok dengan kriteria input.")]
    public void No_table_means_no_rows(string output) =>
        WingetRules.ParseList(output).Should().BeEmpty();

    [Theory]
    [InlineData("7zip.7zip", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("Microsoft.VisualStudioCode", true)]
    [InlineData("a&calc", false)]
    [InlineData("x\" & calc \"", false)]
    [InlineData("dua kata", false)]
    [InlineData("-flag", false)]
    [InlineData("a|b", false)]
    [InlineData("ARP\\Machine\\X64\\{GUID}", false)]
    [InlineData("", false)]
    public void Package_id_validation(string id, bool valid) =>
        WingetRules.IsValidId(id).Should().Be(valid);

    [Fact]
    public void Arguments_never_accept_agreements_on_users_behalf()
    {
        WingetRules.Arguments(WingetAction.Upgrade, "7zip.7zip").Should().Be("upgrade --id 7zip.7zip --exact --source winget");
        WingetRules.Arguments(WingetAction.Reinstall, "7zip.7zip").Should().Be("install --id 7zip.7zip --exact --source winget --force");
        WingetRules.Arguments(WingetAction.UpgradeAll, null).Should().Be("upgrade --all --source winget");

        foreach (WingetAction action in Enum.GetValues<WingetAction>())
        {
            WingetRules.Arguments(action, "a.b").Should().NotContain("accept");
        }

        var act = () => WingetRules.Arguments(WingetAction.Upgrade, "a&calc");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Last_message_skips_progress_bars() =>
        WingetRules.LastMessage("Galat pertama\r\n  ██████▒▒▒▒  \r\nSumber memerlukan persetujuan.\r\n---------\r\n")
            .Should().Be("Sumber memerlukan persetujuan.");
}

[Collection(WpfCollection.Name)]
public class WingetViewModelTests
{
    private sealed class FakeWinget(WingetListResult result, int? exitCode = 0) : IWingetClient
    {
        public List<(WingetAction Action, string? Id)> Runs { get; } = [];

        public int Lists { get; private set; }

        public Task<WingetListResult> ListAsync(CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult(result);
        }

        public Task<int?> RunInConsoleAsync(WingetAction action, string? packageId, CancellationToken cancellationToken)
        {
            Runs.Add((action, packageId));
            return Task.FromResult(exitCode);
        }
    }

    private static readonly WingetListResult Two = new(
    [
        new WingetPackage("VLC", "VideoLAN.VLC", "3.0.20", "3.0.21"),
        new WingetPackage("7-Zip", "7zip.7zip", "24.09", null),
    ], null);

    [StaFact]
    public void Lists_filters_and_upgrades_selected()
    {
        WindowXamlTests.EnsureApplicationResources();
        var winget = new FakeWinget(Two);
        using var vm = new WingetViewModel(winget, (_, _) => true, NullLogger.Instance);

        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        vm.SummaryText.Should().Be("2 program dikenal winget, 1 punya pembaruan");
        vm.View.Cast<WingetPackageItemViewModel>().Select(i => i.Id).Should().Equal("VideoLAN.VLC");
        vm.ShowOnlyUpdates = false;
        vm.View.Cast<WingetPackageItemViewModel>().Should().HaveCount(2);

        vm.Selected = vm.Items.Single(i => i.Id == "7zip.7zip");
        vm.UpgradeCommand.CanExecute(null).Should().BeFalse("tidak ada pembaruan");
        vm.ReinstallCommand.CanExecute(null).Should().BeTrue();

        vm.Selected = vm.Items.Single(i => i.Id == "VideoLAN.VLC");
        vm.Selected.VersionText.Should().Be("3.0.20 → 3.0.21");
        vm.UpgradeCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        winget.Runs.Should().Equal((WingetAction.Upgrade, "VideoLAN.VLC"));
        winget.Lists.Should().Be(2, "daftar disegarkan setelah konsol ditutup");
        vm.AnyChanged.Should().BeTrue();
        vm.NoticeText.Should().Be("winget selesai. Daftar sudah disegarkan.");

        var window = new WingetWindow(vm);
        window.Title.Should().Be("Pembaruan program");
        window.Close();
    }

    [StaFact]
    public void Declined_confirmation_runs_nothing()
    {
        var winget = new FakeWinget(Two);
        using var vm = new WingetViewModel(winget, (_, _) => false, NullLogger.Instance);
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        vm.UpgradeAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.Selected = vm.Items[0];
        vm.ReinstallCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        winget.Runs.Should().BeEmpty();
        vm.AnyChanged.Should().BeFalse();
    }

    [StaFact]
    public void Missing_winget_and_errors_are_explained()
    {
        using var missing = new WingetViewModel(new FakeWinget(new WingetListResult([], null, WingetMissing: true)), (_, _) => true, NullLogger.Instance);
        missing.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        missing.NoticeText.Should().StartWith("winget tidak ditemukan.");

        using var failed = new WingetViewModel(new FakeWinget(new WingetListResult([], "Sumber memerlukan persetujuan.")), (_, _) => true, NullLogger.Instance);
        failed.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        failed.NoticeText.Should().StartWith("winget gagal membaca daftar: Sumber memerlukan persetujuan.");
    }
}
