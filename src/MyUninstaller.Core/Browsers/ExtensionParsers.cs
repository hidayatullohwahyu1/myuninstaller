using System.Text.Json;

namespace MyUninstaller.Core.Browsers;

/// <summary>Isi manifest.json ekstensi Chromium yang dipakai di daftar.</summary>
public sealed record ChromiumManifest(string Name, string? Version, string? Description);

/// <summary>Status ekstensi di berkas Preferences / Secure Preferences profil Chromium.</summary>
/// <param name="IsEnabled">Aktif (tidak ada disable_reasons, dan state bukan 0).</param>
/// <param name="IsBuiltIn">Bawaan browser (location komponen/eksternal-komponen): tidak ditampilkan.</param>
public sealed record ChromiumExtensionState(bool IsEnabled, bool IsBuiltIn);

/// <summary>Pengurai berkas Chromium (Chrome, Edge, Brave) dan Firefox. Logika murni, hanya membaca teks JSON.</summary>
public static class ExtensionParsers
{
    // Nilai "location" Chromium: 5 = COMPONENT, 10 = EXTERNAL_COMPONENT (bawaan browser).
    private static readonly HashSet<int> BuiltInLocations = [5, 10];

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Membaca manifest.json. Nama "__MSG_kunci__" diterjemahkan lewat <paramref name="readMessages"/>
    /// (isi _locales\&lt;default_locale&gt;\messages.json). Null bila manifest rusak.
    /// </summary>
    public static ChromiumManifest? ParseChromiumManifest(string json, Func<string, string?> readMessages)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            var root = doc.RootElement;
            string? locale = GetString(root, "default_locale");
            string? messages = locale is null ? null : readMessages(locale);

            string? name = Localize(GetString(root, "name"), messages);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return new ChromiumManifest(name.Trim(), GetString(root, "version"), Localize(GetString(root, "description"), messages));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"__MSG_appName__" → isi messages.json["appName"].message (tanpa membedakan huruf besar/kecil).</summary>
    public static string? Localize(string? value, string? messagesJson)
    {
        if (value is null || !value.StartsWith("__MSG_", StringComparison.Ordinal) || !value.EndsWith("__", StringComparison.Ordinal))
        {
            return value;
        }

        if (messagesJson is null)
        {
            return null;
        }

        string key = value[6..^2];
        try
        {
            using var doc = JsonDocument.Parse(messagesJson, Options);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object
                    && GetString(property.Value, "message") is { } message)
                {
                    return message;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Status ekstensi dari isi Preferences/Secure Preferences (bagian extensions.settings.&lt;id&gt;).
    /// Null bila tidak tercatat di berkas itu.
    /// </summary>
    public static ChromiumExtensionState? ParseChromiumState(string preferencesJson, string extensionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(preferencesJson, Options);
            if (!doc.RootElement.TryGetProperty("extensions", out var extensions)
                || !extensions.TryGetProperty("settings", out var settings)
                || !settings.TryGetProperty(extensionId, out var entry)
                || entry.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool disabledByReasons = entry.TryGetProperty("disable_reasons", out var reasons) && reasons.ValueKind switch
            {
                JsonValueKind.Number => reasons.TryGetInt32(out int bits) && bits != 0,
                JsonValueKind.Array => reasons.GetArrayLength() > 0,
                _ => false,
            };
            bool disabledByState = entry.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Number && state.GetInt32() == 0;
            bool builtIn = entry.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Number
                           && BuiltInLocations.Contains(location.GetInt32());

            return new ChromiumExtensionState(!disabledByReasons && !disabledByState, builtIn);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Nama profil dari "Local State" (profile.info_cache.&lt;folder&gt;.name); null bila tidak ada.</summary>
    public static string? ParseChromiumProfileName(string localStateJson, string profileFolder)
    {
        try
        {
            using var doc = JsonDocument.Parse(localStateJson, Options);
            return doc.RootElement.TryGetProperty("profile", out var profile)
                   && profile.TryGetProperty("info_cache", out var cache)
                   && cache.TryGetProperty(profileFolder, out var info)
                ? GetString(info, "name")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Satu add-on Firefox dari extensions.json.</summary>
    public sealed record FirefoxAddon(string Id, string Name, string? Version, string? Description, bool IsEnabled, string? Path);

    /// <summary>
    /// Add-on buatan pengguna dari extensions.json Firefox (type "extension", location "app-profile").
    /// Add-on sistem/bawaan dan tema tidak dikembalikan.
    /// </summary>
    public static IReadOnlyList<FirefoxAddon> ParseFirefoxExtensions(string extensionsJson)
    {
        var result = new List<FirefoxAddon>();
        try
        {
            using var doc = JsonDocument.Parse(extensionsJson, Options);
            if (!doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var addon in addons.EnumerateArray())
            {
                if (GetString(addon, "type") != "extension" || GetString(addon, "location") != "app-profile"
                    || GetString(addon, "id") is not { } id)
                {
                    continue;
                }

                string? name = addon.TryGetProperty("defaultLocale", out var locale) ? GetString(locale, "name") : null;
                string? description = addon.TryGetProperty("defaultLocale", out var loc2) ? GetString(loc2, "description") : null;
                bool active = addon.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                bool userDisabled = addon.TryGetProperty("userDisabled", out var u) && u.ValueKind == JsonValueKind.True;

                result.Add(new FirefoxAddon(id, name ?? id, GetString(addon, "version"), description, active && !userDisabled, GetString(addon, "path")));
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
