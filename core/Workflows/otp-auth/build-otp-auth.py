#!/usr/bin/env python3
"""
otp-auth SubFlow uretici — src/*.csx dosyalarini yazar, sonra bunlari base64 gomulu
workflow JSON'larina cevirir. Repo deseni: build-chain-busy.py / build-role-matrix-lab.py.

Uretilenler
-----------
  src/*.csx                 mapping / rule / timer script'leri (diskteki kaynak = tek dogruluk)
  otp-auth.json             SubFlow (attributes.type "S") — 19 state
  otp-auth-harness.json     stateType 4 ile subflow'u tuketen test parent'i

Tasarim: "gate discriminator". Her mapping'in OutputHandler'i instance data'ya TEK bir string
gate alani yazar (profileGate / sendGate / verifyGate / resendGate / authGate) ve switch'i
TOTAL'dir — taninmayan her durum (catch dahil) "failed" yazar. Rule'lar yalnizca esitlik
kontrolu yapar. Boylece auto-transition rule'lari matematiksel olarak ortusmez ve bosta dal
kalmaz; ruled kardeslerin yanina triggerKind 10 fallback koymaya gerek kalmaz.

DIKKAT — task journal `(TransitionId, TaskId)` uzerinden tekildir; AYNI transition icinde ayni
task TANIMINI iki kez kullanma (ikincisi sessizce atlanir). Bu akista her transition bir task
tanimini en fazla bir kez kullanir. Ayni transition'in TEKRAR gecilmesi sorun degil —
secret-cache-lab'in probe-idle self-loop'u bunu kanitlar.
"""

import base64
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "src")

DOMAIN = "core"

SCRIPT_TASK = {"key": "otp-script-task", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-tasks"}
PROFILE_TASK = {"key": "get-simple-profile", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-tasks"}
SEND_TASK = {"key": "send-otp", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-tasks"}
VALIDATE_TASK = {"key": "validate-otp", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-tasks"}
AUTHCODE_TASK = {"key": "issue-authorization-code", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-tasks"}

HELPERS = {"key": "otp-auth-helpers", "version": "1.0.0", "domain": DOMAIN, "flow": "sys-mappings"}

SCHEMA_MASTER = {"key": "otp-auth-master", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-schemas"}
SCHEMA_START = {"key": "otp-auth-start-payload", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-schemas"}
SCHEMA_CODE = {"key": "otp-code-payload", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-schemas"}

VIEW_CODE_ENTRY = {"key": "otp-code-entry-view", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-views"}
VIEW_RESEND = {"key": "otp-resend-view", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-views"}

# --------------------------------------------------------------------------------------
# Ortak CSX parcalari
# --------------------------------------------------------------------------------------

USINGS = """using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
"""

RULE_TEMPLATE = USINGS + """
/// <summary>{doc}</summary>
public class {cls} : ScriptBase, IConditionMapping
{{
    public Task<bool> Handler(ScriptContext context)
    {{
        return Task.FromResult(OtpAuthHelpers.GateIs(context.Instance?.Data, "{gate}", "{value}"));
    }}
}}
"""

RULES = [
    ("ProfileGatePassRule", "profileGate", "pass",
     "Profile step cleared: Kurumsal always passes, Bireysel needs a record whose phone matches."),
    ("ProfileGateNotFoundRule", "profileGate", "not-found",
     "Bireysel caller has no simple-profile record (404/400)."),
    ("ProfileGateMismatchRule", "profileGate", "phone-mismatch",
     "Bireysel caller's supplied phone differs from the profile phone."),
    ("ProfileGateFailedRule", "profileGate", "failed",
     "Profile step could not be interpreted; route to the technical-failure end state."),
    ("SendGateSentRule", "sendGate", "sent",
     "OTP service answered SendOtpSuccess."),
    ("SendGateEscalateFastRule", "sendGate", "escalate-fast",
     "SIM blocked on a Kurumsal caller with the latch still open: retry over FAST."),
    ("SendGateBlockedRule", "sendGate", "blocked",
     "SIM blocked with no FAST escape left: Bireysel caller, or the latch was already set."),
    ("SendGateFailedRule", "sendGate", "failed",
     "OTP send returned an unrecognised status."),
    ("VerifyGateVerifiedRule", "verifyGate", "verified",
     "OTP service confirmed the code."),
    ("VerifyGateRetryRule", "verifyGate", "retry",
     "Wrong code but attempts remain; return to the entry state."),
    ("VerifyGateExceededRule", "verifyGate", "exceeded",
     "Wrong code and the attempt allowance is now spent."),
    ("VerifyGateExpiredRule", "verifyGate", "expired",
     "Service reports the code expired; hand over to the resend gate."),
    ("VerifyGateFailedRule", "verifyGate", "failed",
     "Validation returned an unrecognised status."),
    ("ResendGateAllowedRule", "resendGate", "allowed",
     "Resend allowance remains; offer the user a manual resend."),
    ("ResendGateExhaustedRule", "resendGate", "exhausted",
     "No resend allowance left; end the flow as timed out."),
    ("AuthGateIssuedRule", "authGate", "issued",
     "Authorization code was issued."),
    ("AuthGateFailedRule", "authGate", "failed",
     "Authorization code could not be issued."),
]

HARNESS_RULES = [
    ("OtpAuthSucceededRule", "otpAuthResult", "success",
     "SubFlow finished on its success end state."),
]

# --------------------------------------------------------------------------------------
# Mapping CSX'leri  (IMapping / ITimerMapping / ISubFlowMapping)
# --------------------------------------------------------------------------------------

MAPPINGS = {}

MAPPINGS["OtpAuthStartMapping"] = USINGS + """
/// <summary>
/// Start transition bookkeeping. Normalises the caller-supplied payload into the derived fields the
/// rest of the flow relies on, and seeds every counter and the SIM-block latch.
/// <para>Runs on a script task, so InputHandler has nothing to prepare.</para>
/// </summary>
public class OtpAuthStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);

        var phone = OtpAuthHelpers.Str(data, "user_phone");
        data["phonePrefix"] = OtpAuthHelpers.PhonePrefix(phone);
        data["phoneNumber"] = OtpAuthHelpers.PhoneSubscriber(phone);
        data["phoneCountryCode"] = "90";

        // Caller-tunable knobs; the schema defaults are re-applied here because a SubFlow start
        // payload may legitimately omit them.
        data["otpAttempt"] = OtpAuthHelpers.Int(data, "otpAttempt", 3);
        data["otpTtl"] = OtpAuthHelpers.Int(data, "otpTtl", 180);
        data["resendLimit"] = OtpAuthHelpers.Int(data, "resendLimit", 3);

        data["attemptUsed"] = 0;
        data["resendUsed"] = 0;
        data["simBlocked"] = false;
        data["otpType"] = "Otp";
        data["otpVerified"] = false;
        data["otpAuthResult"] = "in-progress";
        data["otpAuthResultDetail"] = "";

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["GetSimpleProfileMapping"] = USINGS + """
/// <summary>
/// simple-profile lookup by <c>actor</c>, plus the profileGate decision.
/// <para>
/// MockLab has no route-parameter support, so the customer id travels as a query string rather than
/// as a path segment (see VNEXT-BUILD-PLAN.md risk R12).
/// </para>
/// <para>
/// 404 and 400 mean "no record", not a transport error - the Asgard client this contract came from
/// returns null for both. They are handled here when the runtime hands the response to the mapping,
/// and by the state's errorBoundary when the runtime raises them instead.
/// </para>
/// </summary>
public class GetSimpleProfileMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        ArgumentNullException.ThrowIfNull(httpTask);

        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        var actor = OtpAuthHelpers.Str(data, "actor", string.Empty);

        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl) + "?customerId=" + Uri.EscapeDataString(actor));

        // Lab secret store may not carry the key at all; an absent secret must not break the call.
        var apiKey = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
        httpTask.AddHeader("X-APISIX-KEY", apiKey ?? string.Empty);

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var scopeGroup = OtpAuthHelpers.Str(data, "scopeGroup", "Bireysel");
        var isKurumsal = string.Equals(scopeGroup, "Kurumsal", StringComparison.Ordinal);

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var statusCode = OtpAuthHelpers.Int(envelope, "statusCode", 200);
            var payload = envelope.ContainsKey("data")
                ? OtpAuthHelpers.ToDict(envelope["data"])
                : envelope;

            if (statusCode == 404 || statusCode == 400)
            {
                Apply(data, isKurumsal, false, null);
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (statusCode >= 500)
            {
                data["profileGate"] = "failed";
                data["otpAuthResult"] = "technical-failure";
                data["otpAuthResultDetail"] = "simple-profile returned " + statusCode;
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            var profilePhone = OtpAuthHelpers.Str(payload, "phoneNumber");
            data["profileEmail"] = OtpAuthHelpers.Str(payload, "email", string.Empty);
            Apply(data, isKurumsal, profilePhone != null, profilePhone);
        }
        catch (Exception ex)
        {
            // Total switch: an unreadable response must still leave a gate value behind, otherwise
            // no transition rule matches and the instance hangs silently.
            data["profileGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "simple-profile parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }

    /// <summary>
    /// Kurumsal treats the profile as informational only: neither a missing record nor a phone
    /// mismatch stops the flow, because the caller supplies the phone for the legal entity's user.
    /// </summary>
    private static void Apply(IDictionary<string, object> data, bool isKurumsal, bool found, string profilePhone)
    {
        data["profileFound"] = found;
        data["profilePhone"] = profilePhone ?? string.Empty;

        if (isKurumsal)
        {
            data["profileGate"] = "pass";
            return;
        }

        if (!found)
        {
            data["profileGate"] = "not-found";
            data["otpAuthResult"] = "profile-not-found";
            data["otpAuthResultDetail"] = "No simple-profile record for the actor";
            return;
        }

        if (!OtpAuthHelpers.PhonesMatch(OtpAuthHelpers.Str(data, "user_phone"), profilePhone))
        {
            data["profileGate"] = "phone-mismatch";
            data["otpAuthResult"] = "phone-mismatch";
            data["otpAuthResultDetail"] = "Supplied phone does not match the profile phone";
            return;
        }

        data["profileGate"] = "pass";
    }
}
"""

MAPPINGS["ProfileNotFoundFallbackMapping"] = USINGS + """
/// <summary>
/// Runs on the errorBoundary path when the runtime raises 404/400 instead of handing the response to
/// GetSimpleProfileMapping. Recomputes profileGate with the same scope-aware rule so both routes
/// converge on identical behaviour.
/// </summary>
public class ProfileNotFoundFallbackMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var isKurumsal = string.Equals(OtpAuthHelpers.Str(data, "scopeGroup", "Bireysel"), "Kurumsal", StringComparison.Ordinal);

        data["profileFound"] = false;
        data["profilePhone"] = string.Empty;

        if (isKurumsal)
        {
            data["profileGate"] = "pass";
        }
        else
        {
            data["profileGate"] = "not-found";
            data["otpAuthResult"] = "profile-not-found";
            data["otpAuthResultDetail"] = "No simple-profile record for the actor";
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["SendOtpMapping"] = USINGS + """
/// <summary>
/// OTP send, shared by the otp-sending and otp-resending-fast states, and the home of the
/// SIM-block LATCH.
/// <para>
/// The latch is the whole reason one mapping serves both states. InputHandler always sends
/// <c>OtpType = data.otpType</c>. The first OtpBlacklisted answer for a Kurumsal caller flips
/// otpType to "Fast" and sets simBlocked. Because the escalate-fast gate additionally requires
/// <c>simBlocked == false</c>, a second block can only produce "blocked" - so FAST is attempted at
/// most once, and plain "Otp" is never attempted again for the life of the instance.
/// </para>
/// <para>Bireysel never satisfies the escalation condition, so FAST is unreachable for it by
/// construction rather than by a guard that could be forgotten.</para>
/// </summary>
public class SendOtpMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        ArgumentNullException.ThrowIfNull(httpTask);

        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));

        var apiKey = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
        httpTask.AddHeader("X-APISIX-KEY", apiKey ?? string.Empty);

        // CitizenshipNumber is the ACTOR, not sub: for a Kurumsal caller sub is the legal entity
        // number, while the SMS goes to the natural person acting on its behalf.
        httpTask.SetBody(new
        {
            Phone = new
            {
                Number = OtpAuthHelpers.Str(data, "phoneNumber", string.Empty),
                Prefix = OtpAuthHelpers.Str(data, "phonePrefix", string.Empty),
                CountryCode = OtpAuthHelpers.Str(data, "phoneCountryCode", "90")
            },
            CitizenshipNumber = OtpAuthHelpers.Str(data, "actor", string.Empty),
            OtpRetryAttempt = OtpAuthHelpers.Int(data, "otpAttempt", 3),
            OtpDuration = OtpAuthHelpers.Int(data, "otpTtl", 180),
            Language = OtpAuthHelpers.Str(data, "language", "tr-TR"),
            MessageTemplate = OtpAuthHelpers.Str(data, "messageTemplate"),
            OtpType = OtpAuthHelpers.Str(data, "otpType", "Otp"),
            InstanceId = context.Instance?.Id.ToString(),
            SmsSender = OtpAuthHelpers.Str(data, "smsSender"),
            AppType = OtpAuthHelpers.Str(data, "appType"),
            ProcessName = "otp-auth",
            ProcessIdentity = OtpAuthHelpers.Str(data, "actor", string.Empty),
            Tags = (object)null
        });

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var payload = envelope.ContainsKey("data") ? OtpAuthHelpers.ToDict(envelope["data"]) : envelope;
            var sendInfo = payload.ContainsKey("sendInfo") ? OtpAuthHelpers.ToDict(payload["sendInfo"]) : new Dictionary<string, object>();

            var status = OtpAuthHelpers.Str(payload, "sendOtpStatus", string.Empty);
            data["sendOtpStatus"] = status;
            data["smsTraceId"] = OtpAuthHelpers.Str(sendInfo, "smsTraceId", string.Empty);

            if (string.Equals(status, "SendOtpSuccess", StringComparison.Ordinal))
            {
                var ttl = OtpAuthHelpers.Int(data, "otpTtl", 180);
                data["otpReference"] = OtpAuthHelpers.Str(payload, "otpReference", string.Empty);
                data["otpSentAt"] = OtpAuthHelpers.UtcNow();
                // Absolute deadline: the TTL timer derives its duration from this, so retrying a
                // wrong code cannot buy the user a fresh validity window.
                data["otpExpiresAt"] = OtpAuthHelpers.UtcIn(ttl);
                data["sendGate"] = "sent";
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (string.Equals(status, "OtpBlacklisted", StringComparison.Ordinal))
            {
                var isKurumsal = string.Equals(OtpAuthHelpers.Str(data, "scopeGroup", "Bireysel"), "Kurumsal", StringComparison.Ordinal);
                var alreadyLatched = OtpAuthHelpers.Bool(data, "simBlocked", false);

                if (isKurumsal && !alreadyLatched)
                {
                    data["simBlocked"] = true;
                    data["simBlockedAt"] = OtpAuthHelpers.UtcNow();
                    data["otpType"] = "Fast";
                    data["sendGate"] = "escalate-fast";
                    return Task.FromResult(new ScriptResponse { Data = data });
                }

                data["simBlocked"] = true;
                data["sendGate"] = "blocked";
                data["otpAuthResult"] = "sim-blocked";
                data["otpAuthResultDetail"] = OtpAuthHelpers.Str(sendInfo, "message", "SIM blocked for OTP delivery");
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            data["sendGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "Unexpected sendOtpStatus: " + status;
        }
        catch (Exception ex)
        {
            data["sendGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "send-otp parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["ValidateOtpMapping"] = USINGS + """
/// <summary>
/// OTP verification and the attempt counter.
/// <para>
/// The wrong-code / allowance-spent split is decided here rather than in a rule, so the transition
/// rules stay pure equality checks and cannot disagree with each other.
/// </para>
/// <para>
/// The submitted code is dropped from instance data on the way out: a transition payload is merged
/// into the instance root, so leaving it in place would persist the one-time code.
/// </para>
/// </summary>
public class ValidateOtpMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        ArgumentNullException.ThrowIfNull(httpTask);

        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));

        var apiKey = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
        httpTask.AddHeader("X-APISIX-KEY", apiKey ?? string.Empty);

        httpTask.SetBody(new
        {
            OtpCode = OtpAuthHelpers.Str(data, "otpCode", string.Empty),
            OtpReference = OtpAuthHelpers.Str(data, "otpReference", string.Empty),
            CitizenshipNumber = OtpAuthHelpers.Str(data, "actor", string.Empty),
            InstanceId = context.Instance?.Id.ToString()
        });

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpCode"] = null;

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var payload = envelope.ContainsKey("data") ? OtpAuthHelpers.ToDict(envelope["data"]) : envelope;
            var status = OtpAuthHelpers.Str(payload, "validateOtpStatus", string.Empty);

            if (string.Equals(status, "ValidateOtpSuccess", StringComparison.Ordinal))
            {
                data["otpVerified"] = true;
                data["verifyGate"] = "verified";
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (string.Equals(status, "ExpiredOtp", StringComparison.Ordinal))
            {
                data["verifyGate"] = "expired";
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (string.Equals(status, "InvalidOtp", StringComparison.Ordinal))
            {
                // The allowance is per instance and is never reset by a resend.
                var used = OtpAuthHelpers.Int(data, "attemptUsed", 0) + 1;
                var allowed = OtpAuthHelpers.Int(data, "otpAttempt", 3);
                data["attemptUsed"] = used;

                if (used >= allowed)
                {
                    data["verifyGate"] = "exceeded";
                    data["otpAuthResult"] = "attempt-exceeded";
                    data["otpAuthResultDetail"] = "Wrong code entered " + used + " time(s)";
                }
                else
                {
                    data["verifyGate"] = "retry";
                }

                return Task.FromResult(new ScriptResponse { Data = data });
            }

            data["verifyGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "Unexpected validateOtpStatus: " + status;
        }
        catch (Exception ex)
        {
            data["verifyGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "validate-otp parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["IssueAuthorizationCodeMapping"] = USINGS + """
/// <summary>
/// Exchanges the verified OTP for an authorization code.
/// <para>
/// CONTRACT NOT ANCHORED - the real issuer endpoint and field names were not confirmed during
/// planning (VNEXT-BUILD-PLAN.md section 9, S2). The request and response shapes below match the
/// MockLab fixture and must be revisited before this runs against a real service.
/// </para>
/// </summary>
public class IssueAuthorizationCodeMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        ArgumentNullException.ThrowIfNull(httpTask);

        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));

        var apiKey = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
        httpTask.AddHeader("X-APISIX-KEY", apiKey ?? string.Empty);

        httpTask.SetBody(new
        {
            client_id = OtpAuthHelpers.Str(data, "client_id", string.Empty),
            client_secret = OtpAuthHelpers.Str(data, "client_secret", string.Empty),
            grant_type = OtpAuthHelpers.Str(data, "grant_type", string.Empty),
            scopeGroup = OtpAuthHelpers.Str(data, "scopeGroup", string.Empty),
            sub = OtpAuthHelpers.Str(data, "sub", string.Empty),
            actor = OtpAuthHelpers.Str(data, "actor", string.Empty),
            otpReference = OtpAuthHelpers.Str(data, "otpReference", string.Empty)
        });

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var statusCode = OtpAuthHelpers.Int(envelope, "statusCode", 200);
            var payload = envelope.ContainsKey("data") ? OtpAuthHelpers.ToDict(envelope["data"]) : envelope;
            var code = OtpAuthHelpers.Str(payload, "authorizationCode");

            if (statusCode < 400 && code != null)
            {
                data["authorizationCode"] = code;
                data["authGate"] = "issued";
                data["otpAuthResult"] = "success";
                data["otpAuthResultDetail"] = string.Empty;
                // The secret has served its purpose; do not carry it into the success payload.
                data["client_secret"] = null;
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            data["authorizationCode"] = string.Empty;
            data["authGate"] = "failed";
            data["otpAuthResult"] = "auth-code-failed";
            data["otpAuthResultDetail"] = "Authorization code request returned " + statusCode;
        }
        catch (Exception ex)
        {
            data["authorizationCode"] = string.Empty;
            data["authGate"] = "failed";
            data["otpAuthResult"] = "auth-code-failed";
            data["otpAuthResultDetail"] = "authorization-code parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["EvaluateResendGateMapping"] = USINGS + """
/// <summary>
/// Decides, after a TTL expiry, whether the user still gets a manual resend or the flow ends.
/// Keeping the comparison here means the two exit rules stay pure equality checks.
/// </summary>
public class EvaluateResendGateMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var used = OtpAuthHelpers.Int(data, "resendUsed", 0);
        var limit = OtpAuthHelpers.Int(data, "resendLimit", 3);

        data["resendGate"] = used < limit ? "allowed" : "exhausted";
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["IncrementResendMapping"] = USINGS + """
/// <summary>
/// Consumes one manual resend. Deliberately does NOT touch attemptUsed: the wrong-entry allowance is
/// per instance, so a resend must not hand the user a fresh set of guesses.
/// </summary>
public class IncrementResendMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["resendUsed"] = OtpAuthHelpers.Int(data, "resendUsed", 0) + 1;
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

RESULT_SETTER = USINGS + """
/// <summary>{doc}</summary>
public class {cls} : ScriptBase, IMapping
{{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {{
        return Task.FromResult(new ScriptResponse());
    }}

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {{
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpAuthResult"] = "{result}";
        data["otpAuthResultDetail"] = "{detail}";
        return Task.FromResult(new ScriptResponse {{ Data = data }});
    }}
}}
"""

RESULT_SETTERS = [
    ("SetTtlExpiredResultMapping", "ttl-expired", "OTP expired and no resend allowance remained",
     "Stamps the timeout outcome on the paths the gate mappings do not cover."),
    ("SetCancelledResultMapping", "cancelled", "Cancelled by the caller",
     "Stamps the cancelled outcome on the workflow-level cancel transition."),
    ("SetTechnicalFailureResultMapping", "technical-failure", "Upstream service failure",
     "Stamps the technical-failure outcome on errorBoundary transitions."),
    ("SetAuthCodeFailedResultMapping", "auth-code-failed", "Authorization code service failure",
     "Stamps the auth-code failure outcome on the issuer's errorBoundary transition."),
]

MAPPINGS["OtpTtlTimer"] = """using System;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;

/// <summary>
/// TTL timer for otp-awaiting-code, rebuilt on every entry into that state.
/// <para>
/// The duration is derived from the ABSOLUTE <c>otpExpiresAt</c> stamped when the OTP was sent, not
/// from otpTtl directly. A fixed FromDuration(otpTtl) would restart the clock on every wrong-code
/// round trip, keeping the flow alive long after the SMS code itself had died.
/// </para>
/// </summary>
public class OtpTtlTimer : ScriptBase, ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var ttl = OtpAuthHelpers.Int(data, "otpTtl", 180);
        var expiresAt = OtpAuthHelpers.ParseUtc(OtpAuthHelpers.Str(data, "otpExpiresAt"));

        var remaining = expiresAt.HasValue
            ? expiresAt.Value - DateTime.UtcNow
            : TimeSpan.FromSeconds(ttl);

        // Two seconds of grace so a verify-otp call racing the timer usually wins; if it loses, the
        // server-side expired branch reaches the same gate anyway.
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.FromSeconds(2);
        }

        if (remaining > TimeSpan.FromSeconds(ttl))
        {
            remaining = TimeSpan.FromSeconds(ttl);
        }

        return Task.FromResult(TimerSchedule.FromDuration(remaining));
    }
}
"""

MAPPINGS["AbandonTimer"] = """using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;

/// <summary>
/// Abandonment guard for otp-awaiting-resend: if the user never comes back to press "resend", the
/// instance must not stay open forever.
/// </summary>
public class AbandonTimer : ScriptBase, ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromMinutes(5)));
    }
}
"""

MAPPINGS["HostToOtpAuthSubFlowMapping"] = USINGS + """
/// <summary>
/// Parent side of the SubFlow contract.
/// <para>
/// InputHandler projects only the start-payload fields, so the child never receives unrelated parent
/// state. OutputHandler merges back only the agreed outcome fields, and deliberately leaves
/// client_secret and otpCode behind.
/// </para>
/// </summary>
public class HostToOtpAuthSubFlowMapping : ScriptBase, ISubFlowMapping
{
    private static readonly string[] Inbound =
    {
        "sub", "actor", "client_id", "grant_type", "client_secret",
        "user_phone", "user_email", "user_name", "user_surname",
        "scopeGroup", "otpAttempt", "otpTtl", "resendLimit"
    };

    private static readonly string[] Outbound =
    {
        "otpAuthResult", "otpAuthResultDetail", "authorizationCode",
        "otpReference", "attemptUsed", "resendUsed", "simBlocked"
    };

    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var source = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var payload = new Dictionary<string, object>();

        foreach (var field in Inbound)
        {
            if (source.TryGetValue(field, out var value) && value != null)
            {
                payload[field] = value;
            }
        }

        return Task.FromResult(new ScriptResponse { Data = payload });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var child = OtpAuthHelpers.ToDict(context.Body);

        foreach (var field in Outbound)
        {
            if (child.TryGetValue(field, out var value))
            {
                data[field] = value;
            }
        }

        if (!data.ContainsKey("otpAuthResult") || data["otpAuthResult"] == null)
        {
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "SubFlow returned no outcome";
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
"""

MAPPINGS["HarnessStartMapping"] = USINGS + """
/// <summary>Test harness only: passes the start payload straight through to the SubFlow state.</summary>
public class HarnessStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = OtpAuthHelpers.Merge(context.Instance?.Data) });
    }
}
"""

MAPPINGS["OtpAuthFailedRule"] = USINGS + """
/// <summary>
/// Harness fallback branch: everything the SubFlow can end on that is not "success".
/// Paired with OtpAuthSucceededRule so the two are total and mutually exclusive.
/// </summary>
public class OtpAuthFailedRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(!OtpAuthHelpers.GateIs(context.Instance?.Data, "otpAuthResult", "success"));
    }
}
"""

# --------------------------------------------------------------------------------------
# CSX yazimi + JSON insa yardimcilari
# --------------------------------------------------------------------------------------


def write_csx():
    os.makedirs(SRC, exist_ok=True)
    written = []

    for cls, body in MAPPINGS.items():
        with open(os.path.join(SRC, cls + ".csx"), "w") as fh:
            fh.write(body)
        written.append(cls + ".csx")

    for cls, result, detail, doc in RESULT_SETTERS:
        with open(os.path.join(SRC, cls + ".csx"), "w") as fh:
            fh.write(RESULT_SETTER.format(cls=cls, result=result, detail=detail, doc=doc))
        written.append(cls + ".csx")

    for cls, gate, value, doc in RULES + HARNESS_RULES:
        with open(os.path.join(SRC, cls + ".csx"), "w") as fh:
            fh.write(RULE_TEMPLATE.format(cls=cls, gate=gate, value=value, doc=doc))
        written.append(cls + ".csx")

    return sorted(written)


def code(name):
    with open(os.path.join(SRC, name), "rb") as fh:
        return base64.b64encode(fh.read()).decode()


def ref(name):
    """scriptCode blogu — repo deseni: type L (local), base64 gomulu."""
    return {"type": "L", "location": "./src/" + name, "code": code(name), "encoding": "B64"}


def label(en, tr):
    return [{"language": "en-US", "label": en}, {"language": "tr-TR", "label": tr}]


def view_of(view_ref):
    return {"view": dict(view_ref), "loadData": True}


def exec_task(mapping_file, task_def=None, order=1):
    return {"order": order, "task": dict(task_def or SCRIPT_TASK), "mapping": ref(mapping_file)}


def entry(mapping_file, task_def, order=1):
    return {"order": order, "task": dict(task_def), "mapping": ref(mapping_file)}


def auto(key, target, en, tr, rule_file):
    """Ruled otomatik gecis. Kardesleriyle birlikte TOTAL bir gate uzerinde calisir."""
    return {
        "key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor",
        "labels": label(en, tr), "schema": None, "rule": ref(rule_file),
        "timer": None, "view": None, "onExecutionTasks": [],
    }


def auto_default(key, target, en, tr):
    """Kosulsuz tek cikis — triggerKind 10 yalnizca burada mesru (rule'lu kardesi yok)."""
    return {
        "key": key, "target": target, "triggerType": 1, "triggerKind": 10,
        "versionStrategy": "Minor", "labels": label(en, tr), "schema": None,
        "rule": None, "timer": None, "view": None, "onExecutionTasks": [],
    }


def manual(key, target, en, tr, schema=None, tasks=None):
    return {
        "key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor",
        "labels": label(en, tr), "schema": dict(schema) if schema else None,
        "rule": None, "timer": None, "view": None,
        "onExecutionTasks": tasks or [],
    }


def boundary_transition(key, target, en, tr, tasks=None):
    """errorBoundary'nin Notify ile tetikledigi gecis — manual (triggerType 0)."""
    return manual(key, target, en, tr, tasks=tasks)


def timer_transition(key, target, en, tr, timer_file, tasks=None):
    return {
        "key": key, "target": target, "triggerType": 2, "versionStrategy": "Minor",
        "labels": label(en, tr), "schema": None, "rule": None,
        "timer": ref(timer_file), "view": None, "onExecutionTasks": tasks or [],
    }


def state(key, state_type, sub_type, en, tr, transitions,
          view=None, on_entries=None, on_exits=None, error_boundary=None, subflow=None):
    node = {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Minor",
        "labels": label(en, tr),
        "view": view,
        "subFlow": subflow,
        "onEntries": on_entries or [],
        "onExits": on_exits or [],
        "transitions": transitions,
    }
    if error_boundary:
        node["errorBoundary"] = error_boundary
    return node


def final(key, sub_type, en, tr):
    return state(key, 3, sub_type, en, tr, [])


def notify(transition_key, priority, codes=None):
    rule = {"action": 4, "transition": transition_key, "priority": priority}
    rule["errorCodes"] = codes or ["*"]
    return rule

# --------------------------------------------------------------------------------------
# State machine
# --------------------------------------------------------------------------------------


def auto_t(key, target, en, tr, rule_file, tasks=None):
    node = auto(key, target, en, tr, rule_file)
    node["onExecutionTasks"] = tasks or []
    return node


def build_states():
    # profile-lookup ve profile-gate-recheck ayni dort gate degerini ayni rule dosyalariyla
    # degerlendirir; ikisi de ayni hedeflere gider. recheck, runtime 404/400'u mapping'e degil
    # errorBoundary'ye dusurdugunde devreye girer.
    def profile_branches(prefix):
        return [
            auto_t(prefix + "-pass", "otp-sending",
                   "Profile check passed", "Profil kontrolu gecti", "ProfileGatePassRule.csx"),
            auto_t(prefix + "-missing", "profile-not-found",
                   "No profile record", "Profil kaydi yok", "ProfileGateNotFoundRule.csx"),
            auto_t(prefix + "-phone-mismatch", "phone-mismatch",
                   "Phone does not match profile", "Telefon profille uyusmuyor", "ProfileGateMismatchRule.csx"),
            auto_t(prefix + "-gate-failed", "otp-technical-failure",
                   "Profile step failed", "Profil adimi basarisiz", "ProfileGateFailedRule.csx"),
        ]

    states = []

    states.append(state(
        "otp-initializing", 1, 0, "Initializing", "Baslatiliyor",
        [auto_default("init-to-profile-lookup", "profile-lookup",
                      "Continue to profile lookup", "Profil sorgusuna gec")]))

    states.append(state(
        "profile-lookup", 2, 5, "Profile Lookup", "Profil Sorgusu",
        profile_branches("profile") + [
            boundary_transition("profile-lookup-not-found", "profile-gate-recheck",
                                "Profile lookup returned not found", "Profil sorgusu kayit bulamadi",
                                tasks=[exec_task("ProfileNotFoundFallbackMapping.csx")]),
            boundary_transition("profile-lookup-technical-failure", "otp-technical-failure",
                                "Profile lookup failed", "Profil sorgusu hata verdi",
                                tasks=[exec_task("SetTechnicalFailureResultMapping.csx")]),
        ],
        on_entries=[entry("GetSimpleProfileMapping.csx", PROFILE_TASK)],
        error_boundary={"onError": [
            # 404/400 are a business outcome ("no record"), not a transport failure.
            notify("profile-lookup-not-found", 10, ["404", "400", "Task:404", "Task:400"]),
            notify("profile-lookup-technical-failure", 100, ["*"]),
        ]}))

    states.append(state(
        "profile-gate-recheck", 2, 0, "Profile Gate Recheck", "Profil Kapisi Yeniden Degerlendirme",
        profile_branches("recheck")))

    states.append(state(
        "otp-sending", 2, 5, "Sending OTP", "OTP Gonderiliyor",
        [
            auto_t("send-otp-sent", "otp-awaiting-code",
                   "OTP sent", "OTP gonderildi", "SendGateSentRule.csx"),
            auto_t("send-otp-escalate-fast", "otp-resending-fast",
                   "SIM blocked, escalate to FAST", "SIM bloke, FAST'e yukselt", "SendGateEscalateFastRule.csx"),
            auto_t("send-otp-blocked", "sim-blocked",
                   "SIM blocked, no fallback left", "SIM bloke, alternatif kalmadi", "SendGateBlockedRule.csx"),
            auto_t("send-otp-failed", "otp-technical-failure",
                   "OTP send failed", "OTP gonderimi basarisiz", "SendGateFailedRule.csx"),
            boundary_transition("send-otp-technical-failure", "otp-technical-failure",
                                "OTP send errored", "OTP gonderimi hata verdi",
                                tasks=[exec_task("SetTechnicalFailureResultMapping.csx")]),
        ],
        on_entries=[entry("SendOtpMapping.csx", SEND_TASK)],
        error_boundary={"onError": [notify("send-otp-technical-failure", 100, ["*"])]}))

    # DIKKAT: burada send-otp-escalate-fast'in karsiligi BILINCLI olarak yok. Latch zaten kuruldugu
    # icin gate "escalate-fast" uretemez; yine de tanimlanirsa sonsuz FAST dongusu acilir.
    states.append(state(
        "otp-resending-fast", 2, 5, "Resending OTP over FAST", "OTP FAST ile Yeniden Gonderiliyor",
        [
            auto_t("fast-otp-sent", "otp-awaiting-code",
                   "FAST OTP sent", "FAST OTP gonderildi", "SendGateSentRule.csx"),
            auto_t("fast-otp-blocked", "sim-blocked",
                   "FAST also blocked", "FAST da bloke", "SendGateBlockedRule.csx"),
            auto_t("fast-otp-failed", "otp-technical-failure",
                   "FAST send failed", "FAST gonderimi basarisiz", "SendGateFailedRule.csx"),
            boundary_transition("fast-otp-technical-failure", "otp-technical-failure",
                                "FAST send errored", "FAST gonderimi hata verdi",
                                tasks=[exec_task("SetTechnicalFailureResultMapping.csx")]),
        ],
        on_entries=[entry("SendOtpMapping.csx", SEND_TASK)],
        error_boundary={"onError": [notify("fast-otp-technical-failure", 100, ["*"])]}))

    states.append(state(
        "otp-awaiting-code", 2, 6, "Awaiting OTP Code", "OTP Kodu Bekleniyor",
        [
            manual("verify-otp", "otp-verifying",
                   "Verify OTP code", "OTP kodunu dogrula", schema=SCHEMA_CODE),
            timer_transition("otp-ttl-expired", "otp-ttl-gate",
                             "OTP validity expired", "OTP suresi doldu", "OtpTtlTimer.csx",
                             tasks=[exec_task("EvaluateResendGateMapping.csx")]),
        ],
        view=view_of(VIEW_CODE_ENTRY)))

    states.append(state(
        "otp-verifying", 2, 5, "Verifying OTP", "OTP Dogrulaniyor",
        [
            auto_t("otp-verified", "auth-code-issuing",
                   "OTP verified", "OTP dogrulandi", "VerifyGateVerifiedRule.csx"),
            auto_t("otp-retry-available", "otp-awaiting-code",
                   "Wrong code, attempts remain", "Yanlis kod, hak var", "VerifyGateRetryRule.csx"),
            auto_t("otp-attempt-exceeded", "attempt-exceeded",
                   "Attempt allowance spent", "Deneme hakki bitti", "VerifyGateExceededRule.csx"),
            # Sunucu "expired" derse de ayni resend kapisina gireriz; bu yuzden gate task'i burada da var.
            auto_t("otp-code-expired", "otp-ttl-gate",
                   "Server reports code expired", "Sunucu kodu suresi dolmus diyor",
                   "VerifyGateExpiredRule.csx", tasks=[exec_task("EvaluateResendGateMapping.csx")]),
            auto_t("otp-verify-failed", "otp-technical-failure",
                   "Verification failed", "Dogrulama basarisiz", "VerifyGateFailedRule.csx"),
            boundary_transition("verify-technical-failure", "otp-technical-failure",
                                "Verification errored", "Dogrulama hata verdi",
                                tasks=[exec_task("SetTechnicalFailureResultMapping.csx")]),
        ],
        on_entries=[entry("ValidateOtpMapping.csx", VALIDATE_TASK)],
        error_boundary={"onError": [notify("verify-technical-failure", 100, ["*"])]}))

    states.append(state(
        "otp-ttl-gate", 2, 0, "Resend Gate", "Tekrar Gonderim Kapisi",
        [
            auto_t("ttl-resend-allowed", "otp-awaiting-resend",
                   "Resend allowance remains", "Tekrar gonderim hakki var", "ResendGateAllowedRule.csx"),
            auto_t("ttl-resend-exhausted", "ttl-expired",
                   "No resend allowance left", "Tekrar gonderim hakki bitti", "ResendGateExhaustedRule.csx",
                   tasks=[exec_task("SetTtlExpiredResultMapping.csx")]),
        ]))

    states.append(state(
        "otp-awaiting-resend", 2, 6, "Awaiting Resend Request", "Tekrar Gonderim Bekleniyor",
        [
            manual("resend-otp", "otp-sending",
                   "Send a new OTP", "Yeni OTP gonder",
                   tasks=[exec_task("IncrementResendMapping.csx")]),
            timer_transition("resend-abandoned", "ttl-expired",
                             "User abandoned the resend step", "Kullanici tekrar gonderimi terk etti",
                             "AbandonTimer.csx",
                             tasks=[exec_task("SetTtlExpiredResultMapping.csx")]),
        ],
        view=view_of(VIEW_RESEND)))

    states.append(state(
        "auth-code-issuing", 2, 5, "Issuing Authorization Code", "Yetki Kodu Uretiliyor",
        [
            auto_t("auth-code-issued", "otp-success",
                   "Authorization code issued", "Yetki kodu uretildi", "AuthGateIssuedRule.csx"),
            auto_t("auth-code-rejected", "auth-code-failed",
                   "Authorization code rejected", "Yetki kodu reddedildi", "AuthGateFailedRule.csx"),
            boundary_transition("auth-code-technical-failure", "auth-code-failed",
                                "Authorization code errored", "Yetki kodu hata verdi",
                                tasks=[exec_task("SetAuthCodeFailedResultMapping.csx")]),
        ],
        on_entries=[entry("IssueAuthorizationCodeMapping.csx", AUTHCODE_TASK)],
        error_boundary={"onError": [notify("auth-code-technical-failure", 100, ["*"])]}))

    states += [
        final("otp-success", 1, "OTP Auth Succeeded", "OTP Dogrulama Basarili"),
        final("profile-not-found", 2, "Profile Not Found", "Profil Bulunamadi"),
        final("phone-mismatch", 2, "Phone Mismatch", "Telefon Uyusmuyor"),
        final("sim-blocked", 2, "SIM Blocked", "SIM Bloke"),
        final("attempt-exceeded", 2, "Attempt Allowance Exceeded", "Deneme Hakki Asildi"),
        final("ttl-expired", 8, "OTP Timed Out", "OTP Zaman Asimi"),
        final("auth-code-failed", 2, "Authorization Code Failed", "Yetki Kodu Basarisiz"),
        final("otp-technical-failure", 2, "Technical Failure", "Teknik Hata"),
        final("otp-cancelled", 7, "Cancelled", "Iptal Edildi"),
    ]

    return states

# --------------------------------------------------------------------------------------
# Workflow zarflari
# --------------------------------------------------------------------------------------


def build_subflow():
    return {
        "key": "otp-auth",
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": DOMAIN,
        "version": "1.0.0",
        "tags": ["otp-auth", "otp", "authentication", "subflow", "authorization-code"],
        "attributes": {
            "type": "S",
            "subFlowType": None,
            "timeout": None,
            "labels": label("OTP Auth SubFlow", "OTP Kimlik Dogrulama Alt Akisi"),
            "schema": dict(SCHEMA_MASTER),
            "scripts": {"helpers": [dict(HELPERS)]},
            "functions": [],
            "features": [],
            "extensions": [],
            "sharedTransitions": [],
            "cancel": {
                "key": "cancel-otp-auth",
                "target": "otp-cancelled",
                "triggerType": 0,
                "versionStrategy": "Minor",
                "labels": label("Cancel OTP Auth", "OTP Dogrulamayi Iptal Et"),
                "onExecutionTasks": [exec_task("SetCancelledResultMapping.csx")],
            },
            "startTransition": {
                "key": "start-otp-auth",
                "target": "otp-initializing",
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label("Start OTP Auth", "OTP Dogrulamayi Baslat"),
                "schema": dict(SCHEMA_START),
                "onExecutionTasks": [exec_task("OtpAuthStartMapping.csx")],
            },
            "states": build_states(),
        },
    }


def build_harness():
    """
    Uretim akisi degil — subflow sozlesmesini (stateType 4 + ISubFlowMapping) uctan uca
    dogrulamak icin minimal parent.
    """
    return {
        "key": "otp-auth-harness",
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": DOMAIN,
        "version": "1.0.0",
        "tags": ["otp-auth", "harness", "integration-test", "subflow"],
        "attributes": {
            "type": "F",
            "timeout": None,
            "labels": label("OTP Auth Harness", "OTP Dogrulama Test Parent'i"),
            "schema": dict(SCHEMA_MASTER),
            "scripts": {"helpers": [dict(HELPERS)]},
            "functions": [],
            "features": [],
            "extensions": [],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start-otp-auth-harness",
                "target": "harness-initial",
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label("Start Harness", "Harness'i Baslat"),
                "schema": dict(SCHEMA_START),
                "onExecutionTasks": [exec_task("HarnessStartMapping.csx")],
            },
            "states": [
                state("harness-initial", 1, 0, "Harness Initial", "Harness Baslangic",
                      [auto_default("harness-to-otp-auth", "harness-otp-auth",
                                    "Enter OTP auth subflow", "OTP alt akisina gir")]),
                state("harness-otp-auth", 4, 0, "OTP Authentication", "OTP Kimlik Dogrulama",
                      [
                          auto_t("otp-auth-succeeded", "harness-success",
                                 "SubFlow succeeded", "Alt akis basarili", "OtpAuthSucceededRule.csx"),
                          auto_t("otp-auth-failed", "harness-failed",
                                 "SubFlow did not succeed", "Alt akis basarisiz", "OtpAuthFailedRule.csx"),
                      ],
                      subflow={
                          "type": "S",
                          "process": {"key": "otp-auth", "domain": DOMAIN,
                                      "flow": "sys-flows", "version": "1.0.0"},
                          "mapping": ref("HostToOtpAuthSubFlowMapping.csx"),
                      }),
                final("harness-success", 1, "Harness Success", "Harness Basarili"),
                final("harness-failed", 2, "Harness Failed", "Harness Basarisiz"),
            ],
        },
    }


def dump(doc, name):
    path = os.path.join(HERE, name)
    with open(path, "w") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    return path


def main():
    written = write_csx()
    print("csx yazildi: {} dosya".format(len(written)))

    sub = build_subflow()
    harness = build_harness()

    dump(sub, "otp-auth.json")
    dump(harness, "otp-auth-harness.json")

    states = sub["attributes"]["states"]
    transitions = sum(len(s["transitions"]) for s in states)
    print("otp-auth.json          : {} state, {} transition".format(len(states), transitions))
    print("otp-auth-harness.json  : {} state".format(len(harness["attributes"]["states"])))


if __name__ == "__main__":
    main()
