using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Core.Models;

/// <summary>Map summary from GET api/vda5050/maps</summary>
public class SimMapSummaryDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("mapId")]
    public string MapId { get; set; } = string.Empty;

    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = string.Empty;

    [JsonPropertyName("mapDescription")]
    public string? MapDescription { get; set; }

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }

    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }

    [JsonPropertyName("stationCount")]
    public int StationCount { get; set; }

    public override string ToString() => MapName;
}

/// <summary>Full map detail from GET api/vda5050/maps/{mapId}</summary>
public class SimMapDetailDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("mapId")]
    public string MapId { get; set; } = string.Empty;

    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = string.Empty;

    [JsonPropertyName("mapDescription")]
    public string? MapDescription { get; set; }

    [JsonPropertyName("nodes")]
    public List<SimNodeDto> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<SimEdgeDto> Edges { get; set; } = new();

    [JsonPropertyName("stations")]
    public List<SimStationDto> Stations { get; set; } = new();
}

public class SimNodeDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("nodeDescription")]
    public string? NodeDescription { get; set; }
}

public class SimEdgeDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("edgeId")]
    public string EdgeId { get; set; } = string.Empty;

    [JsonPropertyName("startNodeId")]
    public string StartNodeId { get; set; } = string.Empty;

    [JsonPropertyName("endNodeId")]
    public string EndNodeId { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public double? Length { get; set; }

    [JsonPropertyName("maxSpeed")]
    public double? MaxSpeed { get; set; }

    [JsonPropertyName("edgeDescription")]
    public string? EdgeDescription { get; set; }

    [JsonPropertyName("trajectory")]
    public Trajectory? Trajectory { get; set; }
}

public class SimStationDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("stationId")]
    public string StationId { get; set; } = string.Empty;

    [JsonPropertyName("stationName")]
    public string? StationName { get; set; }

    /// <summary>JSON array string e.g. ["N001","N002"] — the nodes this station is linked to</summary>
    [JsonPropertyName("interactionNodeIds")]
    public string? InteractionNodeIds { get; set; }

    [JsonPropertyName("positionX")]
    public double? PositionX { get; set; }

    [JsonPropertyName("positionY")]
    public double? PositionY { get; set; }

    [JsonPropertyName("positionTheta")]
    public double? PositionTheta { get; set; }

    [JsonPropertyName("positionMapId")]
    public string? PositionMapId { get; set; }

    /// <summary>Returns the first node ID from InteractionNodeIds JSON array, or null if absent.</summary>
    public string? FirstInteractionNodeId
    {
        get
        {
            if (string.IsNullOrEmpty(InteractionNodeIds)) return null;
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(InteractionNodeIds);
                return ids?.FirstOrDefault();
            }
            catch { return null; }
        }
    }
}

/// <summary>
/// Simple in-memory graph built from SimMapDetailDto for path lookups
/// </summary>
public class SimMapGraph
{
    public string MapId { get; set; } = string.Empty;

    /// <summary>nodeId → SimNodeDto lookup</summary>
    public Dictionary<string, SimNodeDto> Nodes { get; set; } = new();

    /// <summary>nodeId → list of outgoing edges</summary>
    public Dictionary<string, List<SimEdgeDto>> Adjacency { get; set; } = new();

    public static SimMapGraph Build(SimMapDetailDto map)
    {
        var graph = new SimMapGraph { MapId = map.MapId };

        foreach (var node in map.Nodes)
            graph.Nodes[node.NodeId] = node;

        foreach (var edge in map.Edges)
        {
            if (!graph.Adjacency.ContainsKey(edge.StartNodeId))
                graph.Adjacency[edge.StartNodeId] = new List<SimEdgeDto>();
            graph.Adjacency[edge.StartNodeId].Add(edge);
        }

        return graph;
    }

    public SimEdgeDto? FindEdge(string fromNodeId, string toNodeId)
    {
        if (Adjacency.TryGetValue(fromNodeId, out var edges))
            return edges.FirstOrDefault(e => e.EndNodeId == toNodeId);
        return null;
    }
}
