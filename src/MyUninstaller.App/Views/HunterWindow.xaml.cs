using System.Windows;
using System.Windows.Input;
using MyUninstaller.App.Localization;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Models;

namespace MyUninstaller.App.Views;

/// <summary>
/// Jendela kecil selalu di atas. Pengguna menyeret ikon target ke jendela/pintasan/ikon baki; saat dilepas,
/// program di bawah kursor dikenali.
/// </summary>
public partial class HunterWindow : ThemedWindow
{
    private readonly HunterProbe _probe;
    private readonly Func<IReadOnlyList<InstalledProgram>> _programs;
    private readonly Action<InstalledProgram> _select;
    private readonly Action<InstalledProgram> _uninstall;
    private InstalledProgram? _found;
    private bool _dragging;

    public HunterWindow(
        HunterProbe probe,
        Func<IReadOnlyList<InstalledProgram>> programs,
        Action<InstalledProgram> select,
        Action<InstalledProgram> uninstall)
    {
        _probe = probe;
        _programs = programs;
        _select = select;
        _uninstall = uninstall;
        InitializeComponent();

        // Pojok kanan bawah area kerja, supaya tidak menutupi jendela program yang diburu.
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - 24;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 24;
        };
    }

    /// <summary>Menampilkan hasil dari berkas yang dijatuhkan ke jendela utama.</summary>
    public void ShowResult(HunterResult result) => Show(result);

    private void Target_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = Target.CaptureMouse();
        if (_dragging)
        {
            TargetGlyph.Opacity = 0.25; // "ikon" sedang dibawa kursor
            Mouse.OverrideCursor = Cursors.Cross;
        }
    }

    private void Target_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        EndDrag();

        // Lepas di atas jendela ini sendiri = batal.
        Point inside = e.GetPosition(this);
        if (inside.X >= 0 && inside.Y >= 0 && inside.X <= ActualWidth && inside.Y <= ActualHeight)
        {
            return;
        }

        Show(_probe.IdentifyAtCursor(_programs()));
    }

    private void Target_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        Target.ReleaseMouseCapture();
        TargetGlyph.Opacity = 1;
        Mouse.OverrideCursor = null;
    }

    private void Show(HunterResult? result)
    {
        ResultPanel.Visibility = Visibility.Visible;
        _found = result?.Program;

        if (_found is not null)
        {
            ResultTitle.Text = Strings.Format("Hunter_Found", _found.DisplayName);
            ResultDetail.Text = Strings.Format("Hunter_Source", result!.ExecutablePath ?? result.Description);
            ResultActions.Visibility = Visibility.Visible;
        }
        else
        {
            ResultTitle.Text = Strings.Get("Hunter_NotFound");
            ResultDetail.Text = result is null ? string.Empty : Strings.Format("Hunter_Source", result.ExecutablePath ?? result.Description);
            ResultActions.Visibility = Visibility.Collapsed;
        }

        Activate();
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (_found is not null)
        {
            _select(_found);
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_found is not null)
        {
            var program = _found;
            Close();
            _uninstall(program);
        }
    }
}
