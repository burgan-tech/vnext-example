using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.PayloadModes;

/// <summary>
/// The same contract on a <c>transition</c> that declares a <c>schema</c>.
/// <para>
/// Start and transition are separate endpoints with separate body handling, so a fix proven on
/// one says nothing about the other. Everything downstream of the controller is payload-mode
/// blind — the two endpoints are the only place the shapes can diverge, which is exactly why
/// both are covered here.
/// </para>
/// </summary>
public class PayloadModesTransitionTests : PayloadModesTestBase
{
    public PayloadModesTransitionTests(VNextTestEnvironment environment) : base(environment) { }

    // ── the three shapes ─────────────────────────────────────────────────────

    [Fact]
    public async Task StandardPayloadWithKey_Validates_AndReflectsOnlyTheAttributesAsThePayload()
    {
        var instanceId = await StartForTransitionAsync();

        var (status, body) = await SubmitRawAsync(instanceId, StandardWithKey(Guid.NewGuid().ToString()));

        Assert.True(status == HttpStatusCode.OK, $"submit-payload was rejected with {(int)status}: {body}");
        AssertPayloadReflected(AttributesOf(body));
        await AssertLandedInPayloadReceivedAsync(instanceId);
    }

    [Fact]
    public async Task StandardPayloadWithoutKey_Validates_AndReflectsTheSamePayload()
    {
        var instanceId = await StartForTransitionAsync();

        var (status, body) = await SubmitRawAsync(instanceId, StandardWithoutKey);

        Assert.True(status == HttpStatusCode.OK, $"submit-payload was rejected with {(int)status}: {body}");
        AssertPayloadReflected(AttributesOf(body));
        await AssertLandedInPayloadReceivedAsync(instanceId);
    }

    [Fact]
    public async Task FreeFormPayload_Validates_AndReflectsTheSamePayload()
    {
        var instanceId = await StartForTransitionAsync();

        var (status, body) = await SubmitRawAsync(instanceId, FreeForm);

        Assert.True(status == HttpStatusCode.OK, $"submit-payload was rejected with {(int)status}: {body}");
        AssertPayloadReflected(AttributesOf(body));
        await AssertLandedInPayloadReceivedAsync(instanceId);
    }

    [Fact]
    public async Task AllThreeShapes_ProduceIdenticalInstanceData()
    {
        var results = new List<string>();
        foreach (var shape in new[] { StandardWithKey(Guid.NewGuid().ToString()), StandardWithoutKey, FreeForm })
        {
            var instanceId = await StartForTransitionAsync();
            var (status, body) = await SubmitRawAsync(instanceId, shape);
            Assert.True(status == HttpStatusCode.OK, $"submit-payload was rejected with {(int)status}: {body}");

            // Read it back off the instance rather than the response, so persistence is covered too.
            results.Add(JsonSerializer.Serialize(await GetAttributesAsync(Workflow, instanceId)));
        }

        Assert.True(results.Distinct().Count() == 1,
            "the three payload shapes persisted different instance data: " + string.Join(" | ", results));
    }

    // ── validation must engage in every mode ─────────────────────────────────

    /// <summary>Valid JSON, but 'customer' — a required property — is absent.</summary>
    private const string MissingCustomerPayload = """{"session":"-"}""";

    /// <summary>Valid JSON, but 'session' — a required property — is absent.</summary>
    private const string MissingSession = """{"customer":{"ownerUserId":"1"}}""";

    /// <summary>A complete payload plus one property the schema does not allow.</summary>
    private const string RoguePayload = """{"session":"-","customer":{"ownerUserId":"1"},"rogue":1}""";

    public static TheoryData<string, string> InvalidPayloads() => new()
    {
        { "standard with key", Standard(MissingSession, Guid.NewGuid().ToString()) },
        { "standard without key", Standard(MissingSession) },
        { "free-form", MissingSession },
    };

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task MissingRequiredField_IsRejected_InEveryPayloadMode(string shape, string json)
    {
        var instanceId = await StartForTransitionAsync();

        var (status, body) = await SubmitRawAsync(instanceId, json);

        Assert.True(status == HttpStatusCode.BadRequest,
            $"the {shape} payload was missing 'session' but the runtime answered {(int)status}: {body}");
        AssertSchemaValidationFailure(shape, body, "session");
    }

    [Fact]
    public async Task UnknownBusinessField_IsRejected_InEveryPayloadMode()
    {
        // additionalProperties:false must bite on the payload's own extra field — proving the
        // schema really is being applied to the payload and not to something else.
        foreach (var (shape, json) in new[]
                 {
                     ("standard", Standard(RoguePayload)),
                     ("free-form", RoguePayload),
                 })
        {
            var instanceId = await StartForTransitionAsync();
            var (status, body) = await SubmitRawAsync(instanceId, json);

            Assert.True(status == HttpStatusCode.BadRequest,
                $"the {shape} payload carried an unknown field but the runtime answered {(int)status}: {body}");
        }
    }

    [Fact]
    public async Task RejectedPayload_LeavesTheInstanceWhereItWas()
    {
        var instanceId = await StartForTransitionAsync();

        var (status, _) = await SubmitRawAsync(instanceId, """{"attributes":{"session":"-"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);

        var (state, instanceStatus) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("collect-payload", state);
        Assert.Equal("A", instanceStatus);
    }

    // ── envelope-only ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvelopeWithoutAttributes_IsTreatedAsAnEnvelope_NotAsThePayload()
    {
        var instanceId = await StartForTransitionAsync();

        var (status, body) = await SubmitRawAsync(instanceId, EnvelopeOnly());

        Assert.True(status == HttpStatusCode.BadRequest, $"expected a 400, got {(int)status}: {body}");
        AssertSchemaValidationFailure("envelope-only", body, "session", "customer");
        AssertEnvelopeWasNotValidatedAsPayload("envelope-only", body);
    }

    // ── a transition with no schema still accepts every shape ────────────────

    [Fact]
    public async Task TransitionWithoutSchema_AcceptsEveryShape_AndNeverStoresTheEnvelope()
    {
        // `complete` declares no schema, so nothing would reject a leaked envelope. The damage
        // shows up in instance data instead: `key` silently persisted as if it were business data.
        var instanceId = await StartForTransitionAsync();
        await SubmitRawAsync(instanceId, FreeForm);

        var key = Guid.NewGuid().ToString();
        var (status, body) = await SendRawJsonAsync(
            HttpMethod.Patch,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/complete?sync=true",
            "{\"key\":\"" + key + "\"}",
            Headers());

        Assert.True(status == HttpStatusCode.OK, $"complete was rejected with {(int)status}: {body}");

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.False(attributes.TryGetProperty("key", out _),
            $"the envelope's 'key' was persisted as business data: {JsonSerializer.Serialize(attributes)}");
        AssertPayloadReflected(attributes);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task AssertLandedInPayloadReceivedAsync(string instanceId)
    {
        var (state, _) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("payload-received", state);
    }
}
