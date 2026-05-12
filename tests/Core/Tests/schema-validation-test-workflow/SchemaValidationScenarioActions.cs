using Core.IntegrationTests.Helpers;

namespace Core.IntegrationTests.Tests.SchemaValidationTestWorkflow;

/// <summary>
/// Scenario actions for <c>schema-validation-test-workflow</c>.
/// Encapsulates instance start, state transitions, and full happy-path chain.
/// </summary>
public sealed class SchemaValidationScenarioActions
{
    private readonly WorkflowInstanceTestHelper _wf;
    private readonly TimeSpan _timeout;

    public SchemaValidationScenarioActions(WorkflowInstanceTestHelper wf, TimeSpan timeout)
    {
        _wf = wf;
        _timeout = timeout;
    }

    /// <summary>
    /// Starts a new instance with valid master-schema data.
    /// Instance lands on <c>data-initialized</c> after SchemaInitMapping runs.
    /// </summary>
    public async Task<string> StartWithValidDataAsync(
        string? orderId = null,
        string? customerName = null,
        decimal amount = 1500.50m,
        string currency = "TRY"
    )
    {
        var id = await _wf.StartInstanceIdAsync(
            new
            {
                key = WorkflowInstanceTestHelper.UniqueInstanceKey("schema-validation"),
                tags = new[] { "integration-test", "schema-validation", "schema" },
                attributes = new
                {
                    orderId = orderId ?? $"ORD-{Guid.NewGuid():N}",
                    customerName = customerName ?? "Test Customer",
                    amount,
                    currency,
                },
            }
        );
        await _wf.WaitForStateAsync(id, "data-initialized", _timeout);
        return id;
    }

    /// <summary>
    /// Runs <c>confirm-with-schema</c> transition with valid payload.
    /// </summary>
    public async Task ConfirmWithSchemaAsync(string instanceId, string confirmedBy = "test-admin")
    {
        await _wf.RunTransitionAsync(
            instanceId,
            "confirm-with-schema",
            headers: null,
            new { attributes = new { confirmed = true, confirmedBy } }
        );
        await _wf.WaitForStateAsync(instanceId, "schema-validated-state", _timeout);
    }

    /// <summary>
    /// Runs <c>skip-to-no-schema</c> transition (schema-less, any body accepted).
    /// </summary>
    public async Task SkipToNoSchemaAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "skip-to-no-schema", headers: null);
        await _wf.WaitForStateAsync(instanceId, "no-schema-state", _timeout);
    }

    /// <summary>
    /// Runs <c>to-no-schema</c> transition from schema-validated-state.
    /// </summary>
    public async Task ToNoSchemaFromValidatedAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "to-no-schema", headers: null);
        await _wf.WaitForStateAsync(instanceId, "no-schema-state", _timeout);
    }

    /// <summary>
    /// Runs <c>complete-schema-validation-test</c> transition from no-schema-state to completed.
    /// </summary>
    public async Task CompleteAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "complete-schema-validation-test", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", _timeout);
    }

    /// <summary>
    /// Attempts to start an instance with invalid start-schema payload (e.g. missing required fields).
    /// Returns the instance ID if runtime silently accepts, or throws HttpRequestException for 4xx rejection.
    /// </summary>
    public async Task<string> StartWithInvalidStartSchemaAsync(object? attributesOverride = null)
    {
        var id = await _wf.StartInstanceIdAsync(
            new
            {
                key = WorkflowInstanceTestHelper.UniqueInstanceKey("schema-validation-invalid-start"),
                tags = new[] { "integration-test", "schema-validation", "invalid-start" },
                attributes = attributesOverride ?? new { customerName = "Missing orderId and amount" },
            }
        );
        await Task.Delay(500);
        return id;
    }

    /// <summary>
    /// Runs <c>enter-subflow</c> transition from schema-validated-state to subflow-state.
    /// Waits for effective state <c>subflow-child-active</c> (child's initial state).
    /// </summary>
    public async Task EnterSubflowAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "enter-subflow", headers: null);
        await _wf.WaitForStateAsync(instanceId, "subflow-child-active", _timeout);
    }

    /// <summary>
    /// Runs <c>update-with-schema</c> (updateData) transition in subflow-state.
    /// </summary>
    public async Task RunUpdateDataAsync(string instanceId, object payload)
    {
        await _wf.RunTransitionAsync(instanceId, "update-with-schema", headers: null, new { attributes = payload });
    }

    /// <summary>
    /// Runs <c>cancel-with-schema</c> transition with provided payload.
    /// </summary>
    public async Task CancelWithSchemaAsync(string instanceId, object payload)
    {
        await _wf.RunTransitionAsync(instanceId, "cancel-with-schema", headers: null, new { attributes = payload });
    }

    /// <summary>
    /// Runs <c>exit-with-schema</c> transition with provided payload.
    /// </summary>
    public async Task ExitWithSchemaAsync(string instanceId, object payload)
    {
        await _wf.RunTransitionAsync(instanceId, "exit-with-schema", headers: null, new { attributes = payload });
    }

    /// <summary>
    /// Full happy path: start → confirm → to-no-schema → complete.
    /// Returns instance id.
    /// </summary>
    public async Task<string> RunFullHappyPathAsync()
    {
        var id = await StartWithValidDataAsync();
        await ConfirmWithSchemaAsync(id);
        await ToNoSchemaFromValidatedAsync(id);
        await CompleteAsync(id);
        return id;
    }
}
