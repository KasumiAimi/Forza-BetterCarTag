namespace FH6GarageEditor;

internal readonly record struct PerformanceBand(double Maximum, int DisplayMaximum);

internal static class PerformanceRating
{
    internal static int Display(double? value, int classId, IReadOnlyDictionary<int, PerformanceBand> bands)
    {
        if (value is not double raw || !double.IsFinite(raw) || raw < 0) return 0;
        if (raw is >= 100 and <= 999 && raw == Math.Truncate(raw)) return (int)raw;
        if (!bands.TryGetValue(classId, out var upper)) return 0;
        PerformanceBand lower;
        if (classId == 0) lower = new(0, 100);
        else if (!bands.TryGetValue(classId - 1, out lower)) return 0;
        if (upper.Maximum <= lower.Maximum || upper.DisplayMaximum <= lower.DisplayMaximum) return 0;

        var fraction = Math.Clamp((raw - lower.Maximum) / (upper.Maximum - lower.Maximum), 0, 1);
        var pi = lower.DisplayMaximum + fraction * (upper.DisplayMaximum - lower.DisplayMaximum);
        var minimum = classId == 0 ? lower.DisplayMaximum : lower.DisplayMaximum + 1;
        return Math.Clamp((int)Math.Ceiling(pi - 1e-9), minimum, upper.DisplayMaximum);
    }
}
