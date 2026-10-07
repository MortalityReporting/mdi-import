using System.Net;
using MortalityReporting.ImportApi.Configuration;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Services;
using System.Text.Json.Nodes;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

public sealed class FhirSubmissionClientTests
{
    [Fact]
    public async Task PostsFhirJsonWithBasicAuthorization()
    {
        HttpRequestMessage? captured = null;
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Created) { Headers = { Location = new Uri("https://fhir.test/Bundle/1") } };
        }));
        ImportApiOptions options = new() { FhirEndpoint = "https://fhir.test", FhirUsername = "client", FhirPassword = "secret" };
        FhirSubmissionResponse response = await new FhirSubmissionClient(httpClient, options).SubmitAsync(new JsonObject { ["resourceType"] = "Bundle" }, "Jane Doe", CancellationToken.None);
        Assert.Equal("Success", response.Status);
        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Basic Y2xpZW50OnNlY3JldA==", captured!.Headers.Authorization!.ToString());
        Assert.Equal("application/fhir+json", captured.Content!.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Error", 400, "")]
    [InlineData(HttpStatusCode.Unauthorized, "Error", 401, "")]
    [InlineData(HttpStatusCode.Forbidden, "Error", 403, "")]
    [InlineData(HttpStatusCode.NotFound, "Error", 404, "")]
    [InlineData(HttpStatusCode.InternalServerError, "Error", 500, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Error", 503, "")]
    public async Task HandlesVariousHttpStatusCodesCorrectly(HttpStatusCode statusCode, string expectedStatus, int expectedStatusCode, string expectedState)
    {
        HttpRequestMessage? captured = null;
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(statusCode);
        }));
        ImportApiOptions options = new() { FhirEndpoint = "https://fhir.test", FhirUsername = "client", FhirPassword = "secret" };
        FhirSubmissionResponse response = await new FhirSubmissionClient(httpClient, options).SubmitAsync(new JsonObject { ["resourceType"] = "Bundle" }, "Jane Doe", CancellationToken.None);

        Assert.Equal(expectedStatus, response.Status);
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedState, response.State);
    }

    [Fact]
    public async Task HandlesTimeoutException()
    {
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            throw new TaskCanceledException("timeout");
        }));
        ImportApiOptions options = new() { FhirEndpoint = "https://fhir.test", FhirUsername = "client", FhirPassword = "secret" };
        FhirSubmissionResponse response = await new FhirSubmissionClient(httpClient, options).SubmitAsync(new JsonObject { ["resourceType"] = "Bundle" }, "Jane Doe", CancellationToken.None);

        Assert.Equal("Error", response.Status);
        Assert.Equal(504, response.StatusCode);
        Assert.Equal("timeout", response.State);
    }

    [Fact]
    public async Task HandlesHttpRequestException()
    {
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            throw new HttpRequestException("Network error");
        }));
        ImportApiOptions options = new() { FhirEndpoint = "https://fhir.test", FhirUsername = "client", FhirPassword = "secret" };
        FhirSubmissionResponse response = await new FhirSubmissionClient(httpClient, options).SubmitAsync(new JsonObject { ["resourceType"] = "Bundle" }, "Jane Doe", CancellationToken.None);

        Assert.Equal("Error", response.Status);
        Assert.Equal(502, response.StatusCode);
        Assert.Equal("connection", response.State);
    }

    [Fact]
    public async Task ReturnsCorrectLocationForSuccess()
    {
        HttpRequestMessage? captured = null;
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Created) { Headers = { Location = new Uri("https://fhir.test/Bundle/123") } };
        }));
        ImportApiOptions options = new() { FhirEndpoint = "https://fhir.test", FhirUsername = "client", FhirPassword = "secret" };
        FhirSubmissionResponse response = await new FhirSubmissionClient(httpClient, options).SubmitAsync(new JsonObject { ["resourceType"] = "Bundle" }, "Jane Doe", CancellationToken.None);

        Assert.Equal("https://fhir.test/Bundle/123", response.Location);
    }

    [Fact]
    public async Task ReturnsEmptyLocationForNonSuccess()
    {
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        ImportApiOptions options = new() { FhirEndpoint = "https://fhir.test", FhirUsername = "client", FhirPassword = "secret" };
        FhirSubmissionResponse response = await new FhirSubmissionClient(httpClient, options).SubmitAsync(new JsonObject { ["resourceType"] = "Bundle" }, "Jane Doe", CancellationToken.None);

        Assert.Null(response.Location);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
