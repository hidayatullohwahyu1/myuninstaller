using System.Windows;
using System.Windows.Controls;
using MyUninstaller.App.Localization;
using MyUninstaller.Core.Uninstall;

namespace MyUninstaller.App.Views;

public partial class UninstallModeDialog : ThemedWindow
{
    private readonly Dictionary<UninstallMode, RadioButton> _options;

    /// <param name="header">Judul, misalnya "Hapus instalan 7-Zip".</param>
    /// <param name="initialMode">Mode terakhir yang dipakai.</param>
    /// <param name="silentCapable">Jumlah program yang punya cara senyap terdokumentasi.</param>
    /// <param name="total">Jumlah program yang akan dihapus.</param>
    public UninstallModeDialog(string header, UninstallMode initialMode, int silentCapable = 0, int total = 1)
    {
        InitializeComponent();
        HeaderText.Text = header;

        // PRD uninstall massal: dijalankan senyap satu per satu. Satu program: bawaan tidak senyap
        // (uninstaller biasanya menawarkan pilihan seperti "simpan pengaturan").
        SilentCheck.Content = Strings.Format("Mode_Silent", silentCapable, total);
        SilentCheck.IsEnabled = silentCapable > 0;
        SilentCheck.IsChecked = silentCapable > 0 && total > 1;
        SilentHint.Text = silentCapable == 0 ? Strings.Get("Mode_SilentNone") : Strings.Get("Mode_SilentHint");

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

    /// <summary>Pengguna memilih mode senyap (berlaku untuk program yang mendukungnya).</summary>
    public bool SilentRequested { get; private set; }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = _options.First(o => o.Value.IsChecked == true).Key;
        SilentRequested = SilentCheck.IsChecked == true;
        DialogResult = true;
    }
}
