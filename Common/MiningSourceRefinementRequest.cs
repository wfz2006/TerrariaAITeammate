using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Sources-only continuation grammar. A scope identifies enumeration, never a world snapshot.</summary>
public sealed record MiningSourceRefinementRequest(WorldKnowledgeRequest Request, int RefineOffset, string RefineScope)
{
    internal const int NearbyDiscoveryDistance = 160;

    public static MiningSourceRefinementRequest Parse(string query, int? x = null, int? y = null)
    {
        string[] parts = (query ?? "").Split(';');
        var retained = new List<string> { parts[0] }; var seen = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0; string scope = "";
        foreach (string option in parts.Skip(1))
        {
            int equals = option.IndexOf('=');
            string key = equals < 0 ? "" : option[..equals].Trim().ToLowerInvariant();
            if (key is not ("refineoffset" or "refinescope")) { retained.Add(option); continue; }
            if (!seen.Add(key) || equals != option.LastIndexOf('=')) throw new ArgumentException("Duplicate or malformed source refinement option.");
            string value = option[(equals + 1)..].Trim();
            if (key == "refineoffset")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset > 32768)
                    throw new ArgumentException("refineOffset requires0..32768.");
            }
            else
            {
                if (value.Length != 24 || value.Any(character => !Uri.IsHexDigit(character)))
                    throw new ArgumentException("refineScope must be the24 hexadecimal characters returned by sources.");
                scope = value.ToLowerInvariant();
            }
        }
        WorldKnowledgeRequest request = WorldKnowledgeRequest.Parse(string.Join(";", retained), x, y);
        if (request.Kind != "resource") throw new ArgumentException("sources accepts only wood/stone/ore/dirt/grass resource queries and their paging/distance options.");
        if (offset > 0 && scope.Length == 0) throw new ArgumentException("A nonzero refineOffset requires the returned refineScope.");
        return new(request, offset, scope);
    }

    public static string ComputeScope(JsonObject header)
    {
        JsonObject? query = header["query"] as JsonObject;
        bool resolved = TryGetResolvedOrigin(header, out int x, out int y);
        var identity = new JsonObject { ["worldId"] = header["world"]?["worldId"]?.DeepClone(),
            ["epoch"] = header["world"]?["epoch"]?.DeepClone(),
            ["origin"] = resolved ? new JsonObject { ["x"] = x, ["y"] = y } : header["origin"]?.DeepClone() };
        // Null coordinates select the current observer only when the query begins.
        // Making that same effective origin explicit must preserve enumeration identity.
        foreach (string field in new[] { "resource", "offset", "limit", "minDistance", "maxDistance" })
            identity[field] = query?[field]?.DeepClone();
        // Old nonnative headers without a valid resolved pair retain their conservative
        // identity; they must not gain an invented query anchor.
        if (!resolved)
            foreach (string field in new[] { "x", "y" }) identity[field] = query?[field]?.DeepClone();
        identity["groups"] = new JsonArray((header["groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(group => (JsonNode?)new JsonObject { ["id"] = group["id"]?.DeepClone(), ["tileType"] = group["tileType"]?.DeepClone(), ["bounds"] = group["bounds"]?.DeepClone() }).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToJsonString())))[..24].ToLowerInvariant();
    }

    internal static bool TryGetResolvedOrigin(JsonObject header, out int x, out int y)
    {
        x = y = 0;
        return header["origin"] is JsonObject origin &&
            origin["x"] is JsonValue ox && ox.TryGetValue(out x) && x >= 0 &&
            origin["y"] is JsonValue oy && oy.TryGetValue(out y) && y >= 0;
    }

    public static string ContinueQuery(JsonObject header, int offset, string scope)
        => PageQuery(header, header["query"]?["offset"]?.GetValue<int>() ?? 0) + ";refineOffset=" + offset + ";refineScope=" + scope;

    internal static string PageQuery(JsonObject header, int offset)
    {
        JsonObject query = header["query"]!.AsObject();
        string value = header["resource"]!.GetValue<string>() + ";offset=" + offset + ";limit=" + (query["limit"]?.GetValue<int>() ?? 4);
        foreach (string field in new[] { "minDistance", "maxDistance" }) if (query[field] != null) value += ";" + field + "=" + query[field]!.GetValue<int>();
        return value;
    }
}
