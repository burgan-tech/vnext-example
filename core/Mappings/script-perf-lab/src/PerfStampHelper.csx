using System;

namespace Perf.Helpers;

/// <summary>İkinci helper — helper-set'in çok üyeli (A7) yolunu tetiklemek için var.</summary>
public static class PerfStampHelper
{
    public static string Stage(int stage, string instanceId) =>
        "perf:" + stage + ":" + (instanceId ?? "none");
}
