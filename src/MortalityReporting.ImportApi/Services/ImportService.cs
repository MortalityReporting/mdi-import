using System.Text.Json.Nodes;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Parsing;

namespace MortalityReporting.ImportApi.Services;

public sealed class ImportService(ImportFileParser parser, MdiFhirMapper mapper, IFhirSubmissionClient submissionClient)
{
    public async Task<IReadOnlyList<ImportRecordResponse>> ProcessAsync(Stream input, string fileName, CancellationToken cancellationToken)
    {
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(input, fileName, cancellationToken);
        List<ImportRecordResponse> responses = [];
        foreach (ImportRecord record in records)
        {
            ImportRecordResponse response = new()
            {
                Fields = CreateFieldStatuses(record)
            };
            try
            {
                JsonObject bundle = mapper.Map(record);
                FhirSubmissionResponse submission = await submissionClient.SubmitAsync(bundle, DisplayName(record), cancellationToken);
                responses.Add(new ImportRecordResponse { FhirBundle = bundle, Fields = response.Fields, FhirResponse = submission });
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
            {
                responses.Add(new ImportRecordResponse { Fields = response.Fields, Errors = [exception.Message], FhirResponse = new FhirSubmissionResponse { Name = DisplayName(record), Status = "Error", State = "mapping" } });
            }
        }
        return responses;
    }

    private static Dictionary<string, FieldMappingStatus> CreateFieldStatuses(ImportRecord record)
    {
        Dictionary<string, FieldMappingStatus> fields = new(StringComparer.Ordinal);
        foreach ((string key, string? value) in record.Fields)
        {
            string text = value ?? "";
            fields[key] = new FieldMappingStatus { Status = string.IsNullOrWhiteSpace(text) ? "not mapped" : "mapped", Value = text };
        }
        return fields;
    }

    private static string DisplayName(ImportRecord record) =>
        string.Join(' ', new[] { record.Get("FIRSTNAME", "First Name"), record.Get("MIDNAME", "Middle Name"), record.Get("LASTNAME", "Last Name"), record.Get("Decedent Name") }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
}
