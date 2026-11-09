using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// The <c>$Everyone</c> predefined role (vnext#1193) on master-schema <c>x-roles</c>, against the
/// <c>role-matrix-lab-combinators</c> flow (master schema <c>role-matrix-combinator-master</c> 1.0.2).
/// </summary>
/// <remarks>
/// <para><c>$Everyone</c> is an identity leaf that is Yes for every caller — with or without roles,
/// starter or not — so <c>hiddenFromAll</c>'s <c>[{ grant: deny, role: $Everyone }]</c> prunes the field
/// for everybody. A caller CLAIMING the role <c>$Everyone</c> in its role header changes nothing: the
/// leaf never looks at the caller's role set.</para>
/// <para><c>hiddenByWorkaround</c> pins the older spelling of the same intent,
/// <c>[deny $InstanceStarter, allow $InstanceStarter]</c>: the deny hides the field from the starter,
/// and the lone allow turns the set into an allowlist that admits nobody else.</para>
/// <para>Both fields are seeded in the start body, so they are STORED (instance data keeps them);
/// only the read surfaces prune them. <c>caseRef</c> and <c>customerId</c> carry no <c>x-roles</c> and are
/// the positive control: present for all four callers.</para>
/// </remarks>
public sealed class EveryoneGrantTests : RoleMatrixLabTestBase
{
    public EveryoneGrantTests(VNextTestEnvironment environment) : base(environment) { }

    private const string Flow = "role-matrix-lab-combinators";

    private const string Customer = "morph-idm.customer";
    private const string CorporateOps = "morph-idm.corporate-ops";

    private const string HiddenFromAll = "everyone-secret: never served";
    private const string HiddenByWorkaround = "workaround-secret: never served";

    /// <summary>The four callers: label, role header (or none), and <c>act_sub</c> (or none).</summary>
    private static readonly string[] Callers = ["starter", "corporate-ops", "role-less", "claims-$Everyone"];

    private static Dictionary<string, string> HeadersOf(string caller) => caller switch
    {
        // The starter: act_sub = CreatedBy, so $InstanceStarter is Yes for this caller only.
        "starter" => HeadersFor(Customer, StarterActor),
        "corporate-ops" => HeadersFor(CorporateOps),
        "role-less" => HeadersFor(NoRole),
        // A caller sending `role: $Everyone` (and `x-roles`) — the claim must not matter.
        "claims-$Everyone" => HeadersFor("$Everyone"),
        _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, null)
    };

    /// <summary>Starts one case AS THE STARTER with both hidden fields seeded in the start body.</summary>
    private async Task<(string InstanceId, string CaseRef)> StartSeededCaseAsync(string tag)
    {
        var caseRef = $"{tag}-{Guid.NewGuid():N}"[..24];
        var response = await Api.StartInstanceAsync(Flow, new
        {
            caseRef,
            customerId = "u-veli",
            hiddenFromAll = HiddenFromAll,
            hiddenByWorkaround = HiddenByWorkaround
        }, HeadersFor(Customer, StarterActor));

        var instanceId = response.Body.GetProperty("id").GetString()
                         ?? throw new InvalidOperationException("start response carried no instance id");

        await WaitUntilSettledAsync(Flow, instanceId, Customer);
        await AssertNotFaultedAsync(Flow, instanceId, Customer);
        return (instanceId, caseRef);
    }

    /// <summary>Instance attributes for a caller on one read surface, with the raw body for a leak check.</summary>
    private async Task<(JsonElement Attributes, string Raw)> ReadAsync(string surface, string instanceId, string caller)
    {
        var headers = HeadersOf(caller);
        string url;
        if (surface == "data")
        {
            // Read fresh, not from the data function's per-caller response cache.
            headers["X-VNext-Cache-Override"] = "true";
            url = $"api/v1/core/workflows/{Flow}/instances/{instanceId}/functions/data";
        }
        else
        {
            url = $"api/v1/core/workflows/{Flow}/instances/{instanceId}";
        }

        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: headers);
        Assert.True(status == HttpStatusCode.OK, $"{surface} as {caller} answered {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement.Clone();
        foreach (var envelope in new[] { "data", "attributes" })
        {
            if (root.TryGetProperty(envelope, out var inner) && inner.ValueKind == JsonValueKind.Object)
                return (inner, body);
        }

        return (root, body);
    }

    private static string? Str(JsonElement attributes, string name) =>
        attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private async Task AssertHiddenFromEveryCallerAsync(string surface)
    {
        var (instanceId, caseRef) = await StartSeededCaseAsync($"everyone-{surface}");

        foreach (var caller in Callers)
        {
            var (attributes, raw) = await ReadAsync(surface, instanceId, caller);

            Assert.True(!Has(attributes, "hiddenFromAll"),
                $"{surface} as {caller}: deny $Everyone must prune hiddenFromAll for every caller");
            Assert.True(!Has(attributes, "hiddenByWorkaround"),
                $"{surface} as {caller}: [deny $InstanceStarter, allow $InstanceStarter] must prune hiddenByWorkaround");
            Assert.DoesNotContain(HiddenFromAll, raw);
            Assert.DoesNotContain(HiddenByWorkaround, raw);

            // Positive control: the unguarded siblings are served to the same caller.
            Assert.True(Str(attributes, "caseRef") == caseRef,
                $"{surface} as {caller}: the unguarded caseRef must be present (positive control)");
            Assert.True(Str(attributes, "customerId") == "u-veli",
                $"{surface} as {caller}: the unguarded customerId must be present (positive control)");
        }
    }

    /// <summary>
    /// The data function: neither field reaches the starter, a corporate-ops caller, a role-less caller
    /// or a caller claiming <c>$Everyone</c>; the unguarded siblings reach all four.
    /// </summary>
    [Fact]
    public Task DataFunction_HidesEveryoneDeniedFields_FromEveryCaller() => AssertHiddenFromEveryCallerAsync("data");

    /// <summary>The instance GET goes through the same read service and prunes the same way.</summary>
    [Fact]
    public Task InstanceGet_HidesEveryoneDeniedFields_FromEveryCaller() => AssertHiddenFromEveryCallerAsync("get");
}
