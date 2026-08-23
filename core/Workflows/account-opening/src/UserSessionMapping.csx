using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;


public class UserSessionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    /// <summary>
    /// Populate the user session data into the workflow instance
    /// </summary>
    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var privateKey = "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCW04Z6I514AmuceRrWzybVzdnxqdOBusdg4jHcTLPZSHxr+4DMLA3m3c/yVZshJNG09uq1XCwLRdH02HU6JdM5aiaFN2wybJft0Bofm5fc349/BDjZAKioiebtRVgl/U3Prz52CcVWLKNrAtpFeTwLBQ+nQiN/mhnvoBoW5NZrukxjLLj8dB4Dr/+i0pprv/PuUgfRe+iB6j03Vf140swYacIk8MptsZ6ZQdUXrRc6Ky99557Oqg4r/blxbQs+HaAoR2WmR5P0cj4iFN9kVIHdpfD3OlIRVyblqIGtVHX+PCSTcsF/5I+DkEvmejiRhPwU1ok6IMOdM3dqqi0AKKv1AgMBAAECggEAAe1jiHmE7sGz9gZB8cp1XLuNhEFIvt2WZFZKz7dNZm9YiayBA/rj7y+/ICgrlcDJvjlLUAFExDZPVvfe7Zzwn6+L0CpGydEp7HCUxa0gQ5xtHr280gcOxxKPpR4JYMWyTuWJuiPo7EkHPVv5hcELsGwo4kwENjWPppVIgamvoYvbnG7H9Oh9VzlX9OItkLSvWEBm7aOMTVAd1+UiAL7l2Z7dFY0RsGMH8yplRirz/A9BJxiiMTGLLMzc9Ff3KsdQ8JEmpXXdPAujntTMP9zWcME8c2YzspHStPnh9jhpTyEhjSbCS/I1cNsEFsjh2c2MkHDaTPUoI6/0iLuQwBKcAQKBgQDO1evJnd9HO0F/rotd5AuBDwjSLGimikKPympYEvvmPemAEjBRf34cgKT+iA3v4P9cBcNPQBylAIyVoT0XuFRjBD6sxxtin1z3yQRGYpcSxuC3DZ1jU4wi0uly6q/pTvUab4ors6Os3pCuJUM8zT7MmPB45TdIApKvvRrF3+1P9QKBgQC6rWY7gTuESdHW9ec0T97c73kl29TeggEqjlrfOcI8CYMNv/w84DmcWx3rOLiWHHbqPjPX5stKxUyfsSdXwpfJs73inDkruFFuAUPeJpBzYt0hJA9iRTErWskKyRIJ5ehELkyAH3XpRgyUQ0tsW1idBAhRp4mqTMknsujt6IBsAQKBgQCE9nwXJgfs8KjQfdJVz02d755KDgZQWT0k1oi6iampf09l50tseLsHc6OdhLUA6fD+pS3C+oHviITXg8mUQAjvhkEMLQrrwWqwV2cKIELh7Tt0MaplucWydUdheoEPSJTEI8P9CARGEuWLLaUlpwOh3wdnkGKTRiQqGTTm02bpKQKBgAQBQFY6eYpnAwd3kxQ+OmvG/3ReePylEV1WXIC5fn9HPPaIjeLIdLP0CHpJZzxhM/PmjbouC2J5RSGP7WYmmJcNMh+wdlGHzMdtY4VaknLHRjM10Nas4VcqxXFjyu1Hb2o3DBEbm637gL2VjAKxGv+TXJJT49Ixf4dIgVLJUCgBAoGAQx9LF5oH4BKZRHkBuadpQveNCie9zGu+V+9nqUbJMrlt5y6vQQxK0L1MTlgX7c2YBRrD3Dy7kvUnm5QXaHK43kTxOtXgBWArEAYkc+25RvPG2s3aiqmQz+ZEyWi0Z+tc3V6ceBDLT4CidolSGKoAmDT3BeEvY1KCPyy31SvrRpo=";
        var publicKey = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAltOGeiOdeAJrnHka1s8m1c3Z8anTgbrHYOIx3Eyz2Uh8a/uAzCwN5t3P8lWbISTRtPbqtVwsC0XR9Nh1OiXTOWomhTdsMmyX7dAaH5uX3N+PfwQ42QCoqInm7UVYJf1Nz68+dgnFViyjawLaRXk8CwUPp0Ijf5oZ76AaFuTWa7pMYyy4/HQeA6//otKaa7/z7lIH0Xvogeo9N1X9eNLMGGnCJPDKbbGemUHVF60XOisvfeeezqoOK/25cW0LPh2gKEdlpkeT9HI+IhTfZFSB3aXw9zpSEVcm5aiBrVR1/jwkk3LBf+SPg5BL5no4kYT8FNaJOiDDnTN3aqotACir9QIDAQAB";
        // Headers are read through Header(...) — never with the indexer. ScriptContext.Headers is a
        // Dictionary<string, string?> whose indexer THROWS KeyNotFoundException on a missing key, so
        // `context.Headers?["x-forwarded-for"] ?? context.Headers?["x-real-ip"]` never reached its
        // fallback: it threw first. That killed this whole output handler for any caller that does
        // not sit behind a proxy, so `userSession` was never written — and CreateBankAccountMapping
        // downstream then sent an empty body and the flow took account-creation-failed.
        string userRef = Header(context, "user_reference");
        var encryptedCard = RsaCryptoHelper.Encrypt(userRef, publicKey);
        var roundTripOk = RsaCryptoHelper.Decrypt(encryptedCard, privateKey) == userRef;
        var crypto = new
        {
            encryptedCard,
            roundTripOk
        };
        //var dataJson = JsonHelper.Serialize(crypto);

        // Only the headers that are actually present are written. The master schema declares every
        // userSession field as `type: "string"`, so an absent header must be an ABSENT property —
        // writing it as null fails JSON schema validation and takes the whole task down with it.
        var session = CreateObject();
        SetIfPresent(session, "userId", userRef);
        SetIfPresent(session, "deviceId", Header(context, "x-device-id"));
        SetIfPresent(session, "userAgent", Header(context, "user-agent"));
        SetIfPresent(session, "ipAddress", Header(context, "x-forwarded-for") ?? Header(context, "x-real-ip"));

        return new ScriptResponse
            {
                Key = "user-session-output",
                Data = new
                {
                    userSession = session,
                    crypto
                }
            };
    }

    /// <summary>
    /// Reads one request header, returning null when it is absent. ScriptContext.Headers is a
    /// Dictionary&lt;string, string?&gt; with lowercased keys, and its indexer throws on a missing
    /// key — so absence must be handled with TryGetValue, not with `?.[...]` plus `??`.
    /// </summary>
    private static string Header(ScriptContext context, string name)
    {
        var headers = context.Headers as IDictionary<string, string>;
        return headers != null && headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Writes the property only when the value is non-null — see the note above.</summary>
    private void SetIfPresent(object target, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            SetProperty(target, name, value);
    }
}
