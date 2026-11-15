using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.Services;
using MyUninstaller.App.ViewModels;
using MyUninstaller.App.Views;
using MyUninstaller.Core.Browsers;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Uninstall;
using MyUninstaller.Infrastructure.Browsers;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Processes;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Tests.Safety;
using MyUninstaller.Tests.Ui;

namespace MyUninstaller.Tests.Browsers;

public class ExtensionParserTests
{
    [Fact]
    public void Manifest_name_is_localized_case_insensitively()
    {
        const string manifest = """{ "name": "__MSG_appName__", "description": "__MSG_appDesc__", "version": "1.2.3", "default_locale": "en" }""";
        const string messages = """{ "AppName": { "message": "Pengunduh Video" }, "appDesc": { "message": "Unduh video" } }""";

        var result = ExtensionParsers.ParseChromiumManifest(manifest, locale => locale == "en" ? messages : null);

        result.Should().Be(new ChromiumManifest("Pengunduh Video", "1.2.3", "Unduh video"));
    }

    [Theory]
    [InlineData("""{ "name": "__MSG_missing__", "default_locale": "en" }""")]
    [InlineData("""{ "version": "1" }""")]
    [InlineData("{ rusak")]
    public void Unusable_manifest_returns_null(string manifest) =>
        ExtensionParsers.ParseChromiumManifest(manifest, _ => "{}").Should().BeNull();

    [Theory]
    [InlineData("""{"extensions":{"settings":{"ID":{"state":1,"location":1}}}}""", true, false)]
    [InlineData("""{"extensions":{"settings":{"ID":{"disable_reasons":1,"location":1}}}}""", false, false)]
    [InlineData("""{"extensions":{"settings":{"ID":{"disable_reasons":[1],"location":1}}}}""", false, false)]
    [InlineData("""{"extensions":{"settings":{"ID":{"disable_reasons":[],"location":1}}}}""", true, false)]
    [InlineData("""{"extensions":{"settings":{"ID":{"state":0}}}}""", false, false)]
    [InlineData("""{"extensions":{"settings":{"ID":{"location":5}}}}""", true, true)]
    [InlineData("""{"extensions":{"settings":{"ID":{"location":10}}}}""", true, true)]
    public void Chromium_state(string preferences, bool enabled, bool builtIn) =>
        ExtensionParsers.ParseChromiumState(preferences, "ID").Should().Be(new ChromiumExtensionState(enabled, builtIn));

    [Fact]
    public void Chromium_state_missing_entry_is_null() =>
        ExtensionParsers.ParseChromiumState("""{"extensions":{"settings":{}}}""", "ID").Should().BeNull();

    [Fact]
    public void Profile_name_from_local_state() =>
        ExtensionParsers.ParseChromiumProfileName("""{"profile":{"info_cache":{"Profile 3":{"name":"Kerja"}}}}""", "Profile 3").Should().Be("Kerja");

    [Fact]
    public void Firefox_keeps_only_user_extensions()
    {
        const string json = """
            { "addons": [
              { "id": "a@x", "type": "extension", "location": "app-profile", "version": "2.0", "active": true, "userDisabled": false,
                "defaultLocale": { "name": "Pemblokir Iklan", "description": "Blokir" }, "path": "C:\\p\\extensions\\a@x.xpi" },
              { "id": "b@x", "type": "extension", "location": "app-profile", "active": false, "userDisabled": true,
                "defaultLocale": { "name": "Nonaktif" } },
              { "id": "sys@mozilla.org", "type": "extension", "location": "app-system-defaults", "defaultLocale": { "name": "Sistem" } },
              { "id": "tema@x", "type": "theme", "location": "app-profile", "defaultLocale": { "name": "Tema" } }
            ] }
            """;

        var addons = ExtensionParsers.ParseFirefoxExtensions(json);

        addons.Select(a => (a.Id, a.Name, a.IsEnabled)).Should().Equal(("a@x", "Pemblokir Iklan", true), ("b@x", "Nonaktif", false));
        addons[0].Path.Should().Be(@"C:\p\extensions\a@x.xpi");
    }
}

/// <summary>Pembaca ekstensi pada profil Chrome/Firefox tiruan di folder sementara.</summary>
[Trait("Category", "Integration")]
public sealed class BrowserExtensionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private const string UserId = "aaaabbbbccccddddeeeeffffgggghhhh";
    private const string DisabledId = "ppppoooonnnnmmmmllllkkkkjjjjiiii";
    private const string BuiltInId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Reads_chromium_and_firefox_profiles()
    {
        string chrome = Path.Combine(_root, "Chrome", "User Data");
        string profile = Path.Combine(chrome, "Profile 3");
        Write(Path.Combine(chrome, "Local State"), """{"profile":{"info_cache":{"Profile 3":{"name":"Kerja"}}}}""");
        Write(Path.Combine(profile, "Extensions", UserId, "1.0_0", "manifest.json"), """{ "name": "__MSG_n__", "version": "1.0", "default_locale": "id" }""");
        Write(Path.Combine(profile, "Extensions", UserId, "1.0_0", "_locales", "id", "messages.json"), """{ "n": { "message": "Penerjemah" } }""");
        Write(Path.Combine(profile, "Extensions", DisabledId, "2.0_0", "manifest.json"), """{ "name": "Lama", "version": "2.0" }""");
        Write(Path.Combine(profile, "Extensions", BuiltInId, "1.0_0", "manifest.json"), """{ "name": "Bawaan" }""");
        Write(Path.Combine(profile, "Extensions", "Temp", "x", "manifest.json"), """{ "name": "Sementara" }""");
        Write(Path.Combine(profile, "Secure Preferences"), """{"extensions":{"settings":{""" + $"\"{DisabledId}\"" + """:{"disable_reasons":1},""" + $"\"{BuiltInId}\"" + """:{"location":5}}}}""");

        string firefox = Path.Combine(_root, "Firefox", "Profiles");
        string ffProfile = Path.Combine(firefox, "abcd.default-release");
        string xpi = Path.Combine(ffProfile, "extensions", "blok@contoh.xpi");
        Write(xpi, "xpi");
        string outside = Path.Combine(_root, "di-luar.xpi");
        Write(outside, "x");
        Write(Path.Combine(ffProfile, "extensions.json"), $$"""
            { "addons": [
              { "id": "blok@contoh", "type": "extension", "location": "app-profile", "active": true, "defaultLocale": { "name": "Pemblokir" },
                "path": {{System.Text.Json.JsonSerializer.Serialize(xpi)}} },
              { "id": "luar@contoh", "type": "extension", "location": "app-profile", "active": true, "defaultLocale": { "name": "Luar" },
                "path": {{System.Text.Json.JsonSerializer.Serialize(outside)}} }
            ] }
            """);

        var reader = new BrowserExtensionReader(
            [new BrowserRoot(BrowserKind.Chrome, chrome), new BrowserRoot(BrowserKind.Firefox, firefox)],
            new FolderSizeCalculator(), NullLogger<BrowserExtensionReader>.Instance);

        var all = reader.GetAll();

        all.Select(e => (e.Browser, e.ProfileName, e.Name, e.IsEnabled)).Should().Equal(
            (BrowserKind.Chrome, "Kerja", "Lama", false),
            (BrowserKind.Chrome, "Kerja", "Penerjemah", true),
            (BrowserKind.Firefox, "abcd.default-release", "Pemblokir", true));
        all.Single(e => e.Name == "Pemblokir").Path.Should().Be(xpi, "path di luar folder profil diabaikan");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

[Collection(WpfCollection.Name)]
public sealed class BrowserExtensionServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));

    private sealed class FakeLauncher(string? browserPath, bool launchWorks = true) : IUnelevatedLauncher
    {
        public List<(string File, string Arguments)> Launched { get; } = [];

        public bool Launch(string file, string arguments)
        {
            Launched.Add((file, arguments));
            return launchWorks;
        }

        public string? FindBrowser(BrowserKind browser) => browserPath;
    }

    private sealed class Processes(params string[] names) : IProcessInspector
    {
        public IReadOnlyList<RunningProcess> GetRunningProcesses() => [.. names.Select((n, i) => new RunningProcess(i + 1, n, null))];

        public bool TryKill(RunningProcess process) => false;
    }

    private sealed class Shell : IShellService
    {
        public string? Copied { get; private set; }

        public bool OpenFolder(string path) => true;

        public bool OpenUrl(string url) => true;

        public void CopyText(string text) => Copied = text;

        public bool OpenRegistryEditor(string keyPath) => true;
    }

    private (BrowserExtensionService Service, Shell Shell, SqliteSessionJournal Journal) Create(FakeLauncher launcher, Processes processes)
    {
        var protectedPaths = new ProtectedPaths([], []);
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        var quarantine = new QuarantineService(Path.Combine(_work, "Karantina"), protectedPaths, NullLogger<QuarantineService>.Instance);
        var registry = new RegistryBackupService(Path.Combine(_work, "Karantina"), NullLogger<RegistryBackupService>.Instance);
        var shell = new Shell();
        var service = new BrowserExtensionService(launcher, processes, new SafeRemover(protectedPaths, quarantine, registry, journal), journal, shell,
            NullLogger<BrowserExtensionService>.Instance);
        return (service, shell, journal);
    }

    private static BrowserExtension Ext(BrowserKind browser, string path = @"C:\x") => new()
    {
        Browser = browser,
        ProfileFolder = "Profile 3",
        ProfileName = "Kerja",
        Id = "aaaabbbbccccddddeeeeffffgggghhhh",
        Name = "Penerjemah",
        IsEnabled = true,
        Path = path,
    };

    [Theory]
    [InlineData(BrowserKind.Chrome, "chrome://extensions/?id=")]
    [InlineData(BrowserKind.Edge, "edge://extensions/?id=")]
    [InlineData(BrowserKind.Brave, "brave://extensions/?id=")]
    public void Chromium_opens_extension_page_in_right_profile(BrowserKind browser, string url)
    {
        var launcher = new FakeLauncher(@"C:\Browser\browser.exe");
        var (service, _, _) = Create(launcher, new Processes());

        var result = service.Remove(Ext(browser), (_, _) => throw new InvalidOperationException("tidak boleh ditanya"));

        result.Removed.Should().BeFalse();
        launcher.Launched.Should().ContainSingle().Which.Should().Be(
            (@"C:\Browser\browser.exe", $"--profile-directory=\"Profile 3\" {url}aaaabbbbccccddddeeeeffffgggghhhh"));
        result.Message.Should().Contain("Klik \"Hapus\" di sana");
    }

    [Fact]
    public void Missing_browser_copies_id()
    {
        var (service, shell, _) = Create(new FakeLauncher(null), new Processes());

        var result = service.Remove(Ext(BrowserKind.Chrome), (_, _) => true);

        shell.Copied.Should().Be("aaaabbbbccccddddeeeeffffgggghhhh");
        result.Message.Should().StartWith("Google Chrome tidak ditemukan");
    }

    [Fact]
    public void Firefox_must_be_closed_first()
    {
        string xpi = Path.Combine(_work, "profil", "extensions", "x.xpi");
        Directory.CreateDirectory(Path.GetDirectoryName(xpi)!);
        File.WriteAllText(xpi, "xpi");
        var (service, _, _) = Create(new FakeLauncher(null), new Processes("firefox"));

        var result = service.Remove(Ext(BrowserKind.Firefox, xpi), (_, _) => true);

        result.Message.Should().Be("Firefox sedang berjalan. Tutup Firefox dulu, lalu coba lagi.");
        File.Exists(xpi).Should().BeTrue();
    }

    [Fact]
    public void Firefox_extension_goes_to_quarantine_and_is_journaled()
    {
        string xpi = Path.Combine(_work, "profil", "extensions", "x.xpi");
        Directory.CreateDirectory(Path.GetDirectoryName(xpi)!);
        File.WriteAllText(xpi, "xpi");
        var (service, _, journal) = Create(new FakeLauncher(null), new Processes("explorer"));

        service.Remove(Ext(BrowserKind.Firefox, xpi), (_, _) => false).Removed.Should().BeFalse();
        File.Exists(xpi).Should().BeTrue("pengguna membatalkan");

        var result = service.Remove(Ext(BrowserKind.Firefox, xpi), (_, _) => true);

        result.Removed.Should().BeTrue();
        result.Message.Should().Be("\"Penerjemah\" dipindah ke karantina.");
        File.Exists(xpi).Should().BeFalse();
        var session = journal.GetSessions().Single();
        session.Mode.Should().Be("Extension");
        journal.GetActions(session.Id).Single().Kind.Should().Be(JournalActionKind.QuarantinePath);
    }

    [StaFact]
    public void Window_and_view_model_load()
    {
        WindowXamlTests.EnsureApplicationResources();
        var reader = new FixedReader(Ext(BrowserKind.Chrome), Ext(BrowserKind.Firefox) with { ProfileFolder = "ff", ProfileName = "ff", Id = "a@x" });
        var (service, shell, _) = Create(new FakeLauncher(null), new Processes());

        var vm = new BrowserExtensionsViewModel(reader, service, shell, (_, _) => false);
        vm.SummaryText.Should().Be("2 ekstensi di 2 profil");
        vm.Items[0].GroupText.Should().Be("Google Chrome · profil Kerja");

        vm.Selected = vm.Items[0];
        vm.CopyIdCommand.Execute(null);
        shell.Copied.Should().Be("aaaabbbbccccddddeeeeffffgggghhhh");

        var window = new BrowserExtensionsWindow(vm);
        window.Title.Should().Be("Ekstensi browser");
        window.Close();
    }

    private sealed class FixedReader(params BrowserExtension[] items) : IBrowserExtensionReader
    {
        public IReadOnlyList<BrowserExtension> GetAll() => items;
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }
}
