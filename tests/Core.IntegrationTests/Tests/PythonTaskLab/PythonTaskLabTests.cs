using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.PythonTaskLab;

/// <summary>
/// python-task-lab: PythonTask (type 23) end to end — Orchestration builds the contract request
/// from the standard mapping, calls the domain's Python runtime container over Dapr service
/// invocation (app-id <c>vnext-python-runtime-app</c>) and turns the answer into a task result.
/// <para>
/// Every case starts a fresh instance on <c>ready</c> and fires one case transition to
/// <c>landed</c>. The shared <c>PtlResultProjection.csx</c> writes the task result into instance
/// data (<c>ptl*</c>) for success AND failure, so the contract error code reaches the test as
/// <c>ptlErrorMessage = "&lt;code&gt;: &lt;message&gt;"</c>. Failure cases declare no boundary: the
/// failure is not acted on and the instance still lands; <c>case-fail-abort</c> adds a task-level
/// Abort on 422 and must fault.
/// </para>
/// <para>
/// Needs <c>labs/python-runtime/lab.sh up</c> and the Orchestration host started with
/// <c>Workflow__PythonRuntime__AppId=vnext-python-runtime-app</c>; without the runtime every test
/// skips (probe: <c>PYTHON_RUNTIME_URL</c>, default <c>http://localhost:8089</c>).
/// </para>
/// </summary>
public class PythonTaskLabTests : WorkflowTestBase
{
    private const string Workflow = "python-task-lab";
    private static readonly string RuntimeUrl =
        System.Environment.GetEnvironmentVariable("PYTHON_RUNTIME_URL") ?? "http://localhost:8089";

    public PythonTaskLabTests(VNextTestEnvironment environment) : base(environment) { }

    [SkippableFact]
    public async Task Sum_InputFromTransitionBody_ReplacesAuthoredInput()
    {
        var data = await RunCaseAsync("case-sum", new { values = new[] { 2, 3, 5 } });

        AssertSuccess(data);
        Assert.Equal(10, data.GetProperty("ptlData").GetProperty("total").GetInt32());
        Assert.Equal(3, data.GetProperty("ptlData").GetProperty("count").GetInt32());
    }

    [SkippableFact]
    public async Task StaticInput_WithoutMappedInput_SendsAuthoredConfigInput()
    {
        var data = await RunCaseAsync("case-static-input");

        AssertSuccess(data);
        // ptl-sum's config.input is { values: [1, 1] }.
        Assert.Equal(2, data.GetProperty("ptlData").GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task Context_CarriesInstanceWorkflowTaskAndTimeout()
    {
        var (instanceId, data) = await RunCaseWithIdAsync("case-context");

        AssertSuccess(data);
        var output = data.GetProperty("ptlData");
        Assert.Equal("core", output.GetProperty("domain").GetString());
        Assert.Equal(Workflow, output.GetProperty("workflow").GetString());
        Assert.Equal(instanceId, output.GetProperty("instanceId").GetString());
        Assert.Equal("ptl-context", output.GetProperty("taskKey").GetString());
        Assert.Equal(7000, output.GetProperty("timeoutMs").GetInt32());
    }

    [SkippableFact]
    public async Task Dependency_InstalledAtImageBuild_IsImportable()
    {
        var data = await RunCaseAsync("case-dependency");

        AssertSuccess(data);
        Assert.Equal(2026, data.GetProperty("ptlData").GetProperty("year").GetInt32());
        Assert.Equal("Tuesday", data.GetProperty("ptlData").GetProperty("weekday").GetString());
    }

    [SkippableTheory]
    [InlineData("case-fail", 422, "handler_error: ValueError: ptl rejected by design")]
    [InlineData("case-unknown", 404, "unknown_handler: ")]
    [InlineData("case-timeout", 504, "timeout: ")]
    [InlineData("case-invalid-output", 422, "invalid_output: ")]
    public async Task ContractError_BecomesAFailedTaskResult(string caseKey, int statusCode, string messagePrefix)
    {
        var data = await RunCaseAsync(caseKey);

        Assert.False(data.GetProperty("ptlIsSuccess").GetBoolean(), $"{caseKey} reported success: {data}");
        Assert.Equal(statusCode, data.GetProperty("ptlStatusCode").GetInt32());
        var message = data.GetProperty("ptlErrorMessage").GetString() ?? string.Empty;
        Assert.StartsWith(messagePrefix, message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task HandlerError_MatchedByAbortBoundary_FaultsTheInstance_WithIncident()
    {
        await SkipUnlessRuntimeUpAsync();
        var instanceId = await StartReadyAsync();

        var (status, body) = await SendRawAsync(HttpMethod.Patch,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/case-fail-abort?sync=false",
            new { }, Headers());
        Assert.True((int)status < 400, $"case-fail-abort was refused with {(int)status}: {body}");

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Workflow, instanceId)).Status == "F",
            $"{Workflow}/{instanceId} never faulted — {await DescribeAsync(Workflow, instanceId)}");

        var (incidentStatus, incidentBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/incidents?page=1&pageSize=20", headers: Headers());
        Assert.True(incidentStatus == HttpStatusCode.OK, $"incidents refused with {(int)incidentStatus}: {incidentBody}");

        var incident = JsonDocument.Parse(incidentBody).RootElement.GetProperty("items").EnumerateArray()
            .FirstOrDefault(i => i.TryGetProperty("boundaryAction", out var a) && a.ValueKind == JsonValueKind.String);
        Assert.True(incident.ValueKind == JsonValueKind.Object, $"no boundary incident: {incidentBody}");
        Assert.Equal("Abort", incident.GetProperty("boundaryAction").GetString());
        Assert.Equal(422, incident.GetProperty("statusCode").GetInt32());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<JsonElement> RunCaseAsync(string caseKey, object? body = null) =>
        (await RunCaseWithIdAsync(caseKey, body)).Data;

    private async Task<(string InstanceId, JsonElement Data)> RunCaseWithIdAsync(string caseKey, object? body = null)
    {
        await SkipUnlessRuntimeUpAsync();
        var instanceId = await StartReadyAsync();

        await RunAcceptedAsync(Workflow, instanceId, caseKey, body);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.True(state == "landed" && status != "F",
            $"{caseKey} did not land — {await DescribeAsync(Workflow, instanceId)}");

        var data = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(caseKey, data.GetProperty("ptlCase").GetString());
        Assert.Equal("python", data.GetProperty("ptlTaskType").GetString(), ignoreCase: true);
        return (instanceId, data);
    }

    private async Task<string> StartReadyAsync()
    {
        var instanceId = await StartAsync(Workflow, new { });
        await WaitUntilSettledAsync(Workflow, instanceId);
        return instanceId;
    }

    private static void AssertSuccess(JsonElement data)
    {
        Assert.True(data.GetProperty("ptlIsSuccess").GetBoolean(), $"task failed: {data}");
        Assert.Equal(200, data.GetProperty("ptlStatusCode").GetInt32());
    }

    private static async Task SkipUnlessRuntimeUpAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        bool up;
        try { up = (await http.GetAsync($"{RuntimeUrl.TrimEnd('/')}/v1/health")).IsSuccessStatusCode; }
        catch (Exception) { up = false; }

        Skip.If(!up, $"Python runtime is not reachable at {RuntimeUrl} — start it with labs/python-runtime/lab.sh up " +
                     "and run the Orchestration host with Workflow__PythonRuntime__AppId=vnext-python-runtime-app.");
    }
}
