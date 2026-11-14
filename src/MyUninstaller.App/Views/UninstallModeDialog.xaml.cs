using System.Windows;
using System.Windows.Controls;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.App.Views;

public partial class UninstallModeDialog : ThemedWindow
{
    private readonly Dictionary<UninstallMode, RadioButton> _options;

    public UninstallModeDialog(string header, UninstallMode initialMode)
    {
        InitializeComponent();
        HeaderText.Text = header;

        _options = new()
        {
            [UninstallMode.Builtin] = BuiltinOption,
            [UninstallMode.Safe] = SafeOption,
            [UninstallMode.Normal] = NormalOption,
            [UninstallMode.Super] = SuperOption,
            [UninstallMode.Force] = ForceOption,
        };

        // Hapus paksa tidak pernah diingat sebagai pilihan bawaan: harus dipilih sadar setiap kali.
        RadioButton initial = _options[initialMode == UninstallMode.Force ? UninstallMode.Normal : initialMode];
        initial.IsChecked = true;
        Loaded += (_, _) => initial.Focus();
    }

    public UninstallMode SelectedMode { get; private set; }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = _options.First(o => o.Value.IsChecked == true).Key;
        DialogResult = true;
    }
}
