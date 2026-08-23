using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ScriptPerfLab;

/// <summary>
/// script-perf-lab akışının uçtan uca doğruluğunu pinler: 10 stage'in chunk merge'leri,
/// helper çözümü (stamp), fan-out sonuç seti ve Completed'a ulaşma. PERF ASSERT ETMEZ —
/// sayılar api-tests/script-perf-lab/perf-load.py + README'nin işidir.
/// <para>
/// <c>chunk</c> düğüm-zengin bir dizi (~1KB'lık <c>{i, stage, seg}</c> segment nesneleri),
/// tek bir büyük string DEĞİL — helper'ın (<c>PerfChunkHelper</c>) B9 per-node maliyet profili
/// (NormalizedJson / per-object SerializeToElement) tetiklemek için kasıtlı şekli.
/// </para>
/// </summary>
public class ScriptPerfLabTests : WorkflowTestBase
{
    private const string Workflow = "script-perf-lab";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(180);

    public ScriptPerfLabTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task TenStages_WithHelpersAndFanOut_ReachDoneWithFullDataset()
    {
        var items = Enumerable.Range(0, 3).Select(i => new { id = $"DOC-{i:000}" }).ToArray();
        var instanceId = await StartAsync(Workflow, new { testId = $"it-{Guid.NewGuid():N}", chunkKb = 2, fanoutItems = items });

        await WaitForInstanceStateAsync(Workflow, instanceId, "perf-done", timeout: Budget);

        var attributes = await GetAttributesAsync(Workflow, instanceId);

        // 10 stage'in hepsi merge edilmiş ve helper stamp'i çözülmüş olmalı. GetProperty'nin
        // KeyNotFoundException'ı anahtar adını İÇERMEZ — canlı stack'e karşı ilk koşulan test bu
        // olduğu için her anahtar TryGetProperty + isimli mesajla okunur (FanOutDocumentsTests deseni).
        for (var stage = 1; stage <= 10; stage++)
        {
            Assert.True(attributes.TryGetProperty($"stage{stage}", out var node),
                $"stage{stage} attributes'ta yok — mevcut üst-seviye anahtarlar: " +
                string.Join(", ", attributes.EnumerateObject().Select(p => p.Name)));
            Assert.True(node.TryGetProperty("stamp", out var stamp), $"stage{stage}.stamp yok");
            Assert.StartsWith($"perf:{stage}:", stamp.GetString());

            // chunk = PerfChunkHelper.Build(stage, chunkKb) -> List<object> of ~1KB segments,
            // NOT a string. chunkKb=2 -> at least 2 segment nodes, each carrying a non-empty seg.
            Assert.True(node.TryGetProperty("chunk", out var chunk), $"stage{stage}.chunk yok");
            Assert.Equal(JsonValueKind.Array, chunk.ValueKind);
            Assert.True(chunk.GetArrayLength() >= 2,
                $"stage{stage} chunk beklenen segment sayısında değil (got {chunk.GetArrayLength()})");
            var firstSegment = chunk[0];
            Assert.False(string.IsNullOrEmpty(firstSegment.GetProperty("seg").GetString()),
                $"stage{stage} chunk'ın ilk segmenti 'seg' alanı boş");
        }

        // Fan-out default paketlemesi: resultKey satırları + Summary.
        Assert.True(attributes.TryGetProperty("perfItemResults", out var resultsNode),
            "perfItemResults attributes'ta yok (fan-out default paketlemesi çalışmadı?)");
        var results = resultsNode.EnumerateArray().ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, row => Assert.True(row.GetProperty("isSuccess").GetBoolean()));
        // Kimlik/sıra: ordered=true + itemKey=id türetimi — FanOutDocumentsTests ile aynı titizlik.
        Assert.Equal(items.Select(d => d.id).ToArray(),
            results.Select(row => row.GetProperty("itemKey").GetString()).ToArray());
        Assert.True(attributes.TryGetProperty("perfItemResultsSummary", out var summary),
            "perfItemResultsSummary attributes'ta yok");
        Assert.Equal(3, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        Assert.False(summary.GetProperty("timedOut").GetBoolean(), "fan-out batch timedOut=true");
    }
}
