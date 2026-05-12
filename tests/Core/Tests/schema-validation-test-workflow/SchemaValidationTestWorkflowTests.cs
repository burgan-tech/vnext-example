using System.Net.Http;
using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SchemaValidationTestWorkflow;

/*
! Bugs:
! Cancel transition not available despite availableIn:[] (Transition:100020)
! Exit transition not available in data-initialized despite availableIn listing it (Transition:100020)
! UpdateData transition not available in SubFlow effective state (Transition:100020)
*/

/// <summary>
/// Integration tests for <c>schema-validation-test-workflow</c>.
/// Covers: master schema validation, transition schema enforcement (+ silent rejection),
/// field roles (master schema property visibility).
/// </summary>
public class SchemaValidationTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "schema-validation-test-workflow";
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private readonly WorkflowInstanceTestHelper _wf;
    private readonly SchemaValidationScenarioActions _scenario;

    public SchemaValidationTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
        _scenario = new SchemaValidationScenarioActions(_wf, ShortTimeout);
    }

    // =========================================================================
    // A. Happy Path
    // =========================================================================

    [Fact]
    public async Task HappyPath_FullChain_CompletesWithStatusC()
    {
        var instanceId = await _scenario.RunFullHappyPathAsync();

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));
        Assert.Equal("completed-state", StateFunctionJson.ExtractStateName(stateBody));
    }

    // =========================================================================
    // B. Start Transition Mapping — SchemaInitMapping sets expected fields
    // =========================================================================

    [Fact]
    public async Task StartTransition_SetsInitializedStatus()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var attrs = await _wf.GetAttributesAsync(instanceId);
        SchemaValidationInstanceDataAssertions.AssertInitializedAttributes(attrs);
    }

    // =========================================================================
    // C. Transition Schema Validation
    // =========================================================================

    [Fact]
    public async Task ConfirmTransition_WithValidSchema_MovesToSchemaValidatedState()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        await _wf.RunTransitionAsync(
            instanceId,
            "confirm-with-schema",
            headers: null,
            new { attributes = new { confirmed = true, confirmedBy = "integration-test-user" } }
        );
        await _wf.WaitForStateAsync(instanceId, "schema-validated-state", ShortTimeout);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("schema-validated-state", StateFunctionJson.ExtractStateName(stateBody));

        var attrs = await _wf.GetAttributesAsync(instanceId);
        SchemaValidationInstanceDataAssertions.AssertConfirmedAttributes(attrs);
    }

    [Fact]
    public async Task ConfirmTransition_WithPartialData_DoesNotTransition()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            _wf.RunTransitionAsync(
                instanceId,
                "confirm-with-schema",
                headers: null,
                new { attributes = new { confirmed = true } }
            )
        );
        Assert.Contains("schema validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        var stateName = await _wf.GetStateNameAsync(instanceId);
        Assert.Equal("data-initialized", stateName);
    }

    // =========================================================================
    // D. Field Roles (Master Schema property-level visibility)
    // =========================================================================

    [Fact]
    public async Task FieldRoles_AdminRole_SeesInternalNote()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var dataBody = await _wf.CallFunctionAsync(
            instanceId,
            "data",
            headers: WorkflowTestHttpHeaders.Role("admin")
        );

        var dataContent = ExtractDataContent(dataBody);
        SchemaValidationInstanceDataAssertions.AssertFieldVisible(dataContent, "internalNote");
    }

    [Fact]
    public async Task FieldRoles_CustomerRole_DoesNotSeeInternalNote()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var dataBody = await _wf.CallFunctionAsync(
            instanceId,
            "data",
            headers: WorkflowTestHttpHeaders.Role("customer")
        );

        var dataContent = ExtractDataContent(dataBody);
        SchemaValidationInstanceDataAssertions.AssertFieldHidden(dataContent, "internalNote");
    }

    [Fact]
    public async Task FieldRoles_AuditorRole_SeesAuditLog()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var dataBody = await _wf.CallFunctionAsync(
            instanceId,
            "data",
            headers: WorkflowTestHttpHeaders.Role("auditor")
        );

        var dataContent = ExtractDataContent(dataBody);
        SchemaValidationInstanceDataAssertions.AssertFieldVisible(dataContent, "auditLog");
    }

    [Fact]
    public async Task FieldRoles_NoRole_DoesNotSeeRoleRestrictedFields()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var dataBody = await _wf.CallFunctionAsync(instanceId, "data", headers: null);

        var dataContent = ExtractDataContent(dataBody);
        SchemaValidationInstanceDataAssertions.AssertFieldHidden(dataContent, "internalNote");
        SchemaValidationInstanceDataAssertions.AssertFieldHidden(dataContent, "auditLog");
    }

    // =========================================================================
    // E. Schema-less Transition (accepts any body)
    // =========================================================================

    [Fact]
    public async Task NoSchemaTransition_AcceptsAnyBody()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        await _wf.RunTransitionAsync(instanceId, "skip-to-no-schema", headers: null);
        await _wf.WaitForStateAsync(instanceId, "no-schema-state", ShortTimeout);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("no-schema-state", StateFunctionJson.ExtractStateName(stateBody));
    }

    // =========================================================================
    // F. Master Schema — Invalid Enum Rejection
    // =========================================================================

    [Fact]
    public async Task MasterSchema_RejectsInstanceWithInvalidEnum()
    {
        try
        {
            var instanceId = await _scenario.StartWithValidDataAsync(currency: "JPY");

            var stateName = await _wf.GetStateNameAsync(instanceId);
            Assert.Fail(
                $"Expected start rejection for invalid currency enum 'JPY', "
                    + $"but instance was created and reached state '{stateName}'. "
                    + "Master schema enum validation may not reject at start time."
            );
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
        {
            Assert.Fail(
                "Workflow not found in runtime (404). Publish the workflow first, then re-run tests."
            );
        }
        catch (HttpRequestException)
        {
            // 4xx rejection — master schema (or start schema) correctly blocked invalid enum
        }
    }

    // =========================================================================
    // G. Start Transition Schema Validation
    // =========================================================================

    [Fact]
    public async Task StartTransitionSchema_RejectsMissingRequiredFields()
    {
        try
        {
            var instanceId = await _scenario.StartWithInvalidStartSchemaAsync(
                new { customerName = "Only name, missing orderId/amount/currency" }
            );

            var stateName = await _wf.GetStateNameAsync(instanceId);
            Assert.Fail(
                $"Expected start rejection for missing required fields (orderId, amount, currency), "
                    + $"but instance was created and reached state '{stateName}'. "
                    + "Start transition schema validation did not reject the payload."
            );
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
        {
            Assert.Fail(
                "Workflow not found in runtime (404). Publish the workflow first, then re-run tests."
            );
        }
        catch (HttpRequestException)
        {
            // 4xx rejection — start schema correctly blocked missing required fields
        }
    }

    [Fact]
    public async Task StartTransitionSchema_AcceptsValidPayload()
    {
        var instanceId = await _scenario.StartWithValidDataAsync(
            orderId: "ORD-VALID-001",
            customerName: "Valid Customer",
            amount: 100.0m,
            currency: "USD"
        );

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("data-initialized", StateFunctionJson.ExtractStateName(stateBody));
    }

    // =========================================================================
    // H. Manual Transition Schema — Invalid Payload Rejection
    // =========================================================================

    [Fact]
    public async Task ConfirmTransition_WithInvalidSchemaPayload_DoesNotChangeState()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            _wf.RunTransitionAsync(
                instanceId,
                "confirm-with-schema",
                headers: null,
                new { attributes = new { confirmed = "yes", confirmedBy = 123 } }
            )
        );
        Assert.Contains("schema validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        var stateName = await _wf.GetStateNameAsync(instanceId);
        Assert.Equal("data-initialized", stateName);
    }

    [Fact]
    public async Task ConfirmTransition_WithMissingRequiredField_DoesNotChangeState()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            _wf.RunTransitionAsync(
                instanceId,
                "confirm-with-schema",
                headers: null,
                new { attributes = new { confirmed = true } }
            )
        );
        Assert.Contains("schema validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        var stateName = await _wf.GetStateNameAsync(instanceId);
        Assert.Equal("data-initialized", stateName);
    }

    // =========================================================================
    // I. Cancel Transition Schema
    // =========================================================================

    [Fact]
    public async Task CancelTransition_WithValidSchemaPayload_MovesToCancelled()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        await _scenario.CancelWithSchemaAsync(
            instanceId,
            new { cancelReason = "No longer needed", cancelledBy = "test-user" }
        );

        await _wf.WaitForStateAsync(instanceId, "cancelled-state", ShortTimeout);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("cancelled-state", StateFunctionJson.ExtractStateName(stateBody));
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));
    }

    [Fact]
    public async Task CancelTransition_WithMissingCancelReason_DoesNotCancel()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        await _scenario.CancelWithSchemaAsync(instanceId, new { cancelledBy = "test-user" });

        await Task.Delay(1000);

        var stateName = await _wf.GetStateNameAsync(instanceId);
        Assert.Equal("data-initialized", stateName);
    }

    // =========================================================================
    // J. Exit Transition Schema
    // =========================================================================

    [Fact]
    public async Task ExitTransition_WithValidSchemaPayload_MovesToExited()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        await _scenario.ExitWithSchemaAsync(
            instanceId,
            new { exitReason = "Test completed", exitCode = "success" }
        );

        await _wf.WaitForStateAsync(instanceId, "exited-state", ShortTimeout);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal("exited-state", StateFunctionJson.ExtractStateName(stateBody));
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));
    }

    [Fact]
    public async Task ExitTransition_WithInvalidExitCodeEnum_DoesNotExit()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();

        await _scenario.ExitWithSchemaAsync(
            instanceId,
            new { exitReason = "Test", exitCode = "invalid-code" }
        );

        await Task.Delay(1000);

        var stateName = await _wf.GetStateNameAsync(instanceId);
        Assert.Equal("data-initialized", stateName);
    }

    // =========================================================================
    // K. UpdateData Schema (SubFlow context)
    // =========================================================================

    [Fact]
    public async Task UpdateData_InSubflow_WithValidSchema_UpdatesAttributes()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();
        await _scenario.ConfirmWithSchemaAsync(instanceId);
        await _scenario.EnterSubflowAsync(instanceId);
        await Task.Delay(2000);

        var bodyBefore = await _wf.GetInstanceBodyAsync(instanceId);
        var eTagBefore = GetETag(bodyBefore);

        await _scenario.RunUpdateDataAsync(
            instanceId,
            new { notes = "Updated via schema-validated updateData" }
        );

        await Task.Delay(1500);

        var bodyAfter = await _wf.GetInstanceBodyAsync(instanceId);
        var eTagAfter = GetETag(bodyAfter);

        Assert.NotEqual(eTagBefore, eTagAfter);

        var parentAttrs = await _wf.GetAttributesAsync(instanceId);
        Assert.True(
            parentAttrs.TryGetProperty("notes", out var notesEl),
            "updateData should write 'notes' field to parent instance attributes, but it was not found. "
                + "Verify that child workflow's updateData mapping writes the transition payload into parent data."
        );
        Assert.Equal("Updated via schema-validated updateData", notesEl.GetString());
    }

    [Fact]
    public async Task UpdateData_InSubflow_WithMissingNotes_DoesNotUpdate()
    {
        var instanceId = await _scenario.StartWithValidDataAsync();
        await _scenario.ConfirmWithSchemaAsync(instanceId);
        await _scenario.EnterSubflowAsync(instanceId);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            _scenario.RunUpdateDataAsync(instanceId, new { someOtherField = "not notes" })
        );
        Assert.Contains("schema validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        var parentAttrs = await _wf.GetAttributesAsync(instanceId);
        Assert.False(
            parentAttrs.TryGetProperty("someOtherField", out _),
            "updateData with missing required 'notes' field should be rejected by schema validation. "
                + "Parent attributes should NOT contain 'someOtherField'."
        );
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    private static JsonElement ExtractDataContent(JsonElement body)
    {
        if (body.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            return dataEl;
        return body;
    }

    private static string GetETag(JsonElement attributes)
    {
        if (attributes.TryGetProperty("eTag", out var eTag))
            return eTag.GetString() ?? "";
        return "";
    }
}
