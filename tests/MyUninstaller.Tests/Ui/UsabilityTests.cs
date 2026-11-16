using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyUninstaller.App.ViewModels;
using MyUninstaller.Core.Layout;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.Tests.Ui;

public class WindowFitTests
{
    // Laptop pengembang: layar 1366×768, area kerja di atas taskbar setinggi 720.
    private static readonly WindowBounds WorkArea = new(0, 0, 1366, 720);

    [Fact]
    public void Saved_main_window_taller_than_screen_is_shrunk_and_moved_up()
    {
        // Ukuran tersimpan nyata: Top 47, tinggi 760 → tepi bawah 807, bilah status tertutup taskbar.
        var fitted = WindowFit.Fit(new WindowBounds(94, 47, 1180, 760), WorkArea);

        fitted.Should().Be(new WindowBounds(94, 0, 1180, 720));
        fitted.Bottom.Should().BeLessThanOrEqualTo(WorkArea.Bottom);
    }

    [Fact]
    public void Dialog_that_fits_but_overflows_the_bottom_is_moved_up_only()
    {
        var fitted = WindowFit.Fit(new WindowBounds(300, 200, 900, 640), WorkArea);

        fitted.Should().Be(new WindowBounds(300, 80, 900, 640));
    }

    [Fact]
    public void Window_inside_work_area_is_untouched()
    {
        var window = new WindowBounds(100, 30, 900, 600);
        WindowFit.Fit(window, WorkArea).Should().Be(window);
    }

    [Fact]
    public void Window_left_of_screen_is_pulled_back()
    {
        WindowFit.Fit(new WindowBounds(-500, 10, 900, 600), WorkArea).Left.Should().Be(0);
        WindowFit.Fit(new WindowBounds(1000, 10, 900, 600), WorkArea).Right.Should().Be(1366);
    }
}

public class ProgramAssessmentTests
{
    private static ProgramItemViewModel Item(string name, string? publisher = null, bool systemComponent = false) =>
        new(new InstalledProgram
        {
            Id = name,
            DisplayName = name,
            Publisher = publisher,
            Source = ProgramSource.Registry64,
            IsSystemComponent = systemComponent,
        }, new DateOnly(2026, 11, 16));

    [Fact]
    public void Driver_is_marked_with_reason()
    {
        var item = Item("Windows Driver Package - ASUS (AsusPTPDrv) HIDClass  (04/21/2017 11.0.0.16)", "ASUS");

        item.Risk.Should().Be(ProgramRisk.Driver);
        item.IsRisky.Should().BeTrue();
        item.RiskBadgeText.Should().Be("DRIVER");
        item.AssessmentText.Should().StartWith("Driver perangkat keras.");
        item.ToolTipText.Should().Contain("AsusPTPDrv").And.Contain("Driver perangkat keras.");
    }

    [Fact]
    public void Shared_runtime_is_marked()
    {
        var item = Item("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Corporation");

        item.RiskBadgeText.Should().Be("RUNTIME");
        item.AssessmentText.Should().StartWith("Runtime bersama.");
    }

    [Fact]
    public void Ordinary_program_gets_neutral_assessment_without_badge()
    {
        var item = Item("Notepad++ (64-bit x64)", "Notepad++ Team");

        item.IsRisky.Should().BeFalse();
        item.RiskBadgeText.Should().BeNull();
        item.AssessmentText.Should().StartWith("Tidak dikenali sebagai driver");
        item.ToolTipText.Should().StartWith("Notepad++ (64-bit x64)" + Environment.NewLine);
    }
}

[Collection(WpfCollection.Name)]
public class ScrollBarTemplateTests
{
    [StaFact]
    public void Track_can_be_clicked_to_page_and_thumb_is_wide_enough()
    {
        WindowXamlTests.EnsureApplicationResources();
        var scroll = new ScrollViewer
        {
            Content = new Border { Height = 5000 },
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Height = 300,
            Width = 200,
        };
        var window = new Window { Content = scroll, Left = -20000, Top = -20000, ShowActivated = false, Width = 240, Height = 360 };
        window.Show();
        scroll.UpdateLayout();

        var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        var track = (Track)bar.Template.FindName("PART_Track", bar);

        track.DecreaseRepeatButton.Command.Should().Be(ScrollBar.PageUpCommand);
        track.IncreaseRepeatButton.Command.Should().Be(ScrollBar.PageDownCommand);
        bar.ActualWidth.Should().BeGreaterThanOrEqualTo(14);
        track.Thumb.ActualWidth.Should().BeGreaterThanOrEqualTo(8);

        // Klik jalur di bawah pegangan = turun satu halaman.
        track.IncreaseRepeatButton.Command.Execute(null);
        ScrollBar.PageDownCommand.Execute(null, bar);
        scroll.UpdateLayout();
        scroll.VerticalOffset.Should().BeGreaterThan(0);

        window.Close();
    }
}
