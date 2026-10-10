using System.Text.Json.Nodes;
using GaTech.Chai.Vrdr;
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
    public void MapsSocialSecurityNumberToPatientIdentifier()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-ssn";
        record.Fields["Decedent Name"] = "Han Solo";
        record.Fields["Decedent SSN"] = "123-45-6789";

        JsonObject patient = new MdiFhirMapper().Map(record)["entry"]!.AsArray()
            .First(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient")!["resource"]!.AsObject();
        Assert.Equal("http://hl7.org/fhir/sid/us-ssn", patient["identifier"]![0]! ["system"]!.GetValue<string>());
        Assert.Equal("123-45-6789", patient["identifier"]![0]! ["value"]!.GetValue<string>());
    }

    [Fact]
    public void AddsUnknownDataAbsentReasonWhenPatientHasNoIdentifier()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-no-id";
        record.Fields["Decedent Name"] = "Han Solo";

        JsonObject patient = new MdiFhirMapper().Map(record)["entry"]!.AsArray()
            .First(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient")!["resource"]!.AsObject();
        JsonObject extension = patient["identifier"]![0]!["extension"]![0]!.AsObject();
        Assert.Equal("http://hl7.org/fhir/StructureDefinition/data-absent-reason", extension["url"]!.GetValue<string>());
        Assert.Equal("unknown", extension["valueCode"]!.GetValue<string>());
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

    [Fact]
    public void MapsXlsxAddressLabelsAndAutopsyFields()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-address";
        record.Fields["Decedent Name"] = "Han Solo";
        record.Fields["Decedent Residence: Street"] = "5100 Peachtree Street";
        record.Fields["Location of Death: Street"] = "5100 Peachtree Street";
        record.Fields["Location of Death: City"] = "Atlanta";
        record.Fields["Location of Injury: Street"] = "3487 Lilac Lane";
        record.Fields["Autopsy Performed?"] = "Yes";
        record.Fields["Autopsy Results Available?"] = "Yes";
        record.Fields["Autopsy Performed Office Name"] = "HEAT Division";
        record.Fields["Autopsy Performed Location: City"] = "Atlanta";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Location" && entry!["resource"]!["id"]?.GetValue<string>()?.Contains("death") == true);
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Location" && entry!["resource"]!["id"]?.GetValue<string>()?.Contains("autopsy") == true);
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["meta"]!["profile"]!.AsArray().Any(profile => profile!.GetValue<string>().Contains("vrdr-autopsy-performed-indicator", StringComparison.Ordinal)));
        Assert.Contains(bundle["entry"]!.AsArray(), entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient" && entry!["resource"]!["address"] is not null);
    }

    [Fact]
    public void UsesAbsoluteResourceUrisForNonUuidResourceIds()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "MDI-DEV-GOLDENSET-HanSolo-MDIEDRS";
        record.Fields["Decedent Name"] = "Han Solo";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        string subjectReference = bundle["entry"]![0]! ["resource"]!["subject"]!["reference"]!.GetValue<string>();
        string fullUrl = bundle["entry"]![0]!["fullUrl"]!.GetValue<string>();
        Assert.StartsWith("Patient/", subjectReference, StringComparison.Ordinal);
        Assert.StartsWith("https://mdi.local/fhir/Composition/", fullUrl, StringComparison.Ordinal);
        foreach (string value in StringValues(bundle))
        {
            if (value.StartsWith("urn:uuid:", StringComparison.Ordinal))
                Assert.True(Guid.TryParse(value[9..], out _), $"Invalid UUID URI: {value}");
        }
    }

    [Fact]
    public void PregnancyStatusNotApplicableMapsToNACoding()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-pregnancy-na";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        record.Fields["PREGNANT"] = "Not Applicable";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? pregnancyObservation = FindPregnancyObservation(bundle);

        Assert.NotNull(pregnancyObservation);
        Assert.NotNull(pregnancyObservation!["valueCodeableConcept"]);
        Assert.IsType<JsonObject>(pregnancyObservation["valueCodeableConcept"]);
        JsonObject value = (JsonObject)pregnancyObservation["valueCodeableConcept"]!;
        Assert.NotNull(value["coding"]);
        JsonArray codingArray = (JsonArray)value["coding"]!;
        Assert.Single(codingArray);
        JsonObject coding = (JsonObject)codingArray[0]!;
        Assert.Equal(CodeSystemDeathPregnancyStatus.NA.Coding[0].System, coding["system"]!.GetValue<string>());
        Assert.Equal(CodeSystemDeathPregnancyStatus.NA.Coding[0].Code, coding["code"]!.GetValue<string>());
        Assert.Equal(CodeSystemDeathPregnancyStatus.NA.Coding[0].Display, coding["display"]!.GetValue<string>());
    }

    [Fact]
    public void PregnancyStatusPregnantAtTimeOfDeathMapsToVrdrCoding()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-pregnancy-pregnant";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        record.Fields["PREGNANT"] = "Pregnant at time of death";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? pregnancyObservation = FindPregnancyObservation(bundle);

        Assert.NotNull(pregnancyObservation);
        Assert.NotNull(pregnancyObservation["valueCodeableConcept"]);
        Assert.IsType<JsonObject>(pregnancyObservation["valueCodeableConcept"]);
        JsonObject value = (JsonObject)pregnancyObservation["valueCodeableConcept"]!;
        Assert.NotNull(value["coding"]);
        JsonArray codingArray = (JsonArray)value["coding"]!;
        Assert.Single(codingArray);
        JsonObject coding = (JsonObject)codingArray[0]!;
        Assert.Equal(CodeSystemDeathPregnancyStatus.PregnantAtTimeOfDeath.Coding[0].System, coding["system"]!.GetValue<string>());
        Assert.Equal(CodeSystemDeathPregnancyStatus.PregnantAtTimeOfDeath.Coding[0].Code, coding["code"]!.GetValue<string>());
        Assert.Equal(CodeSystemDeathPregnancyStatus.PregnantAtTimeOfDeath.Coding[0].Display, coding["display"]!.GetValue<string>());
    }

    [Fact]
    public void PregnancyStatusUnrecognizedValueRemainsTextOnly()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-pregnancy-unknown";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        record.Fields["PREGNANT"] = "Unknown pregnancy status";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? pregnancyObservation = FindPregnancyObservation(bundle);

        Assert.NotNull(pregnancyObservation);
        Assert.NotNull(pregnancyObservation["valueCodeableConcept"]);
        Assert.IsType<JsonObject>(pregnancyObservation["valueCodeableConcept"]);
        JsonObject value = (JsonObject)pregnancyObservation["valueCodeableConcept"]!;
        Assert.Null(value["coding"]);
        Assert.Equal("Unknown pregnancy status", value["text"]!.GetValue<string>());
    }

    [Fact]
    public void MissingPregnancyStatusOmitsPregnancyObservation()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-pregnancy-missing";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        // PREGNANT field is not set

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? pregnancyObservation = FindPregnancyObservation(bundle);

        Assert.Null(pregnancyObservation);
    }

    [Fact]
    public void CodedConceptsOmitEmptyText()
    {
        ImportRecord record = new();
        record.Fields["BASEFHIRID"] = "case-empty-concept-text";
        record.Fields["MRNNUMBER"] = "MRN-1";
        record.Fields["AUTOPSYPERFORMED"] = "Yes";
        record.Fields["AUTOPSYRESULTSAVAILABLE"] = "No";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject patient = FindResource(bundle, "Patient");
        JsonObject patientIdentifierType = patient["identifier"]![0]!["type"]!.AsObject();
        Assert.Null(patientIdentifierType["text"]);

        JsonObject autopsy = FindResource(bundle, "Observation", "-autopsy");
        Assert.Null(autopsy["code"]!["text"]);
        Assert.Null(autopsy["component"]![0]!["code"]!["text"]);
    }

    private static JsonObject? FindPregnancyObservation(JsonObject bundle)
    {
        JsonArray entries = bundle["entry"]!.AsArray();
        foreach (JsonNode? entry in entries)
        {
            JsonObject? resource = entry!["resource"]?.AsObject();
            if (resource != null && resource["resourceType"]?.GetValue<string>() == "Observation")
            {
                // Look for observations with IDs ending in "-pregnancy" (pattern: observation-{id}-pregnancy)
                string? resourceId = resource["id"]?.GetValue<string>();
                if (resourceId != null && resourceId.EndsWith("-pregnancy", StringComparison.OrdinalIgnoreCase))
                {
                    return resource;
                }
            }
        }
        return null;
    }

    private static JsonObject FindResource(JsonObject bundle, string resourceType, string idSuffix = "") =>
        bundle["entry"]!.AsArray()
            .Select(entry => entry!["resource"]!.AsObject())
            .First(resource => resource["resourceType"]?.GetValue<string>() == resourceType &&
                              resource["id"]?.GetValue<string>()?.EndsWith(idSuffix, StringComparison.OrdinalIgnoreCase) == true);

    private static IEnumerable<string> StringValues(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out string? scalar) && scalar is not null) yield return scalar;
        if (node is JsonObject obj)
            foreach (JsonNode? child in obj.Select(item => item.Value)) foreach (string itemText in StringValues(child)) yield return itemText;
        if (node is JsonArray array)
            foreach (JsonNode? child in array) foreach (string itemText in StringValues(child)) yield return itemText;
    }
}
