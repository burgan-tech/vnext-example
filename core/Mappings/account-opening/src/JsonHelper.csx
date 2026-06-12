using Newtonsoft.Json;

namespace Acme.Helpers;

public static class JsonHelper
{
    public static string Serialize(object value) => JsonConvert.SerializeObject(value);
}