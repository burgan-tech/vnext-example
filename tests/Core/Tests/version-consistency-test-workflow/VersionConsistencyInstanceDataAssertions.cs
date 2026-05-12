using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.VersionConsistencyTestWorkflow;

/// <summary>
/// Instance <c>attributes</c> contract assertions for <c>version-consistency-test-workflow</c>.
/// Field names align with <c>core/Workflows/version-consistency/src/*.csx</c>.
/// </summary>
public static class VersionConsistencyInstanceDataAssertions
{
    private const string ContractHint =
        "(version-consistency-test-workflow *.csx mapping contract)";

    /// <summary>
    /// Reads <c>flowVersion</c> from a <c>GET /instances/{id}</c> response body.
    /// This is the authoritative source for the version an instance is pinned to.
    /// See <c>vnext-runtime/doc/tr/how-to/start-instance.md</c> (~line 219) for the response shape.
    ///
    /// Format observed in this runtime: <c>"{workflowVersion}-pkg.{packageVersion}+{domain}"</c>,
    /// e.g. <c>"2.0.0-pkg.1.0.0+core"</c>. The leading semver segment is the workflow version.
    /// </summary>
    public static string ExtractFlowVersion(JsonElement instanceBody)
    {
        Assert.True(
            instanceBody.TryGetProperty("flowVersion", out var versionEl)
                && versionEl.ValueKind == JsonValueKind.String,
            "GET /instances/{id} response should include 'flowVersion' as a string."
        );
        var version = versionEl.GetString();
        Assert.False(
            string.IsNullOrWhiteSpace(version),
            "'flowVersion' should be a non-empty string."
        );
        return version!;
    }

    /// <summary>
    /// Extracts the workflow semver portion (e.g. <c>"2.0.0"</c>) from a full
    /// <c>flowVersion</c> string like <c>"2.0.0-pkg.1.0.0+core"</c>.
    /// </summary>
    public static string ExtractWorkflowSemver(JsonElement instanceBody)
    {
        var full = ExtractFlowVersion(instanceBody);
        var dashIdx = full.IndexOf('-');
        return dashIdx > 0 ? full.Substring(0, dashIdx) : full;
    }

    /// <summary>
    /// Asserts that the instance's workflow semver portion equals <paramref name="expectedSemver"/>.
    /// E.g. <c>AssertWorkflowVersion(body, "2.0.0")</c> matches <c>"2.0.0-pkg.1.0.0+core"</c>.
    /// </summary>
    public static void AssertWorkflowVersion(JsonElement instanceBody, string expectedSemver)
    {
        var actual = ExtractWorkflowSemver(instanceBody);
        var full = ExtractFlowVersion(instanceBody);
        Assert.True(
            actual == expectedSemver,
            $"Expected workflow semver '{expectedSemver}', got '{actual}' (full flowVersion='{full}')."
        );
    }

    /// <summary>
    /// After start + auto transition to processing-state:
    /// <c>initialized=true</c>, <c>initAt</c> (ISO-8601), optionally <c>testId</c>.
    /// </summary>
    public static void AssertInitializedAttributes(JsonElement attrs, string? expectedTestId = null)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attrs,
            "initialized",
            $"InitVersionMapping should set initialized=true {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "initAt",
            $"InitVersionMapping should set initAt as ISO-8601 {ContractHint}"
        );

        if (expectedTestId is not null)
        {
            JsonElementAssertions.AssertPropertyString(
                attrs,
                "testId",
                expectedTestId,
                $"InitVersionMapping should preserve testId from start attributes {ContractHint}"
            );
        }
    }

    /// <summary>
    /// After review-state onEntries (v2 only): <c>reviewExecuted=true</c>, <c>reviewAt</c> non-empty.
    /// </summary>
    public static void AssertReviewExecuted(JsonElement attrs)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attrs,
            "reviewExecuted",
            $"ReviewMapping should set reviewExecuted=true {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "reviewAt",
            $"ReviewMapping should set reviewAt as ISO-8601 {ContractHint}"
        );
    }

    /// <summary>
    /// V2 completed-state onEntries: <c>v2Completed=true</c>, <c>completedByVersion="2.0.0"</c>,
    /// <c>completedAt</c> non-empty.
    /// </summary>
    public static void AssertV2Completed(JsonElement attrs)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attrs,
            "v2Completed",
            $"V2CompletedMapping should set v2Completed=true {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyString(
            attrs,
            "completedByVersion",
            "2.0.0",
            $"V2CompletedMapping should set completedByVersion='2.0.0' {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "completedAt",
            $"V2CompletedMapping should set completedAt as ISO-8601 {ContractHint}"
        );
    }

    /// <summary>
    /// V1 completed-state onEntries: <c>v1Completed=true</c>, <c>completedByVersion="1.0.0"</c>,
    /// <c>completedAt</c> non-empty.
    /// </summary>
    public static void AssertV1Completed(JsonElement attrs)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attrs,
            "v1Completed",
            $"V1CompletedMapping should set v1Completed=true {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyString(
            attrs,
            "completedByVersion",
            "1.0.0",
            $"V1CompletedMapping should set completedByVersion='1.0.0' {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "completedAt",
            $"V1CompletedMapping should set completedAt as ISO-8601 {ContractHint}"
        );
    }

    /// <summary>
    /// Negative assertion: v1-specific flags must NOT be present on a v2 instance.
    /// </summary>
    public static void AssertNoV1Flags(JsonElement attrs)
    {
        var hasV1Completed = attrs.TryGetProperty("v1Completed", out var v1El)
            && v1El.ValueKind == JsonValueKind.True;
        Assert.False(
            hasV1Completed,
            $"v1Completed should NOT be true on a v2-path instance {ContractHint}"
        );
    }

    /// <summary>
    /// Negative assertion: review flags must NOT be present on a v1 instance
    /// (review-state does not exist in v1).
    /// </summary>
    public static void AssertNoReviewFlags(JsonElement attrs)
    {
        var hasReviewExecuted = attrs.TryGetProperty("reviewExecuted", out var re)
            && re.ValueKind == JsonValueKind.True;
        Assert.False(
            hasReviewExecuted,
            $"reviewExecuted should NOT be true on a v1-path instance (review-state absent in v1) {ContractHint}"
        );

        var hasReviewAt = attrs.TryGetProperty("reviewAt", out var ra)
            && ra.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(ra.GetString());
        Assert.False(
            hasReviewAt,
            $"reviewAt should NOT be present on a v1-path instance {ContractHint}"
        );
    }
}
