using System.Text;
using System.Text.Json.Nodes;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Parsing;
using MortalityReporting.ImportApi.Services;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

public sealed class ImportServiceTests
{
    [Fact]
    public async Task IncludesEmptyFieldsAndRejectsInvalidFields()
    {
        FakeSubmissionClient submission = new();
        ImportService service = new(new ImportFileParser(), new MdiFhirMapper(), submission);
        using MemoryStream input = new(Encoding.UTF8.GetBytes("FIRSTNAME,LASTNAME,CDEATHDATE,CAUSEA\nHan,Solo,not-a-date,\n"));

        IReadOnlyList<ImportRecordResponse> responses = await service.ProcessAsync(input, "case.csv", CancellationToken.None);

        Assert.Single(responses);
        Assert.Equal("mapped", responses[0].Fields["FIRSTNAME"].Status);
        Assert.Equal("not mapped", responses[0].Fields["CAUSEA"].Status);
        Assert.Equal("not mapped", responses[0].Fields["CDEATHDATE"].Status);
        Assert.NotEmpty(responses[0].Fields["CDEATHDATE"].Errors);
        Assert.Equal(422, responses[0].FhirResponse!.StatusCode);
        Assert.Empty(submission.SubmittedBundles);
    }

    private sealed class FakeSubmissionClient : IFhirSubmissionClient
    {
        public List<JsonObject> SubmittedBundles { get; } = [];

        public Task<FhirSubmissionResponse> SubmitAsync(JsonObject bundle, string name, CancellationToken cancellationToken)
        {
            SubmittedBundles.Add(bundle);
            return Task.FromResult(new FhirSubmissionResponse { Name = name, Status = "Success", StatusCode = 201 });
        }
    }
}
