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
                record.Fields[headers[index]] = csv.GetField(index)?.Trim();
            }
            if (record.HasData) records.Add(record);
        }
        return records;
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
            return records;
        }
        catch (ImportParseException) { throw; }
        catch (Exception exception) { throw new ImportParseException($"Unable to read XLSX workbook: {exception.Message}"); }
    }

    private static List<ImportRecord> ParseSchema(List<List<string?>> rows, List<(int Index, string Name)> caseColumns, ImportTemplateKind kind)
    {
        List<ImportRecord> records = caseColumns.Select(_ => new ImportRecord(kind)).ToList();
        foreach (List<string?> row in rows.Skip(1))
        {
            string? section = Get(row, 0);
            string? element = Get(row, 1);
            string? key = !string.IsNullOrWhiteSpace(element) ? element : section;
            if (string.IsNullOrWhiteSpace(key) || ImportRecord.Normalize(key) == "endofcases") continue;

            bool hasCaseValue = caseColumns.Any(column => !string.IsNullOrWhiteSpace(Get(row, column.Index)));
            if (!hasCaseValue && !string.IsNullOrWhiteSpace(section) && !string.IsNullOrWhiteSpace(element))
            {
                foreach (ImportRecord record in records) record.Fields[section] = element;
                continue;
            }
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
            }
            if (record.HasData) records.Add(record);
        }
        return records;
    }

    private static ImportRecord ParseToxicology(List<List<string?>> rows)
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        string? activeTable = null;
        List<string> tableHeaders = [];
        int pendingLabelRow = -1;
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            List<string?> row = rows[rowIndex];
            string? first = Get(row, 0);
            if (ImportRecord.Normalize(first ?? "") is "specimens" or "results" or "notes")
            {
                activeTable = first!.Trim();
                tableHeaders = row.Skip(1).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToList();
                record.Tables.TryAdd(activeTable, []);
                continue;
            }
            if (activeTable is not null && string.IsNullOrWhiteSpace(first) && tableHeaders.Count > 0)
            {
                Dictionary<string, string?> tableRow = new(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < tableHeaders.Count; index++) tableRow[tableHeaders[index]] = Get(row, index + 1);
                if (tableRow.Values.Any(value => !string.IsNullOrWhiteSpace(value))) record.Tables[activeTable].Add(tableRow);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(first) && ImportRecord.Normalize(first) is "results" or "notes") { activeTable = null; }
            if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(Get(row, 1)) && !ImportRecord.Normalize(first).Equals("laboratory", StringComparison.OrdinalIgnoreCase))
            {
                record.Fields[first.Trim()] = Get(row, 1);
            }
            else if (ImportRecord.Normalize(first ?? "") == "fileid")
            {
                pendingLabelRow = rowIndex;
            }
            else if (pendingLabelRow >= 0 && !string.IsNullOrWhiteSpace(first))
            {
                record.Fields["File Id"] = first.Trim();
                pendingLabelRow = -1;
            }
        }
        return record;
    }

    private static void MergeSchemaRecords(List<ImportRecord> destination, List<ImportRecord> source)
    {
        while (destination.Count < source.Count) destination.Add(new ImportRecord(source.FirstOrDefault()?.Kind ?? ImportTemplateKind.MdiAndEdrs));
        for (int index = 0; index < source.Count; index++)
        {
            destination[index].HasCaseSpecificData |= source[index].HasCaseSpecificData;
            foreach ((string key, string? value) in source[index].Fields) destination[index].Fields[key] = value;
            foreach ((string key, List<Dictionary<string, string?>> value) in source[index].Tables) destination[index].Tables[key] = value;
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
