using System;
using System.Linq;

namespace Perf.Helpers;

/// <summary>
/// script-perf-lab: deterministik, istenen boyutta chunk üretir. Amaç instance dokümanını
/// stage başına parametrik büyütmek (B9 append profili). StringBuilder bilinçli yok —
/// script derlemesinde System.Text using'i mevcut değil.
/// </summary>
public static class PerfChunkHelper
{
    public static string Build(int stage, int kb)
    {
        var unit = "s" + stage + "-0123456789abcdefghijklmnopqrstuvwxyz-";
        var repeat = (kb * 1024) / unit.Length + 1;
        return string.Concat(Enumerable.Repeat(unit, repeat)).Substring(0, Math.Max(1, kb * 1024));
    }
}
