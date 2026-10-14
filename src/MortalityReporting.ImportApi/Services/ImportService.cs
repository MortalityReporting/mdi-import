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
            List<string> validationErrors = response.Fields
                .Where(field => field.Value.Errors.Count > 0)
                .SelectMany(field => field.Value.Errors.Select(error => $"{field.Key}: {error}"))
                .ToList();
            if (validationErrors.Count > 0)
            {
                responses.Add(new ImportRecordResponse
                {
                    Fields = response.Fields,
                    Errors = validationErrors,
                    FhirResponse = new FhirSubmissionResponse { Name = DisplayName(record), Status = "Error", StatusCode = 422, State = "validation" }
                });
                continue;
            }
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
        foreach (string key in record.AllFieldNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            string text = record.Fields.GetValueOrDefault(key) ?? "";
            List<string> errors = ImportFieldValidator.Validate(key, text).ToList();
            fields[key] = new FieldMappingStatus { Status = string.IsNullOrWhiteSpace(text) || errors.Count > 0 ? "not mapped" : "mapped", Value = text, Errors = errors };
        }
        foreach ((string table, List<Dictionary<string, string?>> rows) in record.Tables)
        {
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                foreach ((string key, string? value) in rows[rowIndex])
                {
                    string fieldName = $"{table}[{rowIndex}].{key}";
                    string text = value ?? "";
                    List<string> errors = ImportFieldValidator.Validate(fieldName, text).ToList();
                    fields[fieldName] = new FieldMappingStatus { Status = string.IsNullOrWhiteSpace(text) || errors.Count > 0 ? "not mapped" : "mapped", Value = text, Errors = errors };
                }
            }
        }
        return fields;
    }

    private static string DisplayName(ImportRecord record) =>
        string.Join(' ', new[] { record.Get("FIRSTNAME", "First Name"), record.Get("MIDNAME", "Middle Name"), record.Get("LASTNAME", "Last Name"), record.Get("Decedent Name") }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
}
