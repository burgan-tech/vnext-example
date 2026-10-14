using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.HumanTaskChain;

/// <summary>
/// The human-task list against a human state whose <c>queryRoles</c> reads
/// <c>(idmFirstApprover OR idmSuperUser) AND $InstanceBehalfOfStarter</c>, with the caller's roles
/// supplied by the <c>morph-idm</c> provider rather than by a role header.
/// </summary>
/// <remarks>
/// <para><b>The leaf state</b> (<c>ht-idm-approval</c> / <c>ht-idm-human</c>, single level, core):</para>
/// <code>
/// G1 allow allOf[idmFirstApprover, $InstanceBehalfOfStarter]
/// G2 allow allOf[idmSuperUser,     $InstanceBehalfOfStarter]
/// </code>
/// <para>Grants are depth 1 — a combinator cannot nest — so "(A OR B) AND C" is written as the OR of two
/// <c>allOf</c>s, which is the same condition.</para>
/// <para><b>Where the roles come from.</b> No request here carries a <c>role</c> header: under morph-idm a
/// role header takes precedence and the identity service is not asked, which would test the header,
/// not the provider. The MockLab seed <c>morph-idm-roles-collection.json</c> answers <c>get-roles</c>
/// keyed on <c>header.sub</c>; <c>act_sub</c> is sent because the runtime does not call morph-idm for a
/// caller carrying neither <c>act_sub</c> nor <c>client_id</c>.</para>
/// <para><b>Where the identity comes from.</b> <c>$InstanceBehalfOfStarter</c> compares the caller's
/// <c>sub</c> with the instance's <c>CreatedByBehalfOf</c>, which is the starter's <c>sub</c>. Every case
/// is started by an agent (<c>act_sub = u-idm-agent</c>) on behalf of the user named in <c>sub</c>.</para>
/// <para><b>Why a separate run.</b> The provider is chosen once at startup, so these tests skip unless
/// <c>VNEXT_CALLER_ROLE_PROVIDER=morph-idm</c> says the runtime under test was started that way — a
/// green run against the <c>default</c> provider would prove nothing.</para>
/// </remarks>
public class IdmBehalfOfGrantTests(VNextTestEnvironment environment)
    : WorkflowTestBase(environment)
{
    private const string Flow = "ht-idm-approval";
    private const string HumanState = "ht-idm-human";
    private const string Agent = "u-idm-agent";

    // Seeded in morph-idm-roles-collection.json, keyed on header.sub.
    private const string FirstApprover = "idm-first-approver";      // [idmFirstApprover]
    private const string FirstApprover2 = "idm-first-approver-2";   // [idmFirstApprover]
    private const string SuperUser = "idm-super-user";              // [idmSuperUser]
    private const string Other = "idm-other";                       // [idmOther]

    private static bool ProviderIsMorphIdm =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("VNEXT_CALLER_ROLE_PROVIDER"),
            "morph-idm",
            System.StringComparison.OrdinalIgnoreCase);

    /// <summary>A caller with no role header: <c>sub</c> and <c>act_sub</c> only.</summary>
    private static Dictionary<string, string> As(string sub, string actSub)
    {
        var headers = Headers();
        headers["sub"] = sub;
        headers["act_sub"] = actSub;
        return headers;
    }

    /// <summary>
    /// Starts a case on behalf of <paramref name="behalfOf"/> and waits until it rests in the human state.
    /// </summary>
    private async Task<string> StartOnBehalfOfAsync(string behalfOf)
    {
        var response = await Api.StartInstanceAsync(Flow, new
        {
            testId = Guid.NewGuid().ToString("N"),
            humanTask = new { title = "IDM approval", description = $"Approval on behalf of {behalfOf}" }
        }, As(behalfOf, Agent));

        var id = response.Body.GetProperty("id").GetString()!;
        await AssertNotFaultedAsync(Flow, id);
        await WaitForInstanceStateAsync(Flow, id, HumanState, timeout: TimeSpan.FromMinutes(1));
        return id;
    }

    /// <summary>The ids <paramref name="sub"/> is shown by core's human-task list, read past the cache.</summary>
    private async Task<IReadOnlyList<string>> VisibleRowIdsAsync(string sub)
    {
        var headers = As(sub, sub);
        headers["X-VNext-Cache-Override"] = "true";

        var (status, body) = await SendRawAsync(HttpMethod.Get, "api/v1/core/functions/human-task", headers: headers);
        Assert.True(status == HttpStatusCode.OK, $"human-task function failed: {status} {body}");

        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.EnumerateArray()
            .Select(row => row.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty)];
    }

    private Task WaitUntilVisibleAsync(string sub, string instanceId, string because) =>
        WaitUntilAsync(async () => (await VisibleRowIdsAsync(sub)).Contains(instanceId), because, TimeSpan.FromMinutes(1));

    /// <summary>G1 = idmFirstApprover ∧ behalf-of starter → listed.</summary>
    [SkippableFact]
    public async Task FirstApprover_OnWhoseBehalfTheCaseWasStarted_SeesTheTask()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var id = await StartOnBehalfOfAsync(FirstApprover);

        await WaitUntilVisibleAsync(FirstApprover, id,
            $"{FirstApprover} (morph-idm: idmFirstApprover, sub = behalf-of starter) never saw {id}: G1 = Yes ∧ Yes should admit");
    }

    /// <summary>G2 = idmSuperUser ∧ behalf-of starter → listed.</summary>
    [SkippableFact]
    public async Task SuperUser_OnWhoseBehalfTheCaseWasStarted_SeesTheTask()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var id = await StartOnBehalfOfAsync(SuperUser);

        await WaitUntilVisibleAsync(SuperUser, id,
            $"{SuperUser} (morph-idm: idmSuperUser, sub = behalf-of starter) never saw {id}: G2 = Yes ∧ Yes should admit");
    }

    /// <summary>
    /// Holding an admitted role is not enough: a caller who is not the behalf-of starter fails both
    /// <c>allOf</c>s. The positive read comes first, so the refusals are read from a list that is
    /// known to be current.
    /// </summary>
    [SkippableFact]
    public async Task ARoleHolder_WhoIsNotTheBehalfOfStarter_DoesNotSeeTheTask()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var id = await StartOnBehalfOfAsync(FirstApprover);

        await WaitUntilVisibleAsync(FirstApprover, id, $"{FirstApprover} never saw {id}; the negative halves would prove nothing");

        Assert.DoesNotContain(id, await VisibleRowIdsAsync(FirstApprover2)); // G1 Yes ∧ No, G2 No ∧ No
        Assert.DoesNotContain(id, await VisibleRowIdsAsync(SuperUser));      // G1 No ∧ No, G2 Yes ∧ No
    }

    /// <summary>
    /// Being the behalf-of starter is not enough either: without one of the two roles both
    /// <c>allOf</c>s are No.
    /// </summary>
    [SkippableFact]
    public async Task TheBehalfOfStarter_WithoutEitherRole_DoesNotSeeTheTask()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var refused = await StartOnBehalfOfAsync(Other);

        // Control: a case started at the same time for an admitted caller is listed, so the list
        // read below is current and the human-state projection has been written.
        var control = await StartOnBehalfOfAsync(FirstApprover);
        await WaitUntilVisibleAsync(FirstApprover, control, $"{FirstApprover} never saw the control case {control}");

        Assert.DoesNotContain(refused, await VisibleRowIdsAsync(Other)); // G1 No ∧ Yes, G2 No ∧ Yes
    }
}
