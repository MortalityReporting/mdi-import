using System.Text.Json.Nodes;

namespace MortalityReporting.ImportApi.Models;

public sealed class FieldMappingStatus
{
    public string Status { get; init; } = "not mapped";
    public string Value { get; init; } = "";
    public List<string> Errors { get; init; } = [];
    public JsonObject FhirResource { get; init; } = [];
}

public sealed class FhirSubmissionResponse
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "Not Submitted";
    public int? StatusCode { get; init; }
    public string State { get; init; } = "";
    public string? Location { get; init; }
}

public sealed class ImportRecordResponse
{
    public JsonObject? FhirBundle { get; init; }
    public Dictionary<string, FieldMappingStatus> Fields { get; init; } = new(StringComparer.Ordinal);
    public FhirSubmissionResponse? FhirResponse { get; init; }
    public List<string> Errors { get; init; } = [];
}
