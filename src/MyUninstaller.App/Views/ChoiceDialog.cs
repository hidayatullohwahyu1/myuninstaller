using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyUninstaller.App.Views;

/// <summary>Jenis tombol dialog.</summary>
public enum ChoiceKind
{
    Secondary,

    /// <summary>Aksi aman yang disarankan; boleh menjadi tombol Enter.</summary>
    Primary,

    /// <summary>Aksi berbahaya: merah dan TIDAK PERNAH tombol Enter.</summary>
    Danger,
}

/// <summary>Satu tombol dialog.</summary>
public sealed record ChoiceButton(string Label, ChoiceKind Kind = ChoiceKind.Secondary, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// Dialog pesan bertema dengan tombol berlabel bahasa Indonesia (MessageBox memakai label bahasa Windows).
/// Dibuat di kode supaya mudah dipakai ulang tanpa berkas XAML.
/// </summary>
public sealed class ChoiceDialog : ThemedWindow
{
    private int _result = -1;

    private ChoiceDialog(string title, string message, IReadOnlyList<string>? details, IReadOnlyList<ChoiceButton> buttons, string glyph)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20), LastChildFill = true };

        // Tombol di bawah, rata kanan.
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        for (int i = 0; i < buttons.Count; i++)
        {
            var spec = buttons[i];
            int index = i;
            var button = new Button
            {
                Content = spec.Label,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 96,
                IsDefault = spec.IsDefault && spec.Kind != ChoiceKind.Danger,
                IsCancel = spec.IsCancel,
            };
            button.SetResourceReference(StyleProperty, spec.Kind switch
            {
                ChoiceKind.Primary => "PrimaryButton",
                ChoiceKind.Danger => "DangerTextButton",
                _ => "SecondaryButton",
            });
            button.Click += (_, _) =>
            {
                _result = index;
                DialogResult = true;
            };
            buttonPanel.Children.Add(button);
        }

        root.Children.Add(buttonPanel);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var icon = new TextBlock { Text = glyph, FontSize = 22, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(StyleProperty, "GlyphText");
        icon.SetResourceReference(ForegroundProperty, "AccentTextBrush");
        icon.FontSize = 22;
        header.Children.Add(icon);
        var titleText = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        titleText.SetResourceReference(TextBlock.FontFamilyProperty, "UiDisplayFont");
        header.Children.Add(titleText);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });

        if (details is { Count: > 0 })
        {
            var list = new TextBlock
            {
                Text = string.Join(Environment.NewLine, details),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                LineHeight = 18,
            };
            list.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var box = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            };
            box.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            body.Children.Add(box);
        }

        root.Children.Add(body);
        Content = root;
    }

    /// <summary>Menampilkan dialog; mengembalikan indeks tombol yang diklik, atau -1 bila ditutup.</summary>
    public static int Show(
        Window? owner,
        string title,
        string message,
        IReadOnlyList<ChoiceButton> buttons,
        IReadOnlyList<string>? details = null,
        string glyph = "")
    {
        var dialog = new ChoiceDialog(title, message, details, buttons, glyph);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog._result;
    }
}
