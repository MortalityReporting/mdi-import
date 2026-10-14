using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using MortalityReporting.ImportApi.Models;

namespace MortalityReporting.ImportApi.Parsing;

public sealed class ImportFileParser
{
    public async Task<IReadOnlyList<ImportRecord>> ParseAsync(Stream input, string fileName, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".csv" => await ParseCsvAsync(input, cancellationToken),
            ".xlsx" => ParseXlsx(input),
            _ => throw new ImportParseException("Only .csv and .xlsx files are supported.")
        };
    }

    private static async Task<IReadOnlyList<ImportRecord>> ParseCsvAsync(Stream input, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(input, leaveOpen: true);
        CsvConfiguration configuration = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim,
            BadDataFound = context => throw new ImportParseException($"Malformed CSV near row {context.Field}."),
            MissingFieldFound = null
        };
        using CsvReader csv = new(reader, configuration);
        if (!await csv.ReadAsync()) throw new ImportParseException("CSV file must contain a header row.");
        csv.ReadHeader();
        string[] headers = csv.HeaderRecord?.Select(header => header.Trim()).ToArray() ?? [];
        if (headers.Length == 0 || headers.All(string.IsNullOrWhiteSpace)) throw new ImportParseException("CSV file must contain a header row.");

        List<ImportRecord> records = [];
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportRecord record = new();
            for (int index = 0; index < headers.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(headers[index])) continue;
                record.ExpectedFields.Add(headers[index]);
                record.Fields[headers[index]] = csv.GetField(index)?.Trim();
            }
            if (record.HasData) records.Add(record);
        }
        return FinalizeCaseIdentities(records);
    }

    private static IReadOnlyList<ImportRecord> ParseXlsx(Stream input)
    {
        try
        {
            using XLWorkbook workbook = new(input);
            List<ImportRecord> schemaRecords = [];
            bool foundSchema = false;
            ImportTemplateKind schemaKind = workbook.Worksheets.Any(sheet =>
                ImportRecord.Normalize(sheet.Name).Contains("dcrextension") || ImportRecord.Normalize(sheet.Name).Contains("dcrtemplate"))
                ? ImportTemplateKind.Dcr
                : ImportTemplateKind.MdiAndEdrs;
            foreach (IXLWorksheet worksheet in workbook.Worksheets)
            {
                IXLRange? used = worksheet.RangeUsed();
                if (used is null) continue;
                List<List<string?>> rows = used.Rows().Select(row => row.Cells().Select(cell => NormalizeCell(cell.Value)).ToList()).ToList();
                if (rows.Count == 0) continue;
                string firstValue = rows[0].FirstOrDefault() ?? "";
                if (ImportRecord.Normalize(firstValue) == "toxicologytomdischema" || ImportRecord.Normalize(firstValue) == "toxicologytomditemplate")
                {
                    ImportRecord tox = ParseToxicology(rows);
                    if (tox.HasData) return [tox];
                    continue;
                }

                List<(int Index, string Name)> caseColumns = rows[0]
                    .Select((value, index) => (value, index))
                    .Where(item => ImportRecord.Normalize(item.value ?? "").StartsWith("case") && ImportRecord.Normalize(item.value ?? "").Length > 4)
                    .Select(item => (item.index, item.value ?? $"Case {item.index}"))
                    .ToList();
                if (caseColumns.Count > 0)
                {
                    foundSchema = true;
                    List<ImportRecord> sheetRecords = ParseSchema(rows, caseColumns, schemaKind);
                    MergeSchemaRecords(schemaRecords, sheetRecords);
                }
                else if (!foundSchema)
                {
                    foreach (ImportRecord record in ParseFlat(rows)) schemaRecords.Add(record);
                }
            }
            List<ImportRecord> records = foundSchema
                ? schemaRecords.Where(record => record.HasCaseSpecificData).ToList()
                : schemaRecords.Where(record => record.HasData).ToList();
            return FinalizeCaseIdentities(records);
        }
        catch (ImportParseException) { throw; }
        catch (Exception exception) { throw new ImportParseException($"Unable to read XLSX workbook: {exception.Message}"); }
    }

    private static List<ImportRecord> ParseSchema(List<List<string?>> rows, List<(int Index, string Name)> caseColumns, ImportTemplateKind kind)
    {
        List<ImportRecord> records = caseColumns
            .Select(column => new ImportRecord(kind) { SourceKey = column.Name })
            .ToList();
        foreach (List<string?> row in rows.Skip(1))
        {
            string? section = Get(row, 0);
            string? element = Get(row, 1);
            if (string.IsNullOrWhiteSpace(element) && ImportRecord.Normalize(section ?? "") != "fileid") continue;
            string? key = !string.IsNullOrWhiteSpace(element) ? element : section;
            if (ImportRecord.Normalize(section ?? "") == "fileid") key = "File Id";
            if (string.IsNullOrWhiteSpace(key) || ImportRecord.Normalize(key) == "endofcases") continue;

            bool hasCaseValue = caseColumns.Any(column => !string.IsNullOrWhiteSpace(Get(row, column.Index)));
            if (!hasCaseValue && !string.IsNullOrWhiteSpace(section) && !string.IsNullOrWhiteSpace(element))
            {
                foreach (ImportRecord record in records)
                {
                    record.ExpectedFields.Add(key);
                    record.Fields[key] = element;
                }
                continue;
            }
            foreach (ImportRecord record in records) record.ExpectedFields.Add(key);
            for (int index = 0; index < caseColumns.Count; index++)
            {
                string? value = Get(row, caseColumns[index].Index);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    records[index].Fields[key] = value;
                    records[index].HasCaseSpecificData = true;
                }
            }
        }
        return records;
    }

    private static List<ImportRecord> ParseFlat(List<List<string?>> rows)
    {
        if (rows.Count < 2) return [];
        string[] headers = rows[0].Select(value => value?.Trim() ?? "").ToArray();
        List<ImportRecord> records = [];
        foreach (List<string?> row in rows.Skip(1))
        {
            ImportRecord record = new();
            for (int index = 0; index < headers.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(headers[index])) record.Fields[headers[index]] = Get(row, index);
                if (!string.IsNullOrWhiteSpace(headers[index])) record.ExpectedFields.Add(headers[index]);
            }
            if (record.HasData) records.Add(record);
        }
        return records;
    }

    private static ImportRecord ParseToxicology(List<List<string?>> rows)
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        string? activeTable = null;
        List<(int Index, string Name)> tableColumns = [];
        bool awaitingTableHeaders = false;
        int pendingLabelRow = -1;
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            List<string?> row = rows[rowIndex];
            string? first = Get(row, 0);
            string normalizedFirst = ImportRecord.Normalize(first ?? "");

            if (normalizedFirst is "specimens" or "results" or "notes")
            {
                activeTable = normalizedFirst switch
                {
                    "specimens" => "Specimens",
                    "results" => "Results",
                    _ => "Notes"
                };
                tableColumns = GetTableColumns(row, 1);
                awaitingTableHeaders = tableColumns.Count == 0;
                record.Tables.TryAdd(activeTable, []);
                continue;
            }

            // The redesigned template uses a "General Comments" row whose
            // second cell is the Notes table header, followed by rows with
            // the note text in column B.
            if (normalizedFirst == "generalcomments")
            {
                activeTable = "Notes";
                tableColumns = GetTableColumns(row, 1);
                if (tableColumns.Count == 0) tableColumns.Add((1, "Note Content"));
                awaitingTableHeaders = false;
                record.Tables.TryAdd(activeTable, []);
                continue;
            }

            if (activeTable is not null && awaitingTableHeaders)
            {
                tableColumns = GetTableColumns(row, 0);
                awaitingTableHeaders = false;
                continue;
            }

            if (activeTable is not null && tableColumns.Count > 0)
            {
                if (IsToxicologySectionBoundary(normalizedFirst))
                {
                    activeTable = null;
                    tableColumns = [];
                    awaitingTableHeaders = false;
                }
                else
                {
                    Dictionary<string, string?> tableRow = new(StringComparer.OrdinalIgnoreCase);
                    foreach ((int index, string name) in tableColumns)
                    {
                        tableRow[name] = Get(row, index);
                        record.ExpectedFields.Add($"{activeTable}[{record.Tables[activeTable].Count}].{name}");
                    }
                    if (tableRow.Values.Any(value => !string.IsNullOrWhiteSpace(value))) record.Tables[activeTable].Add(tableRow);
                    continue;
                }
            }

            if (pendingLabelRow >= 0 && !string.IsNullOrWhiteSpace(first))
            {
                record.ExpectedFields.Add("File Id");
                record.Fields["File Id"] = first.Trim();
                pendingLabelRow = -1;
                continue;
            }

            if (string.IsNullOrWhiteSpace(first)) continue;
            if (normalizedFirst is "laboratory" or "decedent" or "agencyname") continue;

            if (normalizedFirst == "fileid")
            {
                record.ExpectedFields.Add("File Id");
                string? fileId = Get(row, 1);
                if (!string.IsNullOrWhiteSpace(fileId)) record.Fields["File Id"] = fileId.Trim();
                else pendingLabelRow = rowIndex;
                continue;
            }

            record.ExpectedFields.Add(first.Trim());
            record.Fields[first.Trim()] = Get(row, 1);
        }
        return record;
    }

    private static List<(int Index, string Name)> GetTableColumns(List<string?> row, int minimumIndex)
    {
        return row
            .Select((value, index) => (value, index))
            .Where(item => item.index >= minimumIndex && !string.IsNullOrWhiteSpace(item.value))
            .Select(item => (item.index, item.value!.Trim()))
            .ToList();
    }

    private static bool IsToxicologySectionBoundary(string normalizedFirst) =>
        normalizedFirst is "specimens" or "results" or "notes" or "generalcomments" or "laboratory" or "decedent" or "agencyname" or "fileid";

    private static void MergeSchemaRecords(List<ImportRecord> destination, List<ImportRecord> source)
    {
        for (int index = 0; index < source.Count; index++)
        {
            ImportRecord sourceRecord = source[index];
            int destinationIndex = sourceRecord.SourceKey is null
                ? index < destination.Count ? index : -1
                : destination.FindIndex(record => string.Equals(record.SourceKey, sourceRecord.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (destinationIndex < 0)
            {
                destination.Add(sourceRecord);
                continue;
            }
            ImportRecord destinationRecord = destination[destinationIndex];
            destinationRecord.HasCaseSpecificData |= sourceRecord.HasCaseSpecificData;
            foreach (string field in sourceRecord.ExpectedFields) destinationRecord.ExpectedFields.Add(field);
            foreach ((string key, string? value) in sourceRecord.Fields) destinationRecord.Fields[key] = value;
            foreach ((string key, List<Dictionary<string, string?>> value) in sourceRecord.Tables) destinationRecord.Tables[key] = value;
        }
    }

    private static IReadOnlyList<ImportRecord> FinalizeCaseIdentities(List<ImportRecord> records)
    {
        Dictionary<string, List<ImportRecord>> keyedGroups = records
            .GroupBy(record => ImportRecord.Normalize(record.Get("BASEFHIRID", "File Id", "MDICASEID", "EDRSCASEID") ?? ""), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (List<ImportRecord> group in keyedGroups.Values.Where(group => group.Count > 1))
        {
            AssignDiscriminators(group);
        }

        foreach (List<ImportRecord> group in records
            .Where(record => string.IsNullOrWhiteSpace(record.Get("BASEFHIRID", "File Id", "MDICASEID", "EDRSCASEID")))
            .GroupBy(record => record.CanonicalFingerprint(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.ToList()))
        {
            AssignDiscriminators(group);
        }
        return records;
    }

    private static void AssignDiscriminators(List<ImportRecord> records)
    {
        foreach (IGrouping<string, ImportRecord> fingerprintGroup in records.GroupBy(record => record.CanonicalFingerprint(), StringComparer.Ordinal))
        {
            List<ImportRecord> matching = fingerprintGroup.ToList();
            if (matching.Count == 1)
            {
                matching[0].IdentityDiscriminator = fingerprintGroup.Key;
                continue;
            }
            if (matching.Any(record => string.IsNullOrWhiteSpace(record.SourceKey)))
                throw new ImportParseException("Multiple identical cases share the same case identifier. Provide a distinct case identifier for each case.");
            foreach (ImportRecord record in matching) record.IdentityDiscriminator = record.SourceKey;
        }
    }

    private static string? Get(List<string?> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

    private static string? NormalizeCell(XLCellValue value) => value.Type switch
    {
        XLDataType.DateTime => value.GetDateTime().ToString("O", CultureInfo.InvariantCulture),
        XLDataType.Number => value.GetNumber().ToString(CultureInfo.InvariantCulture),
        XLDataType.Boolean => value.GetBoolean().ToString(),
        XLDataType.TimeSpan => value.GetTimeSpan().ToString(),
        XLDataType.Blank => null,
        _ => value.ToString().Trim()
    };
}
