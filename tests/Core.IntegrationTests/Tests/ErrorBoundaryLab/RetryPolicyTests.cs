using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// The <c>retry</c> boundary action: the runtime re-invokes the failing task itself, and what
/// happens once the attempts run out.
/// </summary>
/// <remarks>
/// <para>
/// Attempt counts are read from instance data, not from MockLab's request log. The HTTP mapping's
/// output handler runs on every attempt — failed ones included — and increments
/// <c>httpAttempts</c>, so the instance itself records how many times the task ran. That keeps the
/// assertion independent of who else has called the same mock endpoint.
/// </para>
/// <para>
/// The recovery case needs a stateful mock (500, 500, 200) whose cursor is global to MockLab, so it
/// rewinds the sequence first and skips when MockLab is not running. The exhaustion case needs only
/// the always-500 endpoint.
/// </para>
/// <para>
/// <b>MEASURED:</b> <c>retryCount</c> on the incident is always 0, even for a case that retried
/// twice. The field is populated from the boundary action's retry policy, which the engine never
/// attaches to the action result. The attempt count is observable only through instance data, which
/// is why these tests read it there.
/// </para>
/// </remarks>
public class RetryPolicyTests : ErrorBoundaryLabTestBase
{
    private const string FlakyRoute = "api/eb-lab/flaky";

    public RetryPolicyTests(VNextTestEnvironment environment) : base(environment) { }

    [SkippableFact]
    public async Task ARetryPolicy_ReInvokesTheTaskUntilItSucceeds()
    {
        using var mocklab = new MockLabAdminClient();
        Skip.If(!await mocklab.IsUpAsync(),
            $"MockLab is not reachable at {mocklab.BaseUrl} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL).");
        Skip.If(!await mocklab.ResetSequenceAsync(FlakyRoute),
            $"MockLab has no mock for '{FlakyRoute}' — the error-boundary-lab seed is not loaded. " +
            "Seeds import only when the collection is new: `docker compose down -v && docker compose up -d`.");

        var instanceId = await RunCaseAsync(Workflow, "case-retry-recover");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("landed", state);
        Assert.NotEqual("F", status);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(3, attributes.GetProperty("httpAttempts").GetInt32());
        Assert.Equal(200, attributes.GetProperty("httpLastStatus").GetInt32());

        // A retry that recovers is not a failure the client ever hears about: no history row, and the
        // block advertises no active link.
        Assert.Empty(await GetIncidentItemsAsync(Workflow, instanceId));

        var metadata = await GetIncidentMetadataAsync(Workflow, instanceId);
        Assert.NotNull(metadata);
        Assert.False(Flag(metadata!.Value, "hasActiveIncident"));
        AssertAbsent(metadata.Value, "active", "the retry recovered, so nothing is open");
    }

    [SkippableFact]
    public async Task WhenTheRetriesRunOut_TheNextMatchingRuleDecides()
    {
        using var mocklab = new MockLabAdminClient();
        Skip.If(!await mocklab.IsUpAsync(),
            $"MockLab is not reachable at {mocklab.BaseUrl} — start it with `docker compose up -d`.");

        // maxRetries 2 against an endpoint that never recovers: three invocations, then the chain is
        // searched again with retry rules excluded and the abort fallback applies.
        var instanceId = await RunCaseAsync(Workflow, "case-retry-exhaust");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(3, attributes.GetProperty("httpAttempts").GetInt32());
        Assert.Equal(500, attributes.GetProperty("httpLastStatus").GetInt32());

        var incident = BoundaryIncident(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("Abort", Text(incident, "boundaryAction"));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));

        // MEASURED: the retry count never reaches the incident.
        Assert.Equal(0, Number(incident, "retryCount"));
    }
}
