using System.Text;
using ClosedXML.Excel;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Parsing;
using Xunit;

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
}
