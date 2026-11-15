using System.Windows;
using MyUninstaller.App.Localization;

namespace MyUninstaller.App.Services;

/// <summary>Dialog pesan, dibungkus agar ViewModel tidak bergantung langsung pada MessageBox.</summary>
public interface IDialogService
{
    void ShowInfo(string title, string message);

    void ShowWarning(string title, string message);

    void ShowError(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public void ShowInfo(string title, string message) => Show(title, message, MessageBoxImage.Information);

    public void ShowWarning(string title, string message) => Show(title, message, MessageBoxImage.Warning);

    public void ShowError(string title, string message) => Show(title, message, MessageBoxImage.Error);

    private static void Show(string title, string message, MessageBoxImage image)
    {
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
        }
    }
}
