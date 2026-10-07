using System.Text.Json.Nodes;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Services;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

public sealed class MdiFhirMapperTests
{
    [Fact]
    public void MapsCoreMdiFieldsUsingMdiProfiles()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-1";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        record.Fields["GENDER"] = "Female";
        record.Fields["CAUSEA"] = "Fentanyl toxicity";
        record.Fields["DURATIONA"] = "minutes to hours";
        record.Fields["MANNER"] = "Accidental death";
        record.Fields["CDEATHDATE"] = "01/08/2022";
        record.Fields["CDEATHTIME"] = "3:30 PM";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Equal("Bundle", bundle["resourceType"]?.GetValue<string>());
        Assert.Contains(bundle["meta"]!["profile"]!.AsArray(), value => value!.GetValue<string>().EndsWith("Bundle-document-mdi-and-edrs", StringComparison.Ordinal));
        JsonArray entries = bundle["entry"]!.AsArray();
        Assert.Equal("Composition", entries[0]! ["resource"]!["resourceType"]!.GetValue<string>());
        Assert.Contains(entries, entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient");
        Assert.Contains(entries, entry => entry!["resource"]!["meta"]!["profile"]!.AsArray().Any(profile => profile!.GetValue<string>().EndsWith("Observation-mdi-cause-of-death-part1", StringComparison.Ordinal)));
    }

    [Fact]
    public void MapsToxicologyTablesToMdiMessageBundle()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-1";
        record.Fields["MDI Case Number"] = "ME-1";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["Toxicology Lab Name"] = "State Forensic Laboratory";
        record.Fields["Lab Address: City"] = "Atlanta";
        record.Tables["Specimens"] = [new Dictionary<string, string?> { ["Name"] = "Blood", ["Identifier"] = "S-1" }];
        record.Tables["Results"] = [new Dictionary<string, string?> { ["Analyte/Analysis"] = "Ethanol", ["Specimen"] = "Blood", ["Value"] = "54 mg/L" }];
        record.Tables["Notes"] = [new Dictionary<string, string?> { ["Note Content"] = "Interpretive note" }];

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Equal("message", bundle["type"]?.GetValue<string>());
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport");
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Observation");
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Organization");
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["conclusion"]?.GetValue<string>() == "Interpretive note");
    }

    [Fact]
    public void MapsMinimalRecordWithEmptyFields()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-1";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Equal("Bundle", bundle["resourceType"]?.GetValue<string>());
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient");
    }

    [Fact]
    public void MapsRecordWithMissingGenderAsUnknown()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-1";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        // Gender is missing

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Contains(bundle["entry"]!.AsArray(), entry =>
        {
            if (entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient")
            {
                var gender = entry!["resource"]!["gender"]?.GetValue<string>();
                return gender == "unknown";
            }
            return false;
        });
    }

    [Fact]
    public void MapsRecordWithNullValuesGracefully()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-1";
        record.Fields["FIRSTNAME"] = null;
        record.Fields["LASTNAME"] = null;
        record.Fields["GENDER"] = "Male";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Contains(bundle["entry"]!.AsArray(), entry =>
            entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient");
    }

    [Fact]
    public void MapsDcrRecordToDcrMessageProfile()
    {
        ImportRecord record = new(ImportTemplateKind.Dcr);
        record.Fields["BASEFHIRID"] = "dcr-1";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        record.Fields["CERTIFIER_NAME"] = "Dr Smith";
        record.Fields["Message Reason"] = "DC_MED_DATA_Q_REQ";
        record.Fields["Submittor Name"] = "Alex Submitter";
        record.Fields["Submittor Email"] = "alex@example.org";
        record.Fields["Funeral Home Name"] = "Example Funeral Home";
        record.Fields["Funeral Home Address: City"] = "Atlanta";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Equal("message", bundle["type"]?.GetValue<string>());
        Assert.Contains(bundle["meta"]!["profile"]!.AsArray(), value => value!.GetValue<string>().EndsWith("Bundle-message-death-certificate-review", StringComparison.Ordinal));
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "MessageHeader");
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Bundle");
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Organization");
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Practitioner" && entry!["resource"]!["id"]?.GetValue<string>()?.Contains("submitter") == true);
    }
}
