using Windows.Data.Json;

namespace RalFsr.Widget
{
    /// <summary>Null-safe reads from Windows.Data.Json objects.</summary>
    internal static class Json
    {
        public static string Str(JsonObject o, string key, string fallback = "")
        {
            IJsonValue v;
            return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.String ? v.GetString() : fallback;
        }

        public static bool Bool(JsonObject o, string key)
        {
            IJsonValue v;
            return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Boolean && v.GetBoolean();
        }

        public static JsonObject Obj(JsonObject o, string key)
        {
            IJsonValue v;
            return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Object ? v.GetObject() : null;
        }

        public static JsonArray Arr(JsonObject o, string key)
        {
            IJsonValue v;
            return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Array ? v.GetArray() : new JsonArray();
        }
    }
}
