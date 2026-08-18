using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ScriptRaceLab;

/// <summary>
/// script-race-lab: a fixture for the script-assembly double-compile race.
/// <para>
/// The parent declares <c>scripts.helpers</c>, so its subflow output mapping compiles into the
/// helper set's shared, singleton-lifetime <c>AssemblyLoadContext</c>
/// (<c>SubflowOutputMappingService</c> compiles with <c>parentWorkflow.Scripts</c>). Parent and
/// child are fully automatic, so N parallel starts land N completions — N compilations of the
/// same mapping under the same assembly name — inside one emit window.
/// </para>
/// <para>
/// On a pre-fix runtime the losers throw <c>FileLoadException</c>, the mapping fails, and the
/// parent is faulted permanently. On the fixed runtime every parent completes.
/// </para>
/// </summary>
public class ScriptRaceLabTests : WorkflowTestBase
{
    private const string Parent = "script-race-lab-parent";

    /// <summary>
    /// Starts that must overlap for the race to be possible. The knob to turn if a run does not
    /// trigger it — the other is the filler bulk in RaceOutputMapping.csx.
    /// </summary>
    private const int ParallelStarts = 30;

    public ScriptRaceLabTests(VNextTestEnvironment environment) : base(environment) { }

    private Task<string> StartOneAsync(string tag) =>
        StartAsync(Parent, new { testId = $"{tag}-{Guid.NewGuid():N}"[..24] });

    [Fact]
    public async Task Smoke_SingleInstance_CompletesAndCarriesTheHelperStamp()
    {
        var parentId = await StartOneAsync("smoke");

        await WaitForInstanceStateAsync(Parent, parentId, "race-done", timeout: TimeSpan.FromSeconds(90));

        var (state, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("race-done", state);
        Assert.Equal("C", status);

        var attributes = await GetAttributesAsync(Parent, parentId);
        Assert.True(attributes.TryGetProperty("raceStamp", out var stamp),
            $"the output mapping did not run or did not reach the helper — {await DescribeAsync(Parent, parentId)}");
        Assert.StartsWith("race:", stamp.GetString());
    }

    [Fact]
    public async Task ParallelStarts_AllComplete_WithoutAnAssemblyLoadFault()
    {
        // The starts must OVERLAP: the race window is one Roslyn emit of the output mapping, and
        // only completions that arrive while the cache entry is still cold can collide.
        var ids = await Task.WhenAll(
            Enumerable.Range(0, ParallelStarts).Select(index => StartOneAsync($"race{index:D2}")));

        // Settle on a terminal status — never on "no longer Busy". A parent holding an open SubFlow
        // correlation is Busy by design for the child's whole lifetime, so Busy carries no
        // information about progress; a terminal status is the only reliable settle signal.
        await Task.WhenAll(ids.Select(async id =>
            await WaitUntilAsync(
                async () => TerminalStatuses.Contains((await GetInstanceStateAsync(Parent, id)).Status),
                $"{Parent}/{id} never settled",
                TimeSpan.FromSeconds(180))));

        var faulted = new List<string>();
        foreach (var id in ids)
        {
            var (_, status) = await GetInstanceStateAsync(Parent, id);
            if (status == "F") faulted.Add(await DescribeAsync(Parent, id));
        }

        Assert.True(faulted.Count == 0,
            $"{faulted.Count}/{ParallelStarts} parents faulted. On a pre-fix runtime this is the " +
            "reproduction — expect Instance:100030 with an inner FileLoadException naming " +
            "'Script_…' and 'Assembly with same name is already loaded'. Faulted instances:" +
            // Fully qualified: the SDK test base exposes a protected `Environment` property that
            // shadows System.Environment here.
            System.Environment.NewLine + string.Join(System.Environment.NewLine, faulted));

        // Every survivor must also have actually run the mapping — an all-C run where the mapping
        // silently did nothing would prove nothing.
        foreach (var id in ids)
        {
            var attributes = await GetAttributesAsync(Parent, id);
            Assert.True(attributes.TryGetProperty("raceStamp", out _),
                $"instance completed without the output mapping's stamp — {await DescribeAsync(Parent, id)}");
        }
    }
}
