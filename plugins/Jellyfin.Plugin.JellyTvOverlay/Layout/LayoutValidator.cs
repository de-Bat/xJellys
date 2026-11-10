using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellyTvOverlay.Layout;

public sealed class LayoutException : Exception
{
    public LayoutException(string message) : base(message)
    {
    }
}

public sealed record ValidatedLayout(
    string Id,
    string Focus,
    int TtlMs,
    string? Parent,
    IReadOnlyList<string> CloseOn,
    IReadOnlyList<string> SuppressTriggers,
    JsonObject Json);

/// <summary>Validates layout v1 JSON, fills defaults, sets owner and namespaces ids. Unknown fields and node types are kept.</summary>
public static class LayoutValidator
{
    private static readonly string[] Focuses = { "passive", "interactive", "interactive+passthrough" };
    private static readonly string[] Triggers = { "itemStart", "pause", "resume", "doublePause", "seek", "stop" };
    private static readonly string[] DefaultCloseOn = { "stop", "itemStart" };

    public static ValidatedLayout Validate(string json, string owner)
    {
        try
        {
            return ValidateCore(json, owner);
        }
        catch (ArgumentException ex)
        {
            // Null json, or duplicate property names (JsonObject throws on first access, so anywhere in the walk).
            throw new LayoutException("invalid JSON: " + ex.Message);
        }
    }

    private static ValidatedLayout ValidateCore(string json, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new LayoutException("owner is required");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LayoutException("invalid JSON: " + ex.Message);
        }

        if (node is not JsonObject obj)
        {
            throw new LayoutException("layout must be a JSON object");
        }

        return ValidateObject(obj, owner);
    }

    private static ValidatedLayout ValidateObject(JsonObject obj, string owner)
    {
        if (!IsNumber(obj["v"], out var v) || v != 1)
        {
            throw new LayoutException("v must be 1");
        }

        var rawId = ReadString(obj, "id") ?? throw new LayoutException("id is required");
        if (rawId.Length == 0)
        {
            throw new LayoutException("id must not be empty");
        }

        var id = Namespace(rawId, owner);

        var focus = "passive";
        if (obj["focus"] is not null)
        {
            focus = ReadString(obj, "focus") ?? string.Empty;
            if (Array.IndexOf(Focuses, focus) < 0)
            {
                throw new LayoutException("focus must be one of " + string.Join(", ", Focuses));
            }
        }

        var ttlMs = 0;
        if (obj["ttlMs"] is not null)
        {
            if (!IsNumber(obj["ttlMs"], out var ttl) || ttl < 0 || ttl > int.MaxValue)
            {
                throw new LayoutException("ttlMs must be a non-negative number");
            }

            ttlMs = (int)ttl;
        }

        if (obj["ttlResetOnKey"] is { } reset && !(reset is JsonValue rv && rv.TryGetValue<bool>(out _)))
        {
            throw new LayoutException("ttlResetOnKey must be a boolean");
        }

        string? parent = null;
        if (obj["parent"] is not null)
        {
            var rawParent = ReadString(obj, "parent");
            if (string.IsNullOrEmpty(rawParent))
            {
                throw new LayoutException("parent must be a non-empty string");
            }

            parent = Namespace(rawParent, owner);
        }

        var closeOn = ReadTriggerList(obj, "closeOn", DefaultCloseOn);
        var suppress = ReadTriggerList(obj, "suppressTriggers", Array.Empty<string>());

        if (obj["root"] is not JsonObject root)
        {
            throw new LayoutException("root is required and must be an object");
        }

        var valueIds = new HashSet<string>(StringComparer.Ordinal);
        CollectValueIds(root, valueIds);
        ValidateKeys(obj["keys"], valueIds);

        JsonArray? preload = null;
        if (obj["preload"] is not null)
        {
            preload = obj["preload"] as JsonArray ?? throw new LayoutException("preload must be an array");
            foreach (var p in preload)
            {
                if (p is not JsonObject po)
                {
                    throw new LayoutException("preload entries must be objects");
                }

                ValidateObject(po, owner);
            }
        }

        obj["owner"] = owner;
        obj["id"] = id;
        obj["focus"] = focus;
        obj["ttlMs"] = ttlMs;
        if (parent is not null)
        {
            obj["parent"] = parent;
        }

        obj["closeOn"] = ToArray(closeOn);
        obj["suppressTriggers"] = ToArray(suppress);

        return new ValidatedLayout(id, focus, ttlMs, parent, closeOn, suppress, obj);
    }

    private static string Namespace(string id, string owner)
        => id.StartsWith(owner + "/", StringComparison.Ordinal) ? id : owner + "/" + id;

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items)
        {
            a.Add(s);
        }

        return a;
    }

    private static string[] ReadTriggerList(JsonObject obj, string field, string[] fallback)
    {
        if (obj[field] is null)
        {
            return fallback;
        }

        if (obj[field] is not JsonArray arr)
        {
            throw new LayoutException(field + " must be an array");
        }

        var list = new List<string>();
        foreach (var item in arr)
        {
            var s = StringValue(item);
            if (s is null || Array.IndexOf(Triggers, s) < 0)
            {
                throw new LayoutException(field + " entries must be one of " + string.Join(", ", Triggers));
            }

            if (!list.Contains(s))
            {
                list.Add(s);
            }
        }

        return list.ToArray();
    }

    private static void CollectValueIds(JsonNode? node, HashSet<string> ids)
    {
        if (node is not JsonObject o)
        {
            return;
        }

        if (StringValue(o["type"]) == "value" && StringValue(o["id"]) is { } id)
        {
            ids.Add(id);
        }

        if (o["children"] is JsonArray children)
        {
            foreach (var c in children)
            {
                CollectValueIds(c, ids);
            }
        }
    }

    private static void ValidateKeys(JsonNode? keys, HashSet<string> valueIds)
    {
        if (keys is null)
        {
            return;
        }

        if (keys is not JsonObject ko)
        {
            throw new LayoutException("keys must be an object");
        }

        foreach (var (key, binding) in ko)
        {
            if (binding is JsonObject b && StringValue(b["op"]) == "step")
            {
                var target = StringValue(b["target"]);
                if (target is null || !valueIds.Contains(target))
                {
                    throw new LayoutException($"keys.{key}: step target '{target}' is not a value node in this layout");
                }

                if (!IsNumber(b["by"], out _))
                {
                    throw new LayoutException($"keys.{key}: step needs a numeric 'by'");
                }
            }
        }
    }

    private static string? ReadString(JsonObject obj, string field) => StringValue(obj[field]);

    private static string? StringValue(JsonNode? n)
        => n is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    private static bool IsNumber(JsonNode? n, out double value)
    {
        value = 0;
        return n is JsonValue jv && jv.TryGetValue<double>(out value);
    }
}
