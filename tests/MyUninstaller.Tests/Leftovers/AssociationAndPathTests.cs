using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Safety;
using MyUninstaller.Core.Scanning;
using MyUninstaller.Infrastructure.FileSystem;
using MyUninstaller.Infrastructure.Leftovers;
using MyUninstaller.Infrastructure.Safety;
using MyUninstaller.Tests.Safety;

namespace MyUninstaller.Tests.Leftovers;

public class PathVariableRulesTests
{
    [Theory]
    [InlineData(@"C:\A;D:\App\bin;C:\B", @"D:\App\bin", @"C:\A;C:\B")]
    [InlineData(@"C:\A;D:\App\bin\;C:\B;", @"d:\app\BIN", @"C:\A;C:\B;")]
    [InlineData(@"D:\App\bin;D:\App\bin", @"D:\App\bin", "")]
    [InlineData(@"%USERPROFILE%\x;D:\App\bin", @"D:\App\bin", @"%USERPROFILE%\x")]
    [InlineData(@"C:\A;;D:\App\bin", @"""D:\App\bin""", @"C:\A")]
    public void Removes_only_the_entry(string value, string entry, string expected) =>
        PathVariableRules.Without(value, entry).Should().Be(expected);

    [Fact]
    public void Missing_entry_returns_null() =>
        PathVariableRules.Without(@"C:\A;C:\B", @"D:\App\bin").Should().BeNull();

    [Fact]
    public void Environment_variables_are_expanded_for_matching()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        PathVariableRules.Without(@"%USERPROFILE%\App\bin;C:\B", Path.Combine(profile, "App", "bin")).Should().Be(@"C:\B");
    }
}

public class AssociationProtectionTests
{
    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\ContohEditor.Document", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\ContohEditor.Document", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\Applications\editor.exe", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.ced", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\txtfile", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\exefile", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\Applications", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Microsoft.PowerShellScript.1", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppXabc123", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\ContohEditor.Document\shell", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\*", true)]
    public void Class_entries(string key, bool isProtected) =>
        ProtectedRegistryKeys.IsProtected(key).Should().Be(isProtected);

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.ced\OpenWithProgids", "ContohEditor.Document", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\.ced\OpenWithProgids", "ContohEditor.Document", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.ced", "Content Type", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.ced\OpenWithProgids", "", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.ced\sub\OpenWithProgids", "X", true)]
    public void OpenWithProgids_values(string key, string value, bool isProtected) =>
        ProtectedRegistryKeys.IsProtectedValue(key, value).Should().Be(isProtected);

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Environment", true)]
    [InlineData(@"HKCU\Environment", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Environment", false)]
    public void Only_user_path_is_editable(string key, bool editable) =>
        ProtectedRegistryKeys.IsEditablePathVariable(key).Should().Be(editable);
}

/// <summary>SafeRemover membuang entri PATH lewat registri palsu (PATH sungguhan tidak pernah disentuh tes).</summary>
public sealed class PathEntryRemovalTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
    private const string Env = @"HKEY_CURRENT_USER\Environment";

    private sealed class PathRegistry(string value) : IRegistryBackupService
    {
        public string Value { get; private set; } = value;

        public bool Exported { get; private set; }

        public bool KeyExists(string keyPath) => true;

        public string? Export(string sessionId, string keyPath)
        {
            Exported = true;
            return @"C:\fake\env.reg";
        }

        public bool DeleteKey(string keyPath) => throw new InvalidOperationException();

        public bool Import(string regFile) => true;

        public bool ValueExists(string keyPath, string valueName) => true;

        public bool DeleteValue(string keyPath, string valueName) => throw new InvalidOperationException();

        public string? ReadRawString(string keyPath, string valueName) => Value;

        public bool WriteRawString(string keyPath, string valueName, string value)
        {
            Value = value;
            return true;
        }
    }

    private (SafeRemover Remover, SqliteSessionJournal Journal) Create(PathRegistry registry) =>
        Create(registry, new ProtectedPaths([@"C:\Windows"], []));

    private (SafeRemover Remover, SqliteSessionJournal Journal) Create(PathRegistry registry, ProtectedPaths protectedPaths)
    {
        var journal = new SqliteSessionJournal(Path.Combine(_work, "riwayat.db"), new FixedClock(DateTimeOffset.Now));
        return (new SafeRemover(protectedPaths, null!, registry, journal), journal);
    }

    [Fact]
    public void Entry_is_removed_after_backup_and_journaled()
    {
        var registry = new PathRegistry(@"C:\Tools;D:\App\bin;C:\Other");
        var (remover, journal) = Create(registry);
        string session = journal.StartSession("Normal", "x");

        remover.Remove(session, [new RemovalTarget(RemovalTargetKind.PathEntry, Env, @"D:\App\bin")])
            .Single().Status.Should().Be(RemovalStatus.Removed);

        registry.Value.Should().Be(@"C:\Tools;C:\Other");
        registry.Exported.Should().BeTrue();
        var action = journal.GetActions(session).Single();
        action.Kind.Should().Be(JournalActionKind.EditPathVariable);
        action.BackupPath.Should().Be(@"C:\fake\env.reg");
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", @"D:\App\bin")]
    [InlineData(Env, @"C:\Windows\System32")]
    [InlineData(Env, "")]
    public void System_path_and_protected_folders_are_refused(string key, string entry)
    {
        var registry = new PathRegistry(@"C:\Windows\System32;D:\App\bin");
        var (remover, journal) = Create(registry);

        remover.Remove(journal.StartSession("Normal", "x"), [new RemovalTarget(RemovalTargetKind.PathEntry, key, entry)])
            .Single().Status.Should().Be(RemovalStatus.Protected);
        registry.Value.Should().Be(@"C:\Windows\System32;D:\App\bin");
    }

    [Fact]
    public void Absent_entry_is_missing_and_nothing_changes()
    {
        var registry = new PathRegistry(@"C:\Tools");
        var (remover, journal) = Create(registry);

        remover.Remove(journal.StartSession("Normal", "x"), [new RemovalTarget(RemovalTargetKind.PathEntry, Env, @"D:\App\bin")])
            .Single().Status.Should().Be(RemovalStatus.Missing);
        registry.Exported.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }
}

/// <summary>Pemindai: asosiasi berkas dan PATH pada kunci HKCU uji (dibuat dan dihapus tes ini).</summary>
[Trait("Category", "Integration")]
public sealed class AssociationAndPathScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyUninstallerTests", Guid.NewGuid().ToString("N"));
    private readonly string _reg = @"Software\MyUninstallerTests\" + Guid.NewGuid().ToString("N");

    private void Key(string sub, string? value = null, string? data = null, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_reg + @"\" + sub)!;
        if (data is not null)
        {
            key.SetValue(value ?? string.Empty, data, kind);
        }
    }

    [Fact]
    public async Task Finds_associations_and_path_entries_of_the_program_only()
    {
        string install = Path.Combine(_root, "Program Files", "Contoh Editor");
        string other = Path.Combine(_root, "Program Files", "Lain");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        Directory.CreateDirectory(other);
        string exe = Path.Combine(install, "editor.exe");

        Key(@"Classes\ContohEditor.Document\shell\open\command", data: $"\"{exe}\" \"%1\"");
        Key(@"Classes\ContohEditor.Icon\DefaultIcon", data: exe + ",1");
        Key(@"Classes\Applications\editor.exe\shell\open\command", data: $"\"{exe}\" \"%1\"");
        Key(@"Classes\.ced", data: "ContohEditor.Document");
        Key(@"Classes\.ced\OpenWithProgids", "ContohEditor.Document", string.Empty);
        Key(@"Classes\.ced\OpenWithProgids", "Lain.Document", string.Empty);
        Key(@"Classes\txtfile\shell\open\command", data: $"\"{exe}\" \"%1\""); // nama kelas inti: tidak pernah diambil
        Key(@"Classes\Lain.Document\shell\open\command", data: $"\"{Path.Combine(other, "lain.exe")}\" \"%1\"");
        Key("Environment", "Path", $@"C:\Tools;{Path.Combine(install, "bin")};{other}", RegistryValueKind.ExpandString);

        var roots = new LeftoverScanRoots
        {
            FolderRoots = [],
            UserDataRoots = [],
            ShortcutRoots = [],
            SoftwareRoots = [],
            RunKeys = [],
            AppPathsKeys = [],
            ClsidKeys = [],
            DriveRoots = [],
            ClassesKeys = [new RegistryRoot(RegistryHive.CurrentUser, _reg + @"\Classes")],
            EnvironmentKey = new RegistryRoot(RegistryHive.CurrentUser, _reg + @"\Environment"),
        };
        var scanner = new LeftoverScanner(roots, new ProtectedPaths([], []), new InstallLocationRules([]), new FolderSizeCalculator(),
            NullLogger<LeftoverScanner>.Instance);

        var program = new InstalledProgram { Id = "a", DisplayName = "Contoh Editor", Source = ProgramSource.Registry64, InstallLocation = install };
        var lain = new InstalledProgram { Id = "b", DisplayName = "Lain", Source = ProgramSource.Registry64, InstallLocation = other };

        var found = await scanner.ScanAsync(program, [lain], deep: false);
        string classes = @"HKEY_CURRENT_USER\" + _reg + @"\Classes";

        found.Should().ContainSingle(f => f.Kind == LeftoverKind.Folder && f.Location == install, "folder instalasi tetap ditemukan");
        found.Where(f => f.Kind != LeftoverKind.Folder).Select(f => (f.Kind, f.Location, f.ValueName)).Should().BeEquivalentTo(new (LeftoverKind, string, string?)[]
        {
            (LeftoverKind.RegistryKey, classes + @"\ContohEditor.Document", null),
            (LeftoverKind.RegistryKey, classes + @"\ContohEditor.Icon", null),
            (LeftoverKind.RegistryKey, classes + @"\Applications\editor.exe", null),
            (LeftoverKind.RegistryValue, classes + @"\.ced\OpenWithProgids", "ContohEditor.Document"),
            (LeftoverKind.PathEntry, @"HKEY_CURRENT_USER\" + _reg + @"\Environment", Path.Combine(install, "bin")),
        });
        found.Should().OnlyContain(f => f.Group == LeftoverGroup.Safe);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_reg, throwOnMissingSubKey: false);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
