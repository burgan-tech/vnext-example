using System;
using System.Collections.Generic;
using System.Linq;

namespace Perf.Helpers;

/// <summary>
/// script-perf-lab: deterministik, istenen boyutta chunk üretir. Amaç instance dokümanını
/// stage başına parametrik büyütmek (B9 append profili). StringBuilder bilinçli yok —
/// script derlemesinde System.Text using'i mevcut değil.
/// <para>
/// Kasıtlı olarak DÜĞÜM-ZENGİN bir şekil döner (kb adet ~1KB node'luk liste), tek bir büyük
/// string DEĞİL: tek string doküman genişliğini büyütür ama JSON düğüm sayısını ~21'de sabit
/// tutardı, bu da B9'un asıl ölçmek istediği per-node maliyetini (NormalizedJson / per-object
/// SerializeToElement) hiç tetiklemezdi.
/// </para>
/// </summary>
public static class PerfChunkHelper
{
    public static List<object> Build(int stage, int kb)
    {
        var unit = "s" + stage + "-0123456789abcdefghijklmnopqrstuvwxyz-";
        var segment = string.Concat(Enumerable.Repeat(unit, 1024 / unit.Length + 1)).Substring(0, 1024);
        var segments = new List<object>();
        for (var i = 0; i < Math.Max(1, kb); i++)
        {
            segments.Add(new { i, stage, seg = segment });
        }
        return segments;
    }
}
