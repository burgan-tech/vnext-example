using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FuturePay;

/// <summary>
/// Extensions are enrichment on a READ, never part of a write response (runtime 0.0.93).
/// <para>
/// <c>loan-disbursement</c> declares the workflow-level extension
/// <c>customer-profile-enrichment</c> (<c>type=3 DefinedFlows</c>, <c>scope=1 GetInstance</c>),
/// whose task calls MockLab and files its result under <c>customerProfileEnrichment</c>. That
/// makes this flow the one place in the domain where the difference is observable end to end:
/// before 0.0.93 a <c>sync=true</c> start or transition ran the same extension pass as an
/// instance GET and returned its output on the write response; now it does not.
/// </para>
/// <para>
/// The suite is deliberately two-sided. Asserting only that the write response is empty would
/// also pass if the extension were broken, unpublished or never reached — so every case that
/// asserts the empty write response is paired with a read that proves the very same extension
/// DOES fire and DOES produce the profile.
/// </para>
/// </summary>
public class SyncResponseExtensionsTests : WorkflowTestBase
{
    private const string Workflow = "loan-disbursement";
    private const string Roles = "core.kredi-tahsis,core.operasyon";

    /// <summary>The key the extension's output mapping files its result under.</summary>
    private const string ExtensionKey = "customerProfileEnrichment";

    public SyncResponseExtensionsTests(VNextTestEnvironment environment) : base(environment) { }

    private static object ApplicationPayload(string customerId) => new
    {
        customerId,
        productType = "ihtiyac",
        requestedAmount = 50_000m,
        currency = "TRY",
        termMonths = 24,
        purpose = "Sync response extension scope test",
        monthlyIncome = 30_000m,
    };

    [Fact]
    public async Task SyncStart_CarriesAnEmptyExtensionsMap()
    {
        var response = await Api.StartInstanceAsync(Workflow, new { }, Headers(Roles));

        // The key survives so the response shape is unchanged for existing clients …
        Assert.True(response.Body.TryGetProperty("extensions", out var extensions),
            $"the start response dropped the 'extensions' key entirely: {response.Body}");
        Assert.Equal(JsonValueKind.Object, extensions.ValueKind);

        // … but nothing is ever put in it on a write.
        Assert.Empty(extensions.EnumerateObject());
    }

    [Fact]
    public async Task SyncTransition_CarriesAnEmptyExtensionsMap_WhileTheInstanceGetStillEnriches()
    {
        var customerId = $"C{Random.Shared.Next(100000, 999999)}";
        var id = await StartAsync(Workflow, new { }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "application-intake", Roles);

        var transition = await Api.RunTransitionAsync(
            Workflow, id, "submit-application", ApplicationPayload(customerId), Headers(Roles));

        Assert.True(transition.Body.TryGetProperty("extensions", out var writeExtensions),
            $"the transition response dropped the 'extensions' key entirely: {transition.Body}");
        Assert.Empty(writeExtensions.EnumerateObject());

        // The other half of the claim: the extension is live, reachable and produces a profile —
        // it simply is not run on the write path. Without this the assertion above would also
        // hold for an extension that was broken or never published.
        var instance = await Api.GetInstanceAsync(Workflow, id, Headers(Roles));
        var readExtensions = instance.Body.GetProperty("extensions");
        Assert.True(readExtensions.TryGetProperty(ExtensionKey, out var enrichment),
            $"the instance GET did not run '{ExtensionKey}' — the read path regressed: {readExtensions}");
        Assert.Equal(
            customerId,
            enrichment.GetProperty("customerProfile").GetProperty("customerId").GetString());
    }

    [Fact]
    public async Task DataFunction_StillRunsTheExtension()
    {
        var customerId = $"C{Random.Shared.Next(100000, 999999)}";
        var id = await StartAsync(Workflow, new { }, Roles);
        await WaitForInstanceStateAsync(Workflow, id, "application-intake", Roles);
        await RunAcceptedAsync(Workflow, id, "submit-application", ApplicationPayload(customerId), Roles);

        // The surface a client is pointed at when it actually wants the enrichment after a write.
        var data = await Api.CallInstanceFunctionAsync(Workflow, id, "data", headers: Headers(Roles));

        var extensions = data.Body.GetProperty("extensions");
        Assert.True(extensions.TryGetProperty(ExtensionKey, out var enrichment),
            $"the data function did not run '{ExtensionKey}': {extensions}");
        Assert.Equal(
            customerId,
            enrichment.GetProperty("customerProfile").GetProperty("customerId").GetString());
    }

    [Fact]
    public async Task ALegacyCallerStillSendingTheExtensionsQueryParameter_IsNotRejected()
    {
        // `?extensions=` was removed from the start and transition endpoint signatures. ASP.NET
        // does not reject an unbound query parameter, so an old client keeps working — it just
        // stops getting extensions back. This pins that promise: the removal is not a 400.
        var (status, body) = await SendRawAsync(
            HttpMethod.Post,
            $"api/v1/core/workflows/{Workflow}/instances/start" +
            "?sync=true&extensions=customer-profile-enrichment",
            new { },
            Headers(Roles));

        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        Assert.Empty(document.RootElement.GetProperty("extensions").EnumerateObject());
    }
}
