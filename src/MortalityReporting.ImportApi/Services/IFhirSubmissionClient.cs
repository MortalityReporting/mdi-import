using System.Text.Json.Nodes;
using MortalityReporting.ImportApi.Models;

namespace MortalityReporting.ImportApi.Services;

public interface IFhirSubmissionClient
{
    Task<FhirSubmissionResponse> SubmitAsync(JsonObject bundle, string name, CancellationToken cancellationToken);
}
