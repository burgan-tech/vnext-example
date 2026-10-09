using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// file-offload-lab: review onEntry. Reads the passport bytes back through ScriptBase.GetFileAsync (same domain,
/// in process) and writes their SHA-256 (lower-case hex) as <c>checksum</c> — equal to the handle's eTag when the
/// stored bytes are the uploaded ones. SHA-256 is computed in plain C#: <c>System.Security.Cryptography.SHA256</c>
/// does not resolve in a mapping compile (CS0103, observed 2026-10-08).
/// </summary>
public class FoChecksumMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        if (data == null || !data.TryGetValue("passport", out var raw) || raw is not IDictionary<string, object> passport
            || !passport.TryGetValue("file", out var fileId) || fileId == null)
        {
            target["checksum"] = "no-passport";
            return new ScriptResponse { Data = result };
        }

        var file = await GetFileAsync("core", "fo-flow", context.Instance.Id.ToString(), fileId.ToString()!);
        target["checksum"] = Sha256Hex(file.Content);
        target["checksumSize"] = file.Content.Length;
        return new ScriptResponse { Data = result };
    }

    private static readonly uint[] K =
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

    private static string Sha256Hex(byte[] data)
    {
        uint[] h = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
        var bitLength = (ulong)data.LongLength * 8;
        var padded = new byte[((data.Length + 9 + 63) / 64) * 64];
        Array.Copy(data, padded, data.Length);
        padded[data.Length] = 0x80;
        for (var i = 0; i < 8; i++) padded[padded.Length - 1 - i] = (byte)(bitLength >> (8 * i));

        var w = new uint[64];
        for (var chunk = 0; chunk < padded.Length; chunk += 64)
        {
            for (var t = 0; t < 16; t++)
                w[t] = (uint)(padded[chunk + 4 * t] << 24 | padded[chunk + 4 * t + 1] << 16
                              | padded[chunk + 4 * t + 2] << 8 | padded[chunk + 4 * t + 3]);
            for (var t = 16; t < 64; t++)
            {
                var s0 = Rotr(w[t - 15], 7) ^ Rotr(w[t - 15], 18) ^ (w[t - 15] >> 3);
                var s1 = Rotr(w[t - 2], 17) ^ Rotr(w[t - 2], 19) ^ (w[t - 2] >> 10);
                w[t] = w[t - 16] + s0 + w[t - 7] + s1;
            }

            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (var t = 0; t < 64; t++)
            {
                var t1 = hh + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[t] + w[t];
                var t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }

            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }

        var hex = new System.Text.StringBuilder(64);
        foreach (var word in h) hex.Append(word.ToString("x8"));
        return hex.ToString();
    }
}
