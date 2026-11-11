using System;
using System.Collections.Generic;
using System.Globalization;

namespace TerrariaAITeammate.Common;

/// <summary>Pure grammar for bounded world fact queries. Parsing never selects or moves an actor.</summary>
public sealed record WorldKnowledgeRequest(string Kind, string Value, int Offset, int Limit, int? X, int? Y)
{
    public int? MinDistance { get; init; }
    public int? MaxDistance { get; init; }
    public bool HasDistanceFilter => MinDistance.HasValue || MaxDistance.HasValue;
    public bool MatchesDistance(double distance) => double.IsFinite(distance) && distance >= (MinDistance ?? 0) &&
        (!MaxDistance.HasValue || distance <= MaxDistance.Value);
    private static readonly HashSet<string> Resources = new(StringComparer.Ordinal)
    {
        "wood", "stone", "iron", "copper", "gold", "silver", "lead", "tin", "tungsten", "platinum",
        "meteorite", "ruby", "dirt", "grass"
    };

    public static WorldKnowledgeRequest Parse(string query, int? x = null, int? y = null)
    {
        if (x.HasValue != y.HasValue || x < 0 || y < 0)
            throw new ArgumentException("World query coordinates require a nonnegative paired x/y origin.");

        string[] parts = (query ?? "").Split(';');
        string subject = parts[0].Trim().ToLowerInvariant();
        int offset = 0, limit = 4;
        int? minDistance = null, maxDistance = null;
        var options = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < parts.Length; index++)
        {
            string option = parts[index].Trim();
            int equals = option.IndexOf('=');
            if (equals <= 0 || equals != option.LastIndexOf('='))
                throw new ArgumentException("World query options must be ;offset=0..50000, ;limit=1..8, ;minDistance=0..100000 or ;maxDistance=0..100000.");
            string key = option[..equals].Trim().ToLowerInvariant();
            string value = option[(equals + 1)..].Trim();
            if (!options.Add(key)) throw new ArgumentException("Duplicate world query option: " + key);
            switch (key)
            {
                case "offset": offset = Integer(value, 0, 50000, "offset"); break;
                case "limit": limit = Integer(value, 1, 8, "limit"); break;
                case "mindistance": minDistance = Integer(value, 0, 100000, "minDistance"); break;
                case "maxdistance": maxDistance = Integer(value, 0, 100000, "maxDistance"); break;
                default: throw new ArgumentException("Unsupported world query option: " + key);
            }
        }

        string kind, canonical = "";
        if (subject.Length == 0 || subject == "overview") kind = "overview";
        else if (Resources.Contains(subject)) { kind = "resource"; canonical = subject; }
        else if (subject is "chests" or "chest" or "stations" or "items") kind = subject;
        else
        {
            int separator = subject.IndexOf(':');
            if (separator <= 0)
                throw new ArgumentException("Unsupported world query. Use overview, a resource, tile:ID, item:ID, npc:ID, boss:name, chests, chest, stations or items.");
            kind = subject[..separator].Trim();
            string value = subject[(separator + 1)..].Trim();
            switch (kind)
            {
                case "resource" when Resources.Contains(value): canonical = value; break;
                case "tile":
                case "npc": canonical = Integer(value, 0, int.MaxValue, kind + " ID").ToString(CultureInfo.InvariantCulture); break;
                case "item": canonical = Integer(value, 1, int.MaxValue, "item ID").ToString(CultureInfo.InvariantCulture); break;
                case "boss": canonical = BossName(value); break;
                default: throw new ArgumentException("Unsupported world query kind or resource: " + subject);
            }
        }

        if (kind == "chest" && !x.HasValue)
            throw new ArgumentException("A chest detail query requires paired x/y selecting its actual anchor or 2x2 footprint; use chests first to discover real chest coordinates.");
        if (minDistance > maxDistance) throw new ArgumentException("minDistance must not exceed maxDistance.");
        if ((minDistance.HasValue || maxDistance.HasValue) && kind is "overview" or "chest")
            throw new ArgumentException("Distance filters apply to location lists, not overview or an exact chest detail query.");
        return new WorldKnowledgeRequest(kind, canonical, offset, limit, x, y)
        { MinDistance = minDistance, MaxDistance = maxDistance };
    }

    private static int Integer(string text, int minimum, int maximum, string name)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name} requires a decimal integer from {minimum} through {maximum}.");
        return value;
    }

    private static string BossName(string value) => value.Replace(" ", "").Replace("_", "") switch
    {
        "50" or "560" or "kingslime" or "slimeking" or "slimecrown" or "史莱姆王" or "史莱姆王冠" => "king_slime",
        "4" or "43" or "eyeofcthulhu" or "eye" or "suspiciouslookingeye" or "克苏鲁之眼" or "克眼" or "可疑眼球" => "eye_of_cthulhu",
        "262" or "plantera" or "世纪之花" => "plantera",
        _ => throw new ArgumentException("Unsupported or empty boss name; supported world boss queries are king_slime, eye_of_cthulhu and plantera.")
    };
}
