using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MortalityReporting.ImportApi.Configuration;
using MortalityReporting.ImportApi.Models;

namespace MortalityReporting.ImportApi.Services;

public sealed class FhirSubmissionClient(HttpClient httpClient, ImportApiOptions options) : IFhirSubmissionClient
{
    public async Task<FhirSubmissionResponse> SubmitAsync(JsonObject bundle, string name, CancellationToken cancellationToken)
    {
        options.ValidateForSubmission();
        using HttpRequestMessage request = new(HttpMethod.Post, options.FhirEndpoint)
        {
            Content = new StringContent(bundle.ToJsonString(), Encoding.UTF8, "application/fhir+json")
        };
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.FhirUsername}:{options.FhirPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new FhirSubmissionResponse
            {
                Name = name,
                Status = response.IsSuccessStatusCode ? "Success" : "Error",
                StatusCode = (int)response.StatusCode,
                State = "",
                Location = response.Headers.Location?.ToString()
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FhirSubmissionResponse { Name = name, Status = "Error", StatusCode = 504, State = "timeout" };
        }
        catch (HttpRequestException)
        {
            return new FhirSubmissionResponse { Name = name, Status = "Error", StatusCode = 502, State = "connection" };
        }
    }
}
