using VNext.Testing.Sdk.Builders;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Core-specific factory methods for building vNext workflow instance payloads.
///
/// Instance/transition API orchestration helpers: <see cref="WorkflowInstanceTestHelper"/>,
/// <see cref="StateFunctionJson"/>, <see cref="WorkflowTestHttpHeaders"/>.
///
/// Inherited helpers from <see cref="TestDataBuilderBase"/>:
///   - <c>BuildInstancePayload(key, tags, attributes)</c>
///   - <c>BuildTransitionBody(attributes)</c>
///   - <c>UniqueKey(prefix)</c>
///   - <c>DeterministicKey(prefix, parts)</c>
///   - <c>BuildCustomWorkingHours(schedule)</c>
///   - <c>DefaultWeekdaySchedule(...)</c>
/// </summary>
public class TestDataBuilder : TestDataBuilderBase
{
    // -------------------------------------------------------------------------
    // Example: replace with your domain's workflow payloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Example: start a "my-workflow" instance.
    /// Replace with your domain's actual workflow names and attributes.
    /// </summary>
    public static object MyWorkflow(string userId, string startDateTime, string endDateTime)
    {
        return BuildInstancePayload(
            key: DeterministicKey("my-wf", userId, startDateTime),
            tags: ["my-workflow"],
            attributes: new
            {
                userId,
                startDateTime,
                endDateTime
            });
    }

    /// <summary>
    /// Example: build an update/cancel transition body.
    /// </summary>
    public static object UpdateAttributes(object attributes) =>
        BuildTransitionBody(attributes);
}
