using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ParallelVariableKeyLab;

/// <summary>
/// parallel-variable-key-lab, trigger family (vnext#1161): every same-domain trigger task type —
/// GetInstances, GetInstance, GetInstanceData, Start, SubProcess, DirectTrigger — at one order, against one
/// target flow. Same-order tasks run as parallel branches of one pipeline and a same-domain trigger task is
/// served in-process; a branch reading the target's schema shared the pipeline's DbContext with every other
/// branch reading it, and the second query failed with "A second operation was started on this context
/// instance". The siblings were then cancelled, the failed group stopped the hook (later orders never ran)
/// and, with no error boundary, the instance carried on anyway. The flows define no boundary on purpose, so
/// the recorder at the last order (<c>family</c>) — or, when it never ran, the task journal — is where a
/// failure shows.
/// </summary>
public class TriggerFamilyParallelTests : WorkflowTestBase
{
    private const string Family = "pvk-trigger-family";
    private const string Mix = "pvk-trigger-mix";
    private const string Target = "pvk-tf-target";

    /// <summary>
    /// The failure is a race, so one clean run proves little: several parents fan out at once and every
    /// one of them must record every slot as succeeded.
    /// </summary>
    private const int Runs = 8;

    public TriggerFamilyParallelTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The field report's shape first (four GetInstances of one flow at order 1), then all six types at
    /// order 2, then the recorder: fourteen slots, all succeeded.
    /// </summary>
    [Fact]
    public async Task FourListsThenAllSixTypes_EveryTaskSucceeds()
    {
        var runs = await Task.WhenAll(Enumerable.Range(0, Runs).Select(_ => RunAsync(Family, mixOnly: false)));

        AssertNoFailures(runs);
        foreach (var run in runs)
        {
            // Order 1: each of the four lists saw exactly this run's three seeds.
            foreach (var list in new[] { "list1", "list2", "list3", "list4" })
                Assert.Equal(3, run.Slots.GetProperty(list).GetProperty("count").GetInt32());

            await AssertTargetsAsync(run);
        }
    }

    /// <summary>
    /// The six-type group alone at order 1, so it runs even on a runtime whose four-list group fails
    /// first — each type's own outcome is observable on its own.
    /// </summary>
    [Fact]
    public async Task AllSixSameDomainTriggerTypesAtOneOrder_EveryTaskSucceeds()
    {
        var runs = await Task.WhenAll(Enumerable.Range(0, Runs).Select(_ => RunAsync(Mix, mixOnly: true)));

        AssertNoFailures(runs);
        foreach (var run in runs)
            await AssertTargetsAsync(run);
    }

    private static void AssertNoFailures(FamilyRun[] runs)
    {
        var failures = runs.Where(r => r.Failed.Count > 0)
            .Select(r => $"{r.ParentId}: {string.Join(", ", r.Failed)}")
            .ToList();
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{runs.Length} runs had failed trigger tasks:\n{string.Join("\n", failures)}");
    }

    /// <summary>DirectTrigger by key moved both trigger seeds; the read seed was only read.</summary>
    private async Task AssertTargetsAsync(FamilyRun run)
    {
        foreach (var key in run.TriggerKeys)
            Assert.Equal("touched", (await GetInstanceStateAsync(Target, key)).State);

        // GET accepts the key as the identifier.
        Assert.Equal("idle", (await GetInstanceStateAsync(Target, run.ReadKey)).State);
    }

    private async Task<FamilyRun> RunAsync(string flow, bool mixOnly)
    {
        var testId = $"pvk-tf-{Guid.NewGuid():N}";
        var readKey = $"{testId}-read";
        var triggerKeys = new[] { $"{testId}-trig-a", $"{testId}-trig-b" };

        foreach (var key in triggerKeys.Prepend(readKey))
            await StartAsync(Target, new { key, attributes = new { testId } });

        var parentId = await StartAsync(flow, new
        {
            attributes = new
            {
                testId, readKey, triggerKeyA = triggerKeys[0], triggerKeyB = triggerKeys[1], mixOnly
            }
        });

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(flow, parentId)).State == "checked",
            $"{flow} never reached 'checked' — {await DescribeAsync(flow, parentId)}",
            TimeSpan.FromSeconds(90));

        await AssertNotFaultedAsync(flow, parentId);

        // A parallel group with a failed task stops the hook: the later orders — the recorder included —
        // never run, and the instance still moves on (no boundary). That is the field report's silent
        // outcome; the task journal is then the only witness of what failed.
        if (!(await GetAttributesAsync(flow, parentId)).TryGetProperty("family", out var family))
            return new FamilyRun(parentId, readKey, triggerKeys,
                ["recorder never ran (a parallel group failed and stopped the hook) — journal: "
                 + await DescribeJournalAsync(flow, parentId)],
                default);

        var failed = family.GetProperty("failed").EnumerateArray().Select(e => e.GetString()!).ToList();
        return new FamilyRun(parentId, readKey, triggerKeys, failed, family.GetProperty("slots"));
    }

    /// <summary>The <c>functions/tasks</c> journal as <c>taskKey@order=businessStatus</c>, metadata only.</summary>
    private async Task<string> DescribeJournalAsync(string flow, string parentId)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{flow}/instances/{parentId}/functions/tasks", headers: Headers());
        if (status != HttpStatusCode.OK) return $"<functions/tasks answered {(int)status}>";

        var rows = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(r => $"{r.GetProperty("taskKey").GetString()}@{r.GetProperty("order").GetInt32()}="
                         + (r.TryGetProperty("businessStatus", out var b) ? b.GetString() : r.GetProperty("status").GetString()));
        return string.Join(", ", rows);
    }

    private sealed record FamilyRun(
        string ParentId, string ReadKey, string[] TriggerKeys, List<string> Failed, JsonElement Slots);
}
