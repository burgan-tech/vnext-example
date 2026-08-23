using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.PayloadModes;

/// <summary>
/// Shared plumbing for the payload-modes scenario.
/// <para>
/// Every test here posts a <b>verbatim JSON string</b>. That is deliberate: the subject is the
/// request contract itself, so the exact bytes on the wire — which properties sit at the top
/// level, how they are cased — are the input under test and must not pass through a serializer
/// that could normalize them.
/// </para>
/// </summary>
public abstract class PayloadModesTestBase : WorkflowTestBase
{
    protected const string Workflow = "payload-modes";

    /// <summary>The business payload, in the three shapes a client may send it.</summary>
    protected const string BusinessPayload =
        """{"session":"-","customer":{"ownerUserId":"2321321"}}""";

    protected PayloadModesTestBase(VNextTestEnvironment environment) : base(environment) { }

    // ── the three payload shapes ─────────────────────────────────────────────

    /// <summary>
    /// Wraps a payload in the standard envelope. Built by concatenation rather than string
    /// interpolation so the JSON braces stay readable — an interpolated raw string would need
    /// escaping precisely where the shape matters most.
    /// </summary>
    protected static string Standard(string payloadJson, string? key = null) =>
        key is null
            ? "{\"attributes\":" + payloadJson + "}"
            : "{\"key\":\"" + key + "\",\"attributes\":" + payloadJson + "}";

    /// <summary>Envelope metadata with no <c>attributes</c> at all — still an envelope.</summary>
    protected static string EnvelopeOnly(string? key = null, bool withTags = false) =>
        "{\"key\":\"" + (key ?? Guid.NewGuid().ToString()) + "\"" + (withTags ? ",\"tags\":[\"a\"]" : "") + "}";

    /// <summary>Standard envelope carrying instance metadata alongside the payload.</summary>
    protected static string StandardWithKey(string key) => Standard(BusinessPayload, key);

    /// <summary>Standard envelope with no metadata — every envelope field is optional.</summary>
    protected static string StandardWithoutKey => Standard(BusinessPayload);

    /// <summary>Free-form: the body IS the payload, with no envelope around it.</summary>
    protected const string FreeForm = BusinessPayload;

    // ── requests ─────────────────────────────────────────────────────────────

    protected Task<(HttpStatusCode Status, string Body)> StartRawAsync(
        string json, IDictionary<string, string>? headers = null) =>
        SendRawJsonAsync(
            HttpMethod.Post,
            $"api/v1/core/workflows/{Workflow}/instances/start?sync=true",
            json,
            headers ?? Headers());

    protected Task<(HttpStatusCode Status, string Body)> SubmitRawAsync(
        string instanceId, string json, IDictionary<string, string>? headers = null) =>
        SendRawJsonAsync(
            HttpMethod.Patch,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/submit-payload?sync=true",
            json,
            headers ?? Headers());

    /// <summary>
    /// Starts an instance that is parked in <c>collect-payload</c>, ready for a transition test.
    /// Uses the free-form shape so the transition tests do not depend on the start-side result.
    /// </summary>
    protected async Task<string> StartForTransitionAsync()
    {
        var (status, body) = await StartRawAsync(FreeForm);
        Assert.True(status == HttpStatusCode.OK, $"arranging the instance failed with {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    // ── assertions ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the instance data holds exactly the business payload — the right values, and
    /// nothing else. The "nothing else" half is the point: when payload-mode detection gets it
    /// wrong the envelope is wrapped into the payload, and <c>key</c>/<c>tags</c>/<c>stage</c>
    /// show up here as if the caller had sent them as business data.
    /// </summary>
    protected static void AssertPayloadReflected(JsonElement attributes)
    {
        Assert.Equal(JsonValueKind.Object, attributes.ValueKind);

        Assert.Equal("-", attributes.GetProperty("session").GetString());
        Assert.Equal("2321321", attributes.GetProperty("customer").GetProperty("ownerUserId").GetString());

        var names = attributes.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "session", "customer" }, names);
    }

    /// <summary>
    /// Asserts the rejection really is schema validation, and that it says WHICH field failed.
    /// <para>
    /// The field name matters as much as the status: a 400 that names nothing leaves the client
    /// unable to point the user at anything. The runtime used to answer exactly that on this
    /// path — a root-level <c>required</c> failure was dropped while flattening the evaluation
    /// tree, so the body arrived with an empty error list.
    /// </para>
    /// </summary>
    protected static void AssertSchemaValidationFailure(string shape, string body, params string[] expectedFields)
    {
        Assert.True(body.Contains("900002", StringComparison.Ordinal),
            $"the {shape} payload was rejected, but not by schema validation: {body}");

        var errors = JsonDocument.Parse(body).RootElement
            .GetProperty("error").GetProperty("validationErrors");

        Assert.True(errors.GetArrayLength() > 0,
            $"the {shape} payload was rejected with no field-level detail at all: {body}");

        foreach (var field in expectedFields)
        {
            Assert.True(body.Contains(field, StringComparison.OrdinalIgnoreCase),
                $"the {shape} rejection never mentions '{field}': {body}");
        }
    }

    /// <summary>
    /// Asserts the runtime complained about the business payload, not about the envelope.
    /// <para>
    /// This is the regression pin. When payload-mode detection misfires, the envelope is wrapped
    /// into <c>attributes</c> and the schema is evaluated against <c>key</c>/<c>tags</c>/
    /// <c>stage</c> — which, under <c>additionalProperties: false</c>, surfaces as
    /// "All values fail against the false schema" naming those very fields.
    /// </para>
    /// </summary>
    protected static void AssertEnvelopeWasNotValidatedAsPayload(string shape, string body)
    {
        Assert.False(body.Contains("false schema", StringComparison.OrdinalIgnoreCase),
            $"the {shape} payload was validated as an envelope, not as the business payload: {body}");

        foreach (var envelopeField in new[] { "\"key\"", "\"tags\"", "\"stage\"", "\"attributes\"" })
        {
            Assert.False(body.Contains(envelopeField, StringComparison.Ordinal),
                $"the {shape} rejection names the envelope field {envelopeField} — the envelope " +
                $"leaked into the payload: {body}");
        }
    }

    /// <summary>Reads <c>attributes</c> off a start/transition response body.</summary>
    protected static JsonElement AttributesOf(string responseBody)
    {
        var root = JsonDocument.Parse(responseBody).RootElement;
        Assert.True(root.TryGetProperty("attributes", out var attributes),
            $"the response carried no attributes: {responseBody}");
        return attributes.Clone();
    }
}
