using MyUninstaller.Core.Leftovers;
using MyUninstaller.Core.Models;
using MyUninstaller.Core.Portable;

namespace MyUninstaller.Core.Monitoring;

/// <summary>
/// Pemindai sisa yang memakai jejak instalasi (versi 2.0): benda yang tercatat dibuat installer program ini dan
/// masih ada ditambahkan ke hasil sebagai "Aman". Aturan "jangan ambil milik program lain" tetap berlaku:
/// benda di dalam folder program lain yang masih terpasang, atau kunci Uninstall program lain, dibuang.
/// Daftar lindung dicek lagi oleh SafeRemover tepat sebelum menghapus.
/// </summary>
public sealed class TraceAwareLeftoverScanner(ILeftoverScanner inner, IInstallTraceStore store, ITraceItemProbe probe) : ILeftoverScanner
{
    public async Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> stillInstalled,
        bool deep,
        CancellationToken cancellationToken = default)
    {
        var found = await inner.ScanAsync(program, stillInstalled, deep, cancellationToken);
        var traces = store.GetAll().Where(t => InstallTraceRules.Matches(t, program)).ToList();
        if (traces.Count == 0)
        {
            return found;
        }

        var others = stillInstalled.Where(p => p.Id != program.Id).ToList();
        var otherFolders = PortableRules.OwnedFolders(others);
        var otherKeys = new HashSet<string>(others.Select(p => p.RegistryKeyPath?.TrimEnd('\\')).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        // Nama penerbit/program lain: folder atau kunci induk penerbit yang masih dipakai tidak pernah diambil.
        var otherNames = new HashSet<string>(
            others.SelectMany(p => new[] { NameMatcher.NormalizePublisher(p.Publisher), NameMatcher.Normalize(p.DisplayName) })
                .Where(n => n.Length >= 3),
            StringComparer.Ordinal);

        var result = found.ToDictionary(i => i.Key);
        foreach (var item in traces.SelectMany(t => t.Items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BelongsToOtherProgram(item) || !probe.Exists(item))
            {
                continue;
            }

            int score = ConfidenceScorer.Score(LeftoverEvidence.RecordedAtInstall, item.Kind);
            var leftover = new LeftoverItem
            {
                Kind = item.Kind,
                Location = item.Location,
                ValueName = item.ValueName,
                Evidence = LeftoverEvidence.RecordedAtInstall,
                Score = score,
                ProgramName = program.DisplayName,
            };

            if (result.TryGetValue(leftover.Key, out var existing))
            {
                // Pemindai biasa sudah menemukannya: naikkan keyakinannya bila jejak lebih kuat.
                if (existing.Score < score)
                {
                    result[leftover.Key] = existing with { Evidence = LeftoverEvidence.RecordedAtInstall, Score = score };
                }
            }
            else
            {
                result[leftover.Key] = leftover;
            }
        }

        return [.. result.Values];

        bool BelongsToOtherProgram(TraceItem item)
        {
            string location = item.Location.TrimEnd('\\');
            return item.Kind switch
            {
                // Sama, di dalam, atau MEMUAT folder program lain (folder penerbit bersama).
                LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut =>
                    PortableRules.IsOwned(location, otherFolders) || otherNames.Contains(NameMatcher.Normalize(Path.GetFileName(location))),
                LeftoverKind.RegistryKey =>
                    otherKeys.Contains(location)
                    || otherKeys.Any(k => k.StartsWith(location + @"\", StringComparison.OrdinalIgnoreCase))
                    || otherNames.Contains(NameMatcher.Normalize(Path.GetFileName(location))),
                LeftoverKind.RegistryValue => otherKeys.Contains(location),
                _ => false,
            };
        }
    }
}
