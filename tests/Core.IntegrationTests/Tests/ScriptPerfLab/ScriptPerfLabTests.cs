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

        // 10 stage'in hepsi merge edilmiş ve helper stamp'i çözülmüş olmalı.
        for (var stage = 1; stage <= 10; stage++)
        {
            var node = attributes.GetProperty($"stage{stage}");
            Assert.StartsWith($"perf:{stage}:", node.GetProperty("stamp").GetString());

            // chunk = PerfChunkHelper.Build(stage, chunkKb) -> List<object> of ~1KB segments,
            // NOT a string. chunkKb=2 -> at least 2 segment nodes, each carrying a non-empty seg.
            var chunk = node.GetProperty("chunk");
            Assert.Equal(JsonValueKind.Array, chunk.ValueKind);
            Assert.True(chunk.GetArrayLength() >= 2,
                $"stage{stage} chunk beklenen segment sayısında değil (got {chunk.GetArrayLength()})");
            var firstSegment = chunk[0];
            Assert.False(string.IsNullOrEmpty(firstSegment.GetProperty("seg").GetString()),
                $"stage{stage} chunk'ın ilk segmenti 'seg' alanı boş");
        }

        // Fan-out default paketlemesi: resultKey satırları + Summary.
        var results = attributes.GetProperty("perfItemResults").EnumerateArray().ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, row => Assert.True(row.GetProperty("isSuccess").GetBoolean()));
        var summary = attributes.GetProperty("perfItemResultsSummary");
        Assert.Equal(3, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
    }
}
