using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using MyUninstaller.App.Services;
using MyUninstaller.Core.Shell;

namespace MyUninstaller.Tests.Shell;

public class ShellRequestTests
{
    [Theory]
    [InlineData(new[] { "--hapus", @"C:\Program Files\App\app.exe" }, @"C:\Program Files\App\app.exe")]
    [InlineData(new[] { "--HAPUS", @"C:\Users\u\Desktop\App.lnk" }, @"C:\Users\u\Desktop\App.lnk")]
    [InlineData(new[] { "--hapus", "\"C:\\A\\b.exe\"" }, @"C:\A\b.exe")]
    [InlineData(new[] { "--hapus", @"C:\A\..\B\b.exe" }, @"C:\B\b.exe")]
    public void Valid_arguments(string[] args, string expected) =>
        ShellRequest.ParseArguments(args).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("--hapus")]
    [InlineData(@"--lain|C:\A\b.exe")]
    [InlineData("--hapus|b.exe")]
    [InlineData(@"--hapus|C:\A\dokumen.txt")]
    [InlineData(@"--hapus|C:\A\skrip.bat")]
    [InlineData(@"--hapus|\\?\C:\A\b.exe")]
    [InlineData("--hapus|C:\\A\\b\n.exe")]
    public void Invalid_arguments_are_ignored(string joined) =>
        ShellRequest.ParseArguments(joined.Length == 0 ? [] : joined.Split('|')).Should().BeNull();

    [Fact]
    public void Overlong_path_is_rejected() =>
        ShellRequest.Validate(@"C:\" + new string('a', ShellRequest.MaxPathLength) + ".exe").Should().BeNull();
}

public class InstanceChannelTests
{
    private static PipeSecurity CurrentUser()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    [Fact]
    public async Task Second_instance_forwards_validated_path()
    {
        string pipe = "MyUninstallerTests." + Guid.NewGuid().ToString("N");
        var received = new List<string>();
        var got = new TaskCompletionSource();
        using var channel = new InstanceChannel(pipe, CurrentUser, NullLogger.Instance);
        _ = channel.ListenAsync(path =>
        {
            lock (received)
            {
                received.Add(path);
                if (path.EndsWith(".lnk", StringComparison.Ordinal))
                {
                    got.TrySetResult();
                }
            }
        });

        // Batas tunggu longgar: saat semua tes berjalan bersamaan, pendengar bisa terlambat dijadwalkan.
        InstanceChannel.Send(pipe, @"C:\A\bukan-exe.txt", timeoutMs: 20_000).Should().BeTrue();
        InstanceChannel.Send(pipe, @"C:\A\App.lnk", timeoutMs: 20_000).Should().BeTrue();
        await got.Task.WaitAsync(TimeSpan.FromSeconds(10));

        received.Should().Equal(@"C:\A\App.lnk");
    }

    [Fact]
    public void Send_without_listener_fails_quickly() =>
        InstanceChannel.Send("MyUninstallerTests." + Guid.NewGuid().ToString("N"), @"C:\A\b.exe", timeoutMs: 200).Should().BeFalse();
}
