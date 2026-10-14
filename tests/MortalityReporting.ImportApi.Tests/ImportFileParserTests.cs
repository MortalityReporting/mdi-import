using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Parsing;
using MortalityReporting.ImportApi.Services;
using Xunit;
using System.IO;
using System.Linq;
using GaTech.Chai.Mdi;

namespace MortalityReporting.ImportApi.Tests;

public sealed class ImportFileParserTests
{
    private readonly ImportFileParser parser = new();

    [Fact]
    public async Task ParsesCsvRowsWithQuotedFields()
    {
        const string csv = "BASEFHIRID,FIRSTNAME,LASTNAME,CAUSEA\ncase-1,Jane,Doe,\"Fentanyl, toxicity\"\n";
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);
        Assert.Single(records);
        Assert.Equal("Fentanyl, toxicity", records[0].Get("CAUSEA"));
    }

    [Fact]
    public async Task ParsesCsvCertifierIdentifierFieldsThroughMapping()
    {
        const string csv = "BASEFHIRID,FIRSTNAME,LASTNAME,CERTIFIER_IDENTIFIER_SYSTEM,CERTIFIER_IDENTIFIER\ncase-1,Jane,Doe,NPI,1234567890\n";
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);

        Assert.Single(records);
        JsonObject practitioner = new MdiFhirMapper().Map(records[0])["entry"]!.AsArray()
            .First(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Practitioner")!["resource"]!.AsObject();
        JsonObject identifier = practitioner["identifier"]![0]!.AsObject();
        Assert.Equal("http://hl7.org/fhir/sid/us-npi", identifier["system"]!.GetValue<string>());
        Assert.Equal("1234567890", identifier["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ParsesSchemaWorkbookAndGlobalFileId()
    {
        using MemoryStream stream = new();
        using (XLWorkbook workbook = new())
        {
            IXLWorksheet sheet = workbook.AddWorksheet("MDI RAVEN Input Schema");
            sheet.Cell(1, 1).Value = "Sections";
            sheet.Cell(1, 2).Value = "Elements";
            sheet.Cell(1, 4).Value = "Case 1";
            sheet.Cell(1, 5).Value = "Case 2";
            sheet.Cell(2, 1).Value = "File Id";
            sheet.Cell(2, 2).Value = "file-1";
            sheet.Cell(3, 2).Value = "Decedent Name";
            sheet.Cell(3, 4).Value = "Jane Doe";
            sheet.Cell(3, 5).Value = "John Doe";
            sheet.Cell(4, 2).Value = "Cause of Death Part I Line a";
            sheet.Cell(4, 4).Value = "Natural causes";
            workbook.SaveAs(stream);
        }
        stream.Position = 0;
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(stream, "case.xlsx", CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.Equal("file-1", records[0].Get("File Id"));
        Assert.Equal("Jane Doe", records[0].Get("Decedent Name"));
        Assert.Equal("file-1", records[1].Get("File Id"));
        Assert.Null(records[1].Get("Cause of Death Part I Line a"));
    }

    [Fact]
    public async Task RejectsUnsupportedExtension()
    {
        ImportParseException exception = await Assert.ThrowsAsync<ImportParseException>(() => parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes("x")), "case.txt", CancellationToken.None));
        Assert.Contains("Only .csv and .xlsx", exception.Message);
    }

    [Fact]
    public async Task ReturnsNoRecordsForHeaderOnlyCsv()
    {
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes("FIRSTNAME,LASTNAME\n")), "empty.csv", CancellationToken.None);
        Assert.Empty(records);
    }

    [Fact]
    public async Task ClassifiesDcrWorkbookAsDcr()
    {
        using MemoryStream stream = new();
        using (XLWorkbook workbook = new())
        {
            IXLWorksheet sheet = workbook.AddWorksheet("Base Input Schema");
            sheet.Cell(1, 1).Value = "Sections";
            sheet.Cell(1, 2).Value = "Elements";
            sheet.Cell(1, 4).Value = "Case 1";
            sheet.Cell(2, 2).Value = "Decedent Name";
            sheet.Cell(2, 4).Value = "Jane Doe";
            IXLWorksheet extension = workbook.AddWorksheet("DCR Extension Input Schema");
            extension.Cell(1, 1).Value = "Sections";
            extension.Cell(1, 2).Value = "Elements";
            extension.Cell(1, 4).Value = "Case 1";
            extension.Cell(2, 2).Value = "Message Reason";
            extension.Cell(2, 4).Value = "DC_MED_DATA_Q_REQ";
            workbook.SaveAs(stream);
        }
        stream.Position = 0;
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(stream, "DCR-Template.xlsx", CancellationToken.None);
        Assert.Single(records);
        Assert.Equal(ImportTemplateKind.Dcr, records[0].Kind);
        Assert.Equal("DC_MED_DATA_Q_REQ", records[0].Get("Message Reason"));
    }

    [Fact]
    public async Task DuplicateImportRecordsYieldIdenticalResourceIdsAndFullUrls()
    {
        const string csv = "BASEFHIRID,FIRSTNAME,LASTNAME\ncase-1,Jane,Doe\n";
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);
        ImportRecord record = records[0];

        // Map the same record twice
        JsonObject bundle1 = new MdiFhirMapper().Map(record);
        JsonObject bundle2 = new MdiFhirMapper().Map(record);

        // Collect resource IDs and full URLs from both bundles
        Dictionary<string, (string Id, string FullUrl)> resources1 = CollectResourceIdsAndUrls(bundle1);
        Dictionary<string, (string Id, string FullUrl)> resources2 = CollectResourceIdsAndUrls(bundle2);

        // Verify that the same record mapped twice yields identical resource ID and full URL pairs
        Assert.Equal(resources1.Count, resources2.Count);
        foreach (var kvp in resources1)
        {
            Assert.True(resources2.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value.Id, resources2[kvp.Key].Id);
            Assert.Equal(kvp.Value.FullUrl, resources2[kvp.Key].FullUrl);
        }

        // Verify all IDs are unique within each bundle and <= 64 characters
        var allIds1 = resources1.Values.Select(r => r.Id);
        Assert.All(allIds1, id => Assert.True(id.Length <= 64, $"Resource ID '{id}' exceeds 64 characters"));
        int distinctCount1 = allIds1.Distinct().Count();
        int totalCount1 = allIds1.Count();
        Assert.True(distinctCount1 == totalCount1, "Resource IDs are not unique within the first bundle");

        var allIds2 = resources2.Values.Select(r => r.Id);
        Assert.All(allIds2, id => Assert.True(id.Length <= 64, $"Resource ID '{id}' exceeds 64 characters"));
        int distinctCount2 = allIds2.Distinct().Count();
        int totalCount2 = allIds2.Count();
        Assert.True(distinctCount2 == totalCount2, "Resource IDs are not unique within the second bundle");
    }

    [Fact]
    public async Task SameCsvParsedTwiceYieldsIdenticalResourceIds()
    {
        const string csv = "BASEFHIRID,FIRSTNAME,LASTNAME,CAUSEA\ncase-1,Jane,Doe,Fentanyl toxicity\ncase-2,John,Smith,Natural causes\n";
        IReadOnlyList<ImportRecord> records1 = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);

        // Parse the same CSV again
        IReadOnlyList<ImportRecord> records2 = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);

        // Map all records from both parses
        List<JsonObject> bundles1 = records1.Select(r => new MdiFhirMapper().Map(r)).ToList();
        List<JsonObject> bundles2 = records2.Select(r => new MdiFhirMapper().Map(r)).ToList();

        // Compare resource IDs between the two parses
        for (int i = 0; i < bundles1.Count; i++)
        {
            Dictionary<string, (string Id, string FullUrl)> resources1 = CollectResourceIdsAndUrls(bundles1[i]);
            Dictionary<string, (string Id, string FullUrl)> resources2 = CollectResourceIdsAndUrls(bundles2[i]);

            Assert.Equal(resources1.Count, resources2.Count);
            foreach (var kvp in resources1)
            {
                Assert.True(resources2.ContainsKey(kvp.Key));
                Assert.Equal(kvp.Value.Id, resources2[kvp.Key].Id);
                Assert.Equal(kvp.Value.FullUrl, resources2[kvp.Key].FullUrl);
            }
        }
    }

    [Fact]
    public async Task RecordsWithSameFileIdButDifferentCaseIdentifiersYieldDistinctResourceIds()
    {
        const string csv = "File Id,BASEFHIRID,FIRSTNAME,LASTNAME\nfile-1,case-1,Jane,Doe\nfile-1,case-2,John,Smith\n";
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);

        Assert.Equal(2, records.Count);

        // Map both records
        JsonObject bundle1 = new MdiFhirMapper().Map(records[0]);
        JsonObject bundle2 = new MdiFhirMapper().Map(records[1]);

        // Collect resource IDs from both bundles
        HashSet<string> ids1 = CollectResourceIds(bundle1);
        HashSet<string> ids2 = CollectResourceIds(bundle2);

        // Verify that records with different case identifiers have different resource IDs
        HashSet<string> overlap = [.. ids1.Intersect(ids2)];
        Assert.Empty(overlap);

        // Verify that each record's resource IDs are internally consistent
        Assert.All(ids1, id => Assert.True(id.Length <= 64, $"Resource ID '{id}' exceeds 64 characters"));
        Assert.All(ids2, id => Assert.True(id.Length <= 64, $"Resource ID '{id}' exceeds 64 characters"));
    }

    [Fact]
    public async Task SchemaXlsxWithTwoCaseColumnsSharingGlobalFileIdYieldsDistinctStableIds()
    {
        using MemoryStream stream = new();
        using (XLWorkbook workbook = new())
        {
            IXLWorksheet sheet = workbook.AddWorksheet("MDI RAVEN Input Schema");
            sheet.Cell(1, 1).Value = "Sections";
            sheet.Cell(1, 2).Value = "Elements";
            sheet.Cell(1, 4).Value = "Case 1";
            sheet.Cell(1, 5).Value = "Case 2";
            sheet.Cell(2, 1).Value = "File Id";
            sheet.Cell(2, 2).Value = "global-file-1";
            sheet.Cell(3, 2).Value = "Decedent Name";
            sheet.Cell(3, 4).Value = "Jane Doe";
            sheet.Cell(3, 5).Value = "John Smith";
            sheet.Cell(4, 2).Value = "Cause of Death Part I Line a";
            sheet.Cell(4, 4).Value = "Natural causes";
            sheet.Cell(4, 5).Value = "Accidental overdose";
            workbook.SaveAs(stream);
        }
        stream.Position = 0;
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(stream, "schema.xlsx", CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.Equal("global-file-1", records[0].Get("File Id"));
        Assert.Equal("Jane Doe", records[0].Get("Decedent Name"));
        Assert.Equal("global-file-1", records[1].Get("File Id"));
        Assert.Equal("John Smith", records[1].Get("Decedent Name"));

        // Map both records
        JsonObject bundle1 = new MdiFhirMapper().Map(records[0]);
        JsonObject bundle2 = new MdiFhirMapper().Map(records[1]);

        // Collect resource IDs from both bundles
        HashSet<string> ids1 = CollectResourceIds(bundle1);
        HashSet<string> ids2 = CollectResourceIds(bundle2);

        // Records with the same File Id but different case data have distinct resource IDs
        HashSet<string> overlap = [.. ids1.Intersect(ids2)];
        Assert.Empty(overlap);

        // Parse and map the same XLSX again to verify determinism
        stream.Position = 0;
        IReadOnlyList<ImportRecord> recordsAgain = await parser.ParseAsync(stream, "schema.xlsx", CancellationToken.None);
        JsonObject bundle1Again = new MdiFhirMapper().Map(recordsAgain[0]);
        JsonObject bundle2Again = new MdiFhirMapper().Map(recordsAgain[1]);

        HashSet<string> ids1Again = CollectResourceIds(bundle1Again);
        HashSet<string> ids2Again = CollectResourceIds(bundle2Again);

        // Verify that repeated parsing yields identical IDs
        Assert.Equal(ids1, ids1Again);
        Assert.Equal(ids2, ids2Again);
    }

    [Fact]
    public async Task RecordsWithSameBaseFhirIdAndDifferentContentYieldDistinctResourceIds()
    {
        const string csv = "BASEFHIRID,FIRSTNAME,LASTNAME\ncase-1,Jane,Doe\ncase-1,John,Smith\n";
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None);

        // Both records should have the same BASEFHIRID
        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].Get("BASEFHIRID"), records[1].Get("BASEFHIRID"));

        // Mapping should produce distinct resource IDs because these are different cases.
        JsonObject bundle1 = new MdiFhirMapper().Map(records[0]);
        JsonObject bundle2 = new MdiFhirMapper().Map(records[1]);

        HashSet<string> ids1 = CollectResourceIds(bundle1);
        HashSet<string> ids2 = CollectResourceIds(bundle2);

        HashSet<string> overlap = [.. ids1.Intersect(ids2)];
        Assert.Empty(overlap);
    }

    [Fact]
    public async Task RejectsIdenticalCsvCasesSharingCaseIdentifier()
    {
        const string csv = "File Id,FIRSTNAME,LASTNAME\nfile-1,Jane,Doe\nfile-1,Jane,Doe\n";
        ImportParseException exception = await Assert.ThrowsAsync<ImportParseException>(() =>
            parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "case.csv", CancellationToken.None));
        Assert.Contains("identical cases", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, (string Id, string FullUrl)> CollectResourceIdsAndUrls(JsonObject bundle)
    {
        Dictionary<string, (string Id, string FullUrl)> resources = new();

        if (bundle["entry"] is JsonArray entries)
        {
            foreach (JsonNode? entry in entries)
            {
                if (entry is JsonObject entryObj && entryObj["resource"] is JsonObject resource)
                {
                    string? resourceType = resource["resourceType"]?.GetValue<string>();
                    string? id = resource["id"]?.GetValue<string>();
                    string? fullUrl = entryObj["fullUrl"]?.GetValue<string>();

                    if (resourceType != null && id != null)
                    {
                        string key = $"{resourceType}/{id}";
                        resources[key] = (id, fullUrl ?? "");
                    }
                }
            }
        }

        return resources;
    }

    private static HashSet<string> CollectResourceIds(JsonObject bundle)
    {
        HashSet<string> ids = new();

        if (bundle["entry"] is JsonArray entries)
        {
            foreach (JsonNode? entry in entries)
            {
                if (entry is JsonObject entryObj && entryObj["resource"] is JsonObject resource)
                {
                    string? resourceType = resource["resourceType"]?.GetValue<string>();
                    string? id = resource["id"]?.GetValue<string>();

                    if (resourceType != null && id != null)
                    {
                        ids.Add($"{resourceType}/{id}");
                    }
                }
            }
        }

        return ids;
    }

    [Fact]
    public async Task ParsesRepositoryFixtureToxToMdiHanSolo()
    {
        string? fixturePath = FindRepositoryFixture("Tox-To-MDI-HanSolo.xlsx");
        Assert.NotNull(fixturePath);

        await using FileStream stream = File.OpenRead(fixturePath!);
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(stream, "Tox-To-MDI-HanSolo.xlsx", CancellationToken.None);

        Assert.Single(records);
        ImportRecord record = records[0];
        Assert.Equal(ImportTemplateKind.Toxicology, record.Kind);
        Assert.Equal("MDI-DEV-GOLDENSET", record.Get("File Id"));
        Assert.Equal("Han Solo", record.Get("Decedent Name"));
        Assert.Equal(6, record.Tables["Specimens"].Count);
        Assert.Equal(13, record.Tables["Results"].Count);
        Assert.Single(record.Tables["Notes"]);
        Assert.StartsWith("It may be appropriate", record.Tables["Notes"][0]["Note Content"]);
        Assert.DoesNotContain(record.Tables["Results"], row => row.Values.Any(value => value?.StartsWith("It may be appropriate", StringComparison.Ordinal) == true));

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.NotNull(bundle["entry"]);

        HashSet<string> resourceTypes = new();
        if (bundle["entry"] is JsonArray resourceEntries)
        {
            foreach (JsonNode? entry in resourceEntries)
            {
                if (entry is JsonObject entryObj && entryObj["resource"] is JsonObject resource)
                {
                    resourceTypes.Add(resource["resourceType"]?.GetValue<string>() ?? "");
                }
            }
        }

        Assert.Contains("DiagnosticReport", resourceTypes);
        Assert.Contains("Observation", resourceTypes);
        Assert.Contains("Organization", resourceTypes);

        JsonArray entries = bundle["entry"]!.AsArray();
        IEnumerable<JsonObject> entryObjects = entries.Select(entry => entry!.AsObject());
        JsonObject patient = entryObjects
            .First(entry => entry["resource"]!["resourceType"]?.GetValue<string>() == "Patient")["resource"]!.AsObject();
        Assert.Equal("123-45-6789", patient["identifier"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("http://hl7.org/fhir/sid/us-ssn", patient["identifier"]![0]!["system"]!.GetValue<string>());

        JsonObject report = entryObjects
            .First(entry => entry["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport")["resource"]!.AsObject();
        Assert.Contains("It may be appropriate", report["conclusion"]!.GetValue<string>());

        JsonObject organization = entryObjects
            .First(entry => entry["resource"]!["resourceType"]?.GetValue<string>() == "Organization")["resource"]!.AsObject();
        JsonObject address = organization["address"]!.AsArray()[0]!.AsObject();
        Assert.Equal("FL", address["state"]!.GetValue<string>());
        Assert.Equal("32608", address["postalCode"]!.GetValue<string>());

        JsonObject specimen = entryObjects
            .First(entry => entry["resource"]!["resourceType"]?.GetValue<string>() == "Specimen")["resource"]!.AsObject();
        Assert.Equal("X353256", specimen["identifier"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("femoral vein structure (body structure)", specimen["collection"]!["bodySite"]!["text"]!.GetValue<string>());
        Assert.Equal(20, specimen["collection"]!["quantity"]!["value"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ParsesDcrJackReacherFixtureWithSignature()
    {
        string? fixturePath = FindRepositoryFixture("DCR-JackReacher.xlsx");
        Assert.NotNull(fixturePath);

        await using FileStream stream = File.OpenRead(fixturePath!);
        IReadOnlyList<ImportRecord> records = await parser.ParseAsync(stream, "DCR-JackReacher.xlsx", CancellationToken.None);

        Assert.Single(records);
        ImportRecord record = records[0];
        Assert.Equal(ImportTemplateKind.Dcr, record.Kind);
        Assert.NotNull(record.SignatureData);
        Assert.NotEmpty(record.SignatureData);
        Assert.Equal("image/jpeg", record.SignatureContentType);
    }

    private static string? FindRepositoryFixture(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
}
