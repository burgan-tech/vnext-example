using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;

namespace Acme.Helpers;

/// <summary>
/// Shared helpers for the otp-auth SubFlow.
/// <para>
/// Deliberately free of any <c>ScriptContext</c> dependency so the class stays a plain
/// sys-mappings helper (same shape as <c>JsonHelper</c> / <c>RsaCryptoHelper</c>). Callers pass
/// <c>context.Instance?.Data</c> and <c>context.Body</c> in as plain objects.
/// </para>
/// <para>
/// The single most important helper is <see cref="Merge"/>. Every OutputHandler in this flow must
/// start from a copy of the current instance data and write on top of it; returning a fresh object
/// with only the newly computed fields can drop everything the earlier states wrote.
/// </para>
/// </summary>
public static class OtpAuthHelpers
{
    /// <summary>Reads any dynamic payload (ExpandoObject, dictionary, POCO) as a string-keyed map.</summary>
    public static IDictionary<string, object> ToDict(object data)
    {
        if (data == null)
        {
            return new Dictionary<string, object>();
        }

        if (data is IDictionary<string, object> typed)
        {
            return typed;
        }

        var result = new Dictionary<string, object>();
        foreach (var property in data.GetType().GetProperties())
        {
            if (!property.CanRead)
            {
                continue;
            }

            try
            {
                result[property.Name] = property.GetValue(data);
            }
            catch (Exception)
            {
                // A property that throws on read is simply absent from the projection.
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a mutable copy of the instance data. Start every OutputHandler with this, mutate the
    /// copy, and return it as <c>ScriptResponse.Data</c> so no previously written field is lost.
    /// </summary>
    public static IDictionary<string, object> Merge(object instanceData)
    {
        var copy = new Dictionary<string, object>();
        foreach (var entry in ToDict(instanceData))
        {
            copy[entry.Key] = entry.Value;
        }

        return copy;
    }

    /// <summary>Reads a string field, returning <paramref name="fallback"/> when absent, null or blank.</summary>
    public static string Str(IDictionary<string, object> data, string key, string fallback = null)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null)
        {
            return fallback;
        }

        var text = raw.ToString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    /// <summary>Reads an integer field, returning <paramref name="fallback"/> when absent or unparsable.</summary>
    public static int Int(IDictionary<string, object> data, string key, int fallback)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Reads a boolean field, returning <paramref name="fallback"/> when absent or unparsable.</summary>
    public static bool Bool(IDictionary<string, object> data, string key, bool fallback)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null)
        {
            return fallback;
        }

        if (raw is bool typed)
        {
            return typed;
        }

        return bool.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }

    /// <summary>True when the named gate field currently holds <paramref name="expected"/>.</summary>
    public static bool GateIs(object instanceData, string gateField, string expected)
    {
        return string.Equals(Str(ToDict(instanceData), gateField), expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reduces any Turkish mobile number spelling to bare national 10 digits: strips separators,
    /// then drops a leading +90 / 90 / 0. Returns null when the result is not 10 digits.
    /// </summary>
    public static string NormalizePhone(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new System.Text.StringBuilder();
        foreach (var character in raw)
        {
            if (character >= '0' && character <= '9')
            {
                digits.Append(character);
            }
        }

        var value = digits.ToString();

        if (value.Length == 12 && value.StartsWith("90", StringComparison.Ordinal))
        {
            value = value.Substring(2);
        }
        else if (value.Length == 11 && value.StartsWith("0", StringComparison.Ordinal))
        {
            value = value.Substring(1);
        }

        return value.Length == 10 ? value : null;
    }

    /// <summary>True when both numbers normalise to the same national 10 digits. Null never matches.</summary>
    public static bool PhonesMatch(string left, string right)
    {
        var a = NormalizePhone(left);
        var b = NormalizePhone(right);
        return a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>First 3 digits of a normalised number, as the OTP service's Phone.Prefix.</summary>
    public static string PhonePrefix(string raw)
    {
        var value = NormalizePhone(raw);
        return value == null ? string.Empty : value.Substring(0, 3);
    }

    /// <summary>Everything after the first 3 digits, as the OTP service's Phone.Number.</summary>
    public static string PhoneSubscriber(string raw)
    {
        var value = NormalizePhone(raw);
        return value == null ? string.Empty : value.Substring(3);
    }

    /// <summary>Round-trip-safe UTC timestamp for otpSentAt / otpExpiresAt / simBlockedAt.</summary>
    public static string UtcNow()
    {
        return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>UTC timestamp <paramref name="seconds"/> in the future.</summary>
    public static string UtcIn(int seconds)
    {
        return DateTime.UtcNow.AddSeconds(seconds).ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>Parses an "o"-format UTC stamp back to a DateTime; returns null when unusable.</summary>
    public static DateTime? ParseUtc(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : (DateTime?)null;
    }
}
