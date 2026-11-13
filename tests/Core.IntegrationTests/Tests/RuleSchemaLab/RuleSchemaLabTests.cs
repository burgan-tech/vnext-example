using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RuleSchemaLab;

/// <summary>
/// Rule-based transition and function schemas (vnext#1225). The <c>review</c> state of
/// <c>rule-schema-lab</c> declares <c>submit</c> (two rule entries, no fallback), <c>submit-fallback</c>
/// (one rule entry + a rule-less retail fallback) and <c>note</c> (no schema). Rules read the
/// <c>x-segment</c> header: <c>corporate</c> selects a schema requiring <c>taxNumber</c>, <c>retail</c> one
/// requiring an 11-digit <c>nationalId</c>.
/// <para>
/// The selected schema is proven by its consequence: the same body passes under one segment and fails
/// under the other, so a runtime that ignored <c>schemas</c> (or picked the wrong entry) cannot pass.
/// </para>
/// </summary>
public class RuleSchemaLabTests : WorkflowTestBase
{
    private const string Workflow = "rule-schema-lab";
    private const string Function = "rule-schema-lab-echo";

    private static readonly object CorporateBody = new { taxNumber = "1234567890" };
    private static readonly object RetailBody = new { nationalId = "12345678901" };

    private readonly HttpClient _client;

    public RuleSchemaLabTests(VNextTestEnvironment environment) : base(environment)
    {
        // WorkflowTestBase's raw client does not surface response headers; the ETag is the point here.
        _client = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    [Fact]
    public async Task Submit_CorporateSegment_ValidatesAgainstTheCorporateEntry()
    {
        var instanceId = await StartReviewAsync();

        // A retail body under the corporate segment fails the corporate schema: the corporate entry won.
        var (wrong, wrongBody) = await PatchAsync(instanceId, "submit", RetailBody, "corporate");
        Assert.True(wrong == HttpStatusCode.BadRequest, $"retail body under corporate answered {(int)wrong}: {wrongBody}");
        Assert.DoesNotContain("Transition:100025", wrongBody);

        var (status, body) = await PatchAsync(instanceId, "submit", CorporateBody, "corporate");
        Assert.True((int)status < 400, $"corporate submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    [Fact]
    public async Task Submit_RetailSegment_ValidatesAgainstTheRetailEntry()
    {
        var instanceId = await StartReviewAsync();

        var (wrong, wrongBody) = await PatchAsync(instanceId, "submit", CorporateBody, "retail");
        Assert.True(wrong == HttpStatusCode.BadRequest, $"corporate body under retail answered {(int)wrong}: {wrongBody}");
        Assert.DoesNotContain("Transition:100025", wrongBody);

        var (status, body) = await PatchAsync(instanceId, "submit", RetailBody, "retail");
        Assert.True((int)status < 400, $"retail submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    /// <summary>No entry matches: 400 Transition:100025, and the instance keeps its state, unfaulted.</summary>
    [Fact]
    public async Task Submit_NoEntryMatches_Rejected_InstanceUntouched()
    {
        var instanceId = await StartReviewAsync();

        foreach (var segment in new[] { "sme", null })
        {
            var (status, body) = await PatchAsync(instanceId, "submit", CorporateBody, segment);
            Assert.True(status == HttpStatusCode.BadRequest, $"segment '{segment}' answered {(int)status}: {body}");
            Assert.Contains("Transition:100025", body);
        }

        await AssertSettledInAsync(instanceId, "review", "A");
    }

    /// <summary>A rule-less last entry catches the unmatched caller.</summary>
    [Fact]
    public async Task SubmitFallback_NoRuleMatches_FallbackEntryValidates()
    {
        var instanceId = await StartReviewAsync();

        var (wrong, wrongBody) = await PatchAsync(instanceId, "submit-fallback", CorporateBody, "sme");
        Assert.True(wrong == HttpStatusCode.BadRequest, $"corporate body on the fallback answered {(int)wrong}: {wrongBody}");
        Assert.DoesNotContain("Transition:100025", wrongBody);

        var (status, body) = await PatchAsync(instanceId, "submit-fallback", RetailBody, "sme");
        Assert.True((int)status < 400, $"fallback submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    /// <summary>No schema declared: any payload is accepted.</summary>
    [Fact]
    public async Task Note_NoSchemaDeclared_AcceptsAnyPayload()
    {
        var instanceId = await StartReviewAsync();

        var (status, body) = await PatchAsync(instanceId, "note", new { anything = 42 }, segment: null);
        Assert.True((int)status < 400, $"note answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "review", "A");
    }

    /// <summary>
    /// The schema function serves the rule-selected entry, 404 when none matches, and 304 to a
    /// revalidating client of the same segment; another segment never receives the first one's ETag.
    /// </summary>
    [Fact]
    public async Task SchemaFunction_ServesTheRuleSelectedEntry()
    {
        var instanceId = await StartReviewAsync();

        var (corporate, corporateTag, corporateBody) = await GetSchemaAsync(instanceId, "submit", "corporate");
        Assert.True(corporate == HttpStatusCode.OK, $"corporate schema answered {(int)corporate}: {corporateBody}");
        Assert.Contains("rule-schema-lab-corporate", corporateBody);
        Assert.False(string.IsNullOrEmpty(corporateTag), "schema function answered without an ETag");

        var (retail, retailTag, retailBody) = await GetSchemaAsync(instanceId, "submit", "retail");
        Assert.True(retail == HttpStatusCode.OK, $"retail schema answered {(int)retail}: {retailBody}");
        Assert.Contains("rule-schema-lab-retail", retailBody);
        Assert.NotEqual(corporateTag, retailTag);

        var (revalidated, _, _) = await GetSchemaAsync(instanceId, "submit", "corporate", corporateTag);
        Assert.Equal(HttpStatusCode.NotModified, revalidated);

        var (crossed, _, crossedBody) = await GetSchemaAsync(instanceId, "submit", "retail", corporateTag);
        Assert.True(crossed == HttpStatusCode.OK, $"retail with the corporate ETag answered {(int)crossed}: {crossedBody}");
        Assert.Contains("rule-schema-lab-retail", crossedBody);

        var (none, _, noneBody) = await GetSchemaAsync(instanceId, "submit", "sme");
        Assert.True(none == HttpStatusCode.NotFound, $"unmatched schema answered {(int)none}: {noneBody}");

        var (fallback, _, fallbackBody) = await GetSchemaAsync(instanceId, "submit-fallback", "sme");
        Assert.True(fallback == HttpStatusCode.OK, $"fallback schema answered {(int)fallback}: {fallbackBody}");
        Assert.Contains("rule-schema-lab-retail", fallbackBody);
    }

    /// <summary>A function whose rule-based inputSchema matches no entry rejects with Function:800006.</summary>
    [Fact]
    public async Task Function_RuleBasedInputSchemaNoMatch_Rejected()
    {
        var url = $"api/v1/{TestDomain.Name}/functions/{Function}";

        var (rejected, rejectedBody) = await SendRawAsync(HttpMethod.Post, url, CorporateBody, SegmentHeaders("retail"));
        Assert.True(rejected == HttpStatusCode.BadRequest, $"unmatched function call answered {(int)rejected}: {rejectedBody}");
        Assert.Contains("Function:800006", rejectedBody);

        var (invalid, invalidBody) = await SendRawAsync(HttpMethod.Post, url, RetailBody, SegmentHeaders("corporate"));
        Assert.True(invalid == HttpStatusCode.BadRequest, $"invalid corporate body answered {(int)invalid}: {invalidBody}");
        Assert.DoesNotContain("Function:800006", invalidBody);

        var (accepted, acceptedBody) = await SendRawAsync(HttpMethod.Post, url, CorporateBody, SegmentHeaders("corporate"));
        Assert.True(accepted == HttpStatusCode.OK, $"matched function call answered {(int)accepted}: {acceptedBody}");
        Assert.Contains("accepted", acceptedBody);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> StartReviewAsync()
    {
        var instanceId = await StartAsync(Workflow, new { testId = $"rule-schema-{Guid.NewGuid():N}"[..32] });
        await AssertSettledInAsync(instanceId, "review", "A");
        return instanceId;
    }

    private static Dictionary<string, string> SegmentHeaders(string? segment)
    {
        var headers = Headers();
        if (segment is not null) headers["x-segment"] = segment;
        return headers;
    }

    private Task<(HttpStatusCode Status, string Body)> PatchAsync(
        string instanceId, string transitionKey, object body, string? segment) =>
        SendRawAsync(
            HttpMethod.Patch,
            $"api/v1/{TestDomain.Name}/workflows/{Workflow}/instances/{instanceId}/transitions/{transitionKey}?sync=false",
            body,
            SegmentHeaders(segment));

    private async Task<(HttpStatusCode Status, string? ETag, string Body)> GetSchemaAsync(
        string instanceId, string transitionKey, string? segment, string? ifNoneMatch = null)
    {
        var url = $"api/v1/{TestDomain.Name}/workflows/{Workflow}/instances/{instanceId}/functions/schema" +
                  $"?transitionKey={transitionKey}";
        var headers = SegmentHeaders(segment);
        if (ifNoneMatch is not null) headers["If-None-Match"] = ifNoneMatch;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _client.SendAsync(request);
        var etag = response.Headers.ETag?.ToString()
                   ?? (response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null);
        return (response.StatusCode, etag, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertSettledInAsync(string instanceId, string expectedState, string expectedStatus)
    {
        await WaitForInstanceStateAsync(Workflow, instanceId, expectedState, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Workflow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(expectedState, state);
        Assert.Equal(expectedStatus, status);
        await AssertNotFaultedAsync(Workflow, instanceId);
    }
}
