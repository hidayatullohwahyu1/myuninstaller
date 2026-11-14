namespace MyUninstaller.Core.Leftovers;

/// <summary>
/// Skor keyakinan 0–100 (PRD): Aman 80–100 (dicentang), Mungkin 50–79 (tidak dicentang),
/// Ragu &lt; 50 (disembunyikan, butuh konfirmasi kedua).
/// </summary>
public static class ConfidenceScorer
{
    public const int SafeThreshold = 80;
    public const int MaybeThreshold = 50;

    /// <summary>Skor dasar per bukti, lalu disesuaikan menurut jenis temuan.</summary>
    public static int Score(LeftoverEvidence evidence, LeftoverKind kind)
    {
        int score = evidence switch
        {
            LeftoverEvidence.InstallLocation => 95,
            LeftoverEvidence.UninstallKey => 95,
            LeftoverEvidence.PointsIntoInstallLocation => 90,
            LeftoverEvidence.ExactNameUnderPublisher => 88,
            LeftoverEvidence.ExactName => 82,
            LeftoverEvidence.ExactNameElsewhere => 65,
            LeftoverEvidence.PublisherOnly => 60,
            LeftoverEvidence.UserData => 40,
            _ => 35,
        };

        // Layanan dan tugas berdampak lebih luas daripada berkas data: sedikit lebih hati-hati.
        if (kind is LeftoverKind.Service or LeftoverKind.ScheduledTask && score > 85)
        {
            score = 85;
        }

        return Math.Clamp(score, 0, 100);
    }

    public static LeftoverGroup GroupOf(int score) => score switch
    {
        >= SafeThreshold => LeftoverGroup.Safe,
        >= MaybeThreshold => LeftoverGroup.Maybe,
        _ => LeftoverGroup.Doubtful,
    };

    /// <summary>Dicentang bawaan hanya kelompok Aman.</summary>
    public static bool IsCheckedByDefault(int score) => GroupOf(score) == LeftoverGroup.Safe;
}
