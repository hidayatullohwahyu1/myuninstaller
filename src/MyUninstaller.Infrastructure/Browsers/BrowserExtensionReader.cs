using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Browsers;

namespace MyUninstaller.Infrastructure.Browsers;

/// <summary>Folder data satu browser (User Data Chromium atau Profiles Firefox).</summary>
public sealed record BrowserRoot(BrowserKind Browser, string DataFolder);

/// <summary>
/// Membaca ekstensi Chrome, Edge, Brave (folder profil\Extensions + Preferences) dan Firefox (extensions.json).
/// Hanya baca; tidak pernah mengubah berkas profil.
/// </summary>
public sealed partial class BrowserExtensionReader(
    IReadOnlyList<BrowserRoot> roots,
    IFolderSizeCalculator sizes,
    ILogger<BrowserExtensionReader> logger) : IBrowserExtensionReader
{
    public BrowserExtensionReader(IFolderSizeCalculator sizes, ILogger<BrowserExtensionReader> logger)
        : this(DefaultRoots(), sizes, logger)
    {
    }

    public static IReadOnlyList<BrowserRoot> DefaultRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            new(BrowserKind.Chrome, Path.Combine(local, "Google", "Chrome", "User Data")),
            new(BrowserKind.Edge, Path.Combine(local, "Microsoft", "Edge", "User Data")),
            new(BrowserKind.Brave, Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            new(BrowserKind.Firefox, Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")),
        ];
    }

    public IReadOnlyList<BrowserExtension> GetAll()
    {
        var result = new List<BrowserExtension>();
        foreach (var root in roots.Where(r => Directory.Exists(r.DataFolder)))
        {
            try
            {
                if (root.Browser == BrowserKind.Firefox)
                {
                    ReadFirefox(root, result);
                }
                else
                {
                    ReadChromium(root, result);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Ekstensi {Browser} tidak bisa dibaca", root.Browser);
            }
        }

        return [.. result.OrderBy(e => e.Browser).ThenBy(e => e.ProfileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // ------------------------------------------------------------------ Chromium

    private void ReadChromium(BrowserRoot root, List<BrowserExtension> result)
    {
        string? localState = ReadText(Path.Combine(root.DataFolder, "Local State"));

        foreach (string profile in Directory.EnumerateDirectories(root.DataFolder))
        {
            string extensionsFolder = Path.Combine(profile, "Extensions");
            if (!Directory.Exists(extensionsFolder))
            {
                continue;
            }

            string profileFolder = Path.GetFileName(profile);
            string profileName = (localState is null ? null : ExtensionParsers.ParseChromiumProfileName(localState, profileFolder)) ?? profileFolder;
            string? securePreferences = ReadText(Path.Combine(profile, "Secure Preferences"));
            string? preferences = ReadText(Path.Combine(profile, "Preferences"));

            foreach (string extensionFolder in Directory.EnumerateDirectories(extensionsFolder))
            {
                string id = Path.GetFileName(extensionFolder);
                if (!ChromiumIdRegex().IsMatch(id))
                {
                    continue; // mis. folder "Temp"
                }

                try
                {
                    if (ReadChromiumExtension(root.Browser, profileFolder, profileName, id, extensionFolder, securePreferences, preferences) is { } extension)
                    {
                        result.Add(extension);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Ekstensi {Id} dilewati", id);
                }
            }
        }
    }

    private BrowserExtension? ReadChromiumExtension(
        BrowserKind browser, string profileFolder, string profileName, string id, string extensionFolder,
        string? securePreferences, string? preferences)
    {
        // Folder versi terbaru (beberapa versi bisa tertinggal setelah pembaruan).
        string? versionFolder = Directory.EnumerateDirectories(extensionFolder)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (versionFolder is null)
        {
            return null;
        }

        var manifest = ExtensionParsers.ParseChromiumManifest(
            File.ReadAllText(Path.Combine(versionFolder, "manifest.json")),
            locale => ReadText(Path.Combine(versionFolder, "_locales", locale, "messages.json")));
        if (manifest is null)
        {
            return null;
        }

        var state = (securePreferences is null ? null : ExtensionParsers.ParseChromiumState(securePreferences, id))
                    ?? (preferences is null ? null : ExtensionParsers.ParseChromiumState(preferences, id));
        if (state is { IsBuiltIn: true })
        {
            return null;
        }

        return new BrowserExtension
        {
            Browser = browser,
            ProfileFolder = profileFolder,
            ProfileName = profileName,
            Id = id,
            Name = manifest.Name,
            Version = manifest.Version,
            Description = manifest.Description,
            IsEnabled = state?.IsEnabled ?? true,
            Path = extensionFolder,
            SizeBytes = sizes.Measure(extensionFolder),
        };
    }

    // ------------------------------------------------------------------ Firefox

    private void ReadFirefox(BrowserRoot root, List<BrowserExtension> result)
    {
        foreach (string profile in Directory.EnumerateDirectories(root.DataFolder))
        {
            if (ReadText(Path.Combine(profile, "extensions.json")) is not { } json)
            {
                continue;
            }

            string profileFolder = Path.GetFileName(profile);
            foreach (var addon in ExtensionParsers.ParseFirefoxExtensions(json))
            {
                // Hanya berkas/folder di dalam folder profil ini yang dipakai (path dari JSON tidak tepercaya).
                string? path = addon.Path is { } p && IsInside(p, profile) && (File.Exists(p) || Directory.Exists(p)) ? p : null;
                if (path is null)
                {
                    continue;
                }

                result.Add(new BrowserExtension
                {
                    Browser = BrowserKind.Firefox,
                    ProfileFolder = profileFolder,
                    ProfileName = profileFolder,
                    Id = addon.Id,
                    Name = addon.Name,
                    Version = addon.Version,
                    Description = addon.Description,
                    IsEnabled = addon.IsEnabled,
                    Path = path,
                    SizeBytes = File.Exists(path) ? new FileInfo(path).Length : sizes.Measure(path),
                });
            }
        }
    }

    private static bool IsInside(string path, string folder)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Membaca berkas teks walau sedang dibuka browser (FileShare.ReadWrite).</summary>
    private static string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ChromiumIdRegex();
}
