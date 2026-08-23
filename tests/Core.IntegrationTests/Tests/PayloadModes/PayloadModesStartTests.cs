using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.PayloadModes;

/// <summary>
/// Start transition with a <c>schema</c>: all three payload shapes must validate against that
/// schema and land the same business payload in instance data.
/// <para>
/// <b>Why this exists.</b> Payload-mode detection used to key on a single case-sensitive
/// <c>attributes</c> property. Any standard envelope that omitted <c>attributes</c> — or spelled
/// it differently — was read as free-form and wrapped whole, so the start schema was evaluated
/// against <c>key</c>/<c>tags</c> instead of the business payload and rejected a valid request.
/// </para>
/// </summary>
public class PayloadModesStartTests : PayloadModesTestBase
{
    public PayloadModesStartTests(VNextTestEnvironment environment) : base(environment) { }

    // ── the three shapes ─────────────────────────────────────────────────────

    [Fact]
    public async Task StandardPayloadWithKey_Validates_AndReflectsOnlyTheAttributesAsThePayload()
    {
        var key = Guid.NewGuid().ToString();

        var (status, body) = await StartRawAsync(StandardWithKey(key));

        Assert.True(status == HttpStatusCode.OK, $"start was rejected with {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement;

        // The envelope was consumed AS an envelope: key became the instance key, not payload data.
        Assert.Equal(key, root.GetProperty("key").GetString());
        AssertPayloadReflected(AttributesOf(body));
    }

    [Fact]
    public async Task StandardPayloadWithoutKey_Validates_AndReflectsTheSamePayload()
    {
        // Every envelope field is optional. `attributes` alone is a complete standard payload.
        var (status, body) = await StartRawAsync(StandardWithoutKey);

        Assert.True(status == HttpStatusCode.OK, $"start was rejected with {(int)status}: {body}");
        AssertPayloadReflected(AttributesOf(body));
    }

    [Fact]
    public async Task FreeFormPayload_Validates_AndReflectsTheSamePayload()
    {
        // No envelope at all — the body itself is the payload.
        var (status, body) = await StartRawAsync(FreeForm);

        Assert.True(status == HttpStatusCode.OK, $"start was rejected with {(int)status}: {body}");
        AssertPayloadReflected(AttributesOf(body));
    }

    [Fact]
    public async Task AllThreeShapes_ProduceIdenticalInstanceData()
    {
        // The contract in one assertion: how the payload was delivered must not change what the
        // instance ends up holding.
        var results = new List<JsonElement>();
        foreach (var shape in new[] { StandardWithKey(Guid.NewGuid().ToString()), StandardWithoutKey, FreeForm })
        {
            var (status, body) = await StartRawAsync(shape);
            Assert.True(status == HttpStatusCode.OK, $"start was rejected with {(int)status}: {body}");
            results.Add(AttributesOf(body));
        }

        var canonical = results.Select(a => JsonSerializer.Serialize(a)).Distinct().ToList();
        Assert.True(canonical.Count == 1,
            "the three payload shapes produced different instance data: " + string.Join(" | ", canonical));
    }

    // ── validation must engage in every mode ─────────────────────────────────

    /// <summary>Valid JSON, but 'customer' — a required property — is absent.</summary>
    private const string MissingCustomer = """{"session":"-"}""";

    public static TheoryData<string, string> InvalidPayloads() => new()
    {
        { "standard with key", Standard(MissingCustomer, Guid.NewGuid().ToString()) },
        { "standard without key", Standard(MissingCustomer) },
        { "free-form", MissingCustomer },
    };

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task MissingRequiredField_IsRejected_InEveryPayloadMode(string shape, string json)
    {
        // A schema that only bites in one mode is worse than no schema: it makes the contract
        // depend on how the client happened to wrap its data.
        var (status, body) = await StartRawAsync(json);

        Assert.True(status == HttpStatusCode.BadRequest,
            $"the {shape} payload was missing 'customer' but the runtime answered {(int)status}: {body}");
        AssertSchemaValidationFailure(shape, body, "customer");
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task RejectedPayload_ReportsTheBusinessField_NotTheEnvelope(string shape, string json)
    {
        var (_, body) = await StartRawAsync(json);

        // The regression this scenario was built for: the complaint must be about the payload.
        AssertEnvelopeWasNotValidatedAsPayload(shape, body);
    }

    // ── envelope-only, and the explicit override ─────────────────────────────

    [Fact]
    public async Task EnvelopeWithoutAttributes_IsTreatedAsAnEnvelope_NotAsThePayload()
    {
        // `{"key": "..."}` is a valid envelope that simply carries no business data. It must be
        // rejected for the payload that is MISSING, never for `key` being an unexpected property.
        var (status, body) = await StartRawAsync(EnvelopeOnly(withTags: true));

        Assert.True(status == HttpStatusCode.BadRequest, $"expected a 400, got {(int)status}: {body}");
        AssertSchemaValidationFailure("envelope-only", body, "session", "customer");
        AssertEnvelopeWasNotValidatedAsPayload("envelope-only", body);
    }

    [Fact]
    public async Task PascalCasedAttributes_IsStillAStandardPayload()
    {
        // JSON model binding downstream is case-insensitive, so detection must be too — otherwise
        // `Attributes` binds correctly but is never reached.
        var (status, body) = await StartRawAsync("{\"Attributes\":" + BusinessPayload + "}");

        Assert.True(status == HttpStatusCode.OK, $"start was rejected with {(int)status}: {body}");
        AssertPayloadReflected(AttributesOf(body));
    }

    [Fact]
    public async Task RawModeHeader_ForcesTheWholeBodyToBeTreatedAsThePayload()
    {
        // The escape hatch for a business payload whose own fields collide with envelope names.
        // With raw forced, the envelope IS the payload and must fail this schema.
        var headers = Headers();
        headers["x-vnext-payload-mode"] = "raw";

        var (status, _) = await StartRawAsync(StandardWithoutKey, headers);

        Assert.True(status == HttpStatusCode.BadRequest,
            "x-vnext-payload-mode: raw was ignored — the body was still unwrapped as an envelope");
    }
}
