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
}
