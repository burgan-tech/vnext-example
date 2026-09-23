using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.TaskInvocationLab;

/// <summary>
/// The third claim this scenario exists for (vnext issue #1007): the persisted
/// <c>TaskInvocationResult</c> / <c>StandardTaskResponse</c> shape a workflow's mapping sees is the
/// SAME regardless of whether <c>Workflow:TaskInvocation:Modes:{wireType}</c> resolves a task to
/// <c>Local</c> (in-process on Orchestration, the shipped default for these five types) or
/// <c>Remote</c> (shipped to the Execution service, exactly as every task ran before issue #1007).
/// </summary>
/// <remarks>
/// <para>
/// <b>This file's value comes from being run TWICE against the same components, not from either
/// run alone.</b> Run it once as-is (the shipped default routes all five types Local), then again
/// with every type forced back to Remote:
/// </para>
/// <code>
/// # Run 1 — shipped default (Local for http/daprservice/soap/statestore/cacheaside):
/// dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~TaskInvocationLab.ResultModelParityTests"
///
/// # Run 2 — force every type back to Remote. Set these BEFORE starting the Orchestration host
/// # (TaskInvocationOptions binds once at startup — see docs/runtime/task-invocation-routing.md
/// # "Reverting a type to Remote" in the vnext repo), then re-run the SAME filter:
/// Workflow__TaskInvocation__Modes__http=Remote \
/// Workflow__TaskInvocation__Modes__daprservice=Remote \
/// Workflow__TaskInvocation__Modes__soap=Remote \
/// Workflow__TaskInvocation__Modes__statestore=Remote \
/// Workflow__TaskInvocation__Modes__cacheaside=Remote \
/// dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
/// # (the Execution service must also be running for Remote dispatch to have anywhere to go)
/// dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~TaskInvocationLab.ResultModelParityTests"
/// </code>
/// <para>
/// Both runs are expected to pass UNCHANGED — no test here branches on which mode is active, by
/// design (the whole point is that the caller-visible shape must not need to know).
/// </para>
/// <para>
/// <b>Why <c>tilTaskType</c> is asserted case-insensitively.</b> This field is the parity trap the
/// scenario was built to catch — the component build report notes it was mis-stamped twice on
/// this branch depending on routing, and it is what gets serialized into the InstanceTasks
/// journal. Reading the runtime source directly (not by running it — this task forbids that)
/// shows the casing itself is not a single, uniform contract today:
/// <c>TaskExecutorBase.CreateSuccessResponse</c>/<c>CreateErrorResponse</c> stamp
/// <c>TaskType.ToString()</c> off the local <c>TaskType</c> enum (PascalCase, e.g. <c>"Http"</c>),
/// while <c>CacheAsideTaskExecutor</c> and the <c>TaskInvocationResult</c> factories used by
/// <c>HttpTaskInvocation</c>/<c>DaprServiceInvocation</c>/<c>StateStoreInvocation</c> carry the
/// lowercase wire constant from <c>BBT.Workflow.Execution.TaskTypes</c> (e.g. <c>"cacheaside"</c>).
/// Both of those code paths are shared, unconditionally, by the Local and Remote dispatch of the
/// SAME wire type — so a casing choice does not by itself prove a Local/Remote divergence, and
/// asserting one exact casing here would fail for a reason unrelated to the routing-parity claim
/// this class exists to check. What this test DOES pin, in both modes: the identifier names the
/// correct task type, is never empty, and does not depend on which path resolved the call.
/// </para>
/// </remarks>
public class ResultModelParityTests : TaskInvocationLabTestBase
{
    public ResultModelParityTests(VNextTestEnvironment environment) : base(environment) { }

    [SkippableFact]
    public async Task Http_ProjectionShape_IsStableAcrossRoutingModes()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL).");

        var instanceId = await RunCaseAsync("case-http-ok");
        var projection = await GetProjectionAsync(instanceId);

        Assert.Equal("http", NullableString(projection, "tilTaskType"), ignoreCase: true);
        Assert.Equal(200, NullableLong(projection, "tilStatusCode"));

        // HttpTaskInvocation (shared by the Local and Remote path alike) always attaches exactly
        // these three metadata keys on success — verified by reading the invocation source, not by
        // running it.
        AssertMetadataKeySet(projection, "Url", "Method", "ReasonPhrase");
    }

    [SkippableFact]
    public async Task DaprService_ProjectionShape_IsStableAcrossRoutingModes()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL). til-dapr-ok also needs the local Dapr " +
            "sidecar routing the 'mocklab' app-id to it.");

        var instanceId = await RunCaseAsync("case-dapr-ok");
        var projection = await GetProjectionAsync(instanceId);

        // See TaskInvocationLabTestBase.IsDaprServiceUnreachable's remarks: this local
        // Orchestration sidecar cannot resolve MockLab's 'mocklab' Dapr app-id via mDNS at all
        // (verified by curling the sidecar's own invoke endpoint directly), independent of routing
        // mode — both Local and Remote dispatch go through the same sidecar.
        Skip.If(IsDaprServiceUnreachable(projection),
            "Dapr service invocation from the Orchestration sidecar to MockLab's 'mocklab' app-id " +
            "is not reachable in this local setup (sidecar answers 500 ERR_DIRECT_INVOKE — " +
            "\"couldn't find service: mocklab\"). Needs the sidecars' mDNS discovery fixed at the " +
            "infra level, not a component change.");

        Assert.Equal("daprservice", NullableString(projection, "tilTaskType"), ignoreCase: true);
        Assert.Equal(200, NullableLong(projection, "tilStatusCode"));

        // DaprServiceInvocation attaches no metadata dictionary today, in either routing mode.
        Assert.Equal(0, ArrayLength(projection, "tilMetadataKeys"));
    }

    [SkippableFact]
    public async Task Soap_ProjectionShape_IsStableAcrossRoutingModes()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL).");

        var instanceId = await RunCaseAsync("case-soap-ok");
        var projection = await GetProjectionAsync(instanceId);

        Assert.Equal("soap", NullableString(projection, "tilTaskType"), ignoreCase: true);
        Assert.Equal(200, NullableLong(projection, "tilStatusCode"));

        // SoapInvocation (shared by the Local and Remote path alike) always attaches exactly these
        // five metadata keys on a non-fault response — verified by reading the invocation source
        // (src/BBT.Workflow.Execution.Core/Invocation/SoapInvocation.cs in the vnext repo), not by
        // running it.
        AssertMetadataKeySet(projection, "Url", "Method", "ReasonPhrase", "SoapVersion", "IsSoapFault");
    }

    /// <summary>
    /// Uses <c>case-statestore-set</c> only (not the round-trip <c>get</c>) — <c>set</c> always
    /// writes the same literal value and is therefore order-independent, unlike CacheAside below.
    /// </summary>
    [Fact]
    public async Task StateStore_ProjectionShape_IsStableAcrossRoutingModes()
    {
        var instanceId = await RunCaseAsync("case-statestore-set");
        var projection = await GetProjectionAsync(instanceId);

        Assert.Equal("statestore", NullableString(projection, "tilTaskType"), ignoreCase: true);
        Assert.Equal(200, NullableLong(projection, "tilStatusCode"));

        // StateStoreInvocation.BaseMetadata (shared by Local and Remote alike) always attaches
        // StoreName + Command + Key, and SetAsync adds Saved.
        AssertMetadataKeySet(projection, "StoreName", "Command", "Key", "Saved");
    }

    /// <summary>
    /// Deliberately does NOT assert the <c>CacheHit</c> boolean — see
    /// <see cref="TaskTypeInvocationTests"/>'s remarks on why that value depends on which other
    /// case in this run last touched the same static cache key, not on the routing mode. What
    /// stays true either way (hit or miss) is the metadata KEY SET and the task type/status code,
    /// because <c>CacheAsideInvocation.BuildMetadata</c> always attaches the same five keys and the
    /// underlying source dispatch always defaults to <c>statusCode: 200</c> on success regardless
    /// of which branch (cache read vs. source dispatch) produced the result.
    /// </summary>
    [SkippableFact]
    public async Task CacheAside_ProjectionShape_IsStableAcrossRoutingModes()
    {
        Skip.If(!await IsMockLabUpAsync(),
            $"MockLab is not reachable at {MockLabBaseUrl()} — start it with `docker compose up -d` " +
            "in the repo root (or set MOCKLAB_BASE_URL). til-cache-source is a literal " +
            "http://localhost:3001 URL (see the component build report) so MOCKLAB_BASE_URL cannot " +
            "redirect it.");

        var instanceId = await RunCaseAsync("case-cacheaside");
        var projection = await GetProjectionAsync(instanceId);

        Assert.Equal("cacheaside", NullableString(projection, "tilTaskType"), ignoreCase: true);
        Assert.Equal(200, NullableLong(projection, "tilStatusCode"));
        Assert.True(BoolOrFalse(projection, "tilHasData"));
        Assert.Equal("til-cache-source-value", projection.GetProperty("tilData").GetProperty("value").GetString());

        AssertMetadataKeySet(projection, "StoreName", "Key", "CacheHit", "Refreshed", "ETag");
    }

    private static void AssertMetadataKeySet(System.Text.Json.JsonElement projection, params string[] expectedKeys)
    {
        var actual = MetadataKeySet(projection);
        var expected = expectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(actual.SetEquals(expected),
            $"expected metadata keys {{{string.Join(",", expected)}}}, got {{{string.Join(",", actual)}}}");
    }
}
