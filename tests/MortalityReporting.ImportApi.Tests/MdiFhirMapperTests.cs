using System.Text.Json;
using System.Text.Json.Nodes;
using GaTech.Chai.Vrdr;
using GaTech.Chai.Share;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Services;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

public sealed class MdiFhirMapperTests
{
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
    public void MapsToxicologyScalarDatesToDiagnosticReportAndSpecimen()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-scalar-dates";
        record.Fields["MDI Case Number"] = "ME-1";
        record.Fields["Decedent Name"] = "Jane Doe";
        // Set scalar date fields
        record.Fields["Date/Time of Specimen Collection"] = "2023-05-15T09:30:00";
        record.Fields["Date/Time of Receipt"] = "2023-05-15T10:00:00";
        record.Fields["Date/Time of Issuance"] = "2023-05-15T11:00:00";

        // Add one specimen row with minimal data (no Date/Time Collected)
        record.Tables["Specimens"] = [new Dictionary<string, string?> { ["Name"] = "Blood", ["Identifier"] = "S-1" }];
        record.Tables["Results"] = [new Dictionary<string, string?> { ["Analyte/Analysis"] = "Ethanol", ["Specimen"] = "Blood", ["Value"] = "54 mg/L" }];

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Find DiagnosticReport
        JsonObject? diagnosticReportEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport")?.AsObject();
        Assert.NotNull(diagnosticReportEntry);
        JsonObject diagnosticReport = diagnosticReportEntry!["resource"]!.AsObject();

        // Assert effective is from Date/Time of Receipt
        Assert.StartsWith("2023-05-15T10:00:00", diagnosticReport["effectiveDateTime"]?.GetValue<string>());

        // Assert issued is from Date/Time of Issuance
        Assert.StartsWith("2023-05-15T11:00:00", diagnosticReport["issued"]?.GetValue<string>());

        // Find Specimen
        JsonObject? specimenEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Specimen")?.AsObject();
        Assert.NotNull(specimenEntry);
        JsonObject specimen = specimenEntry!["resource"]!.AsObject();

        // Assert collection.collected is from scalar Date/Time of Specimen Collection
        Assert.NotNull(specimen["collection"]);
        Assert.StartsWith("2023-05-15T09:30:00", specimen["collection"]!["collectedDateTime"]?.GetValue<string>());
    }

    [Fact]
    public void MapsToxicologyResultsWithMultipleAnalytesAndMethods()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-multi-analytes";
        record.Fields["MDI Case Number"] = "ME-1";
        record.Fields["Decedent Name"] = "Jane Doe";
        // Set Date of Report Issuance
        record.Fields["Date/Time of Issuance"] = "2024-01-22";

        // Add one specimen row
        record.Tables["Specimens"] = [new Dictionary<string, string?> { ["Name"] = "Blood", ["Identifier"] = "S-1" }];

        // Add Results rows covering the specified analytes, methods, and values
        record.Tables["Results"] = [
            new Dictionary<string, string?> {
                ["Analyte/Analysis"] = "http://loinc.org^LP1^Fentanyl",
                ["Method"] = "http://snomed.info/sct^123^Mass spectrometry",
                ["Value"] = "23 ng/mL",
                ["Specimen"] = "Blood"
            },
            new Dictionary<string, string?> {
                ["Analyte/Analysis"] = "Ethanol",
                ["Method"] = "Screening",
                ["Value"] = "Detected",
                ["Specimen"] = "Blood"
            },
            new Dictionary<string, string?> {
                ["Analyte/Analysis"] = "not tested",
                ["Value"] = "",
                ["Specimen"] = "Blood"
            }
        ];

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Find all Observation resources
        JsonObject[] observationsArray = bundle["entry"]!.AsArray()
            .Where(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Observation")
            .Select(entry => entry!["resource"]!.AsObject())
            .ToArray();

        Assert.Equal(3, observationsArray.Length);

        // Find the Specimen resource
        JsonObject? specimenEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Specimen")?.AsObject();
        Assert.NotNull(specimenEntry);
        JsonObject specimen = specimenEntry!["resource"]!.AsObject();

        // Check first observation (Fentanyl)
        JsonObject fentanylObservation = observationsArray[0];
        Assert.Equal("http://loinc.org", fentanylObservation["code"]!["coding"]![0]!["system"]!.GetValue<string>());
        Assert.Equal("LP1", fentanylObservation["code"]!["coding"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("Fentanyl", fentanylObservation["code"]!["coding"]![0]!["display"]!.GetValue<string>());

        // Check first observation method (Mass spectrometry)
        Assert.NotNull(fentanylObservation["method"]);
        JsonObject methodCoding = (JsonObject)fentanylObservation["method"]!["coding"]![0]!;
        Assert.Equal("http://snomed.info/sct", methodCoding["system"]!.GetValue<string>());
        Assert.Equal("123", methodCoding["code"]!.GetValue<string>());
        Assert.Equal("Mass spectrometry", methodCoding["display"]!.GetValue<string>());

        // Check first observation valueString (Fentanyl)
        Assert.Equal("23 ng/mL", fentanylObservation["valueString"]!.GetValue<string>());
        Assert.Null(fentanylObservation["valueQuantity"]);

        // Check specimen reference points to mapped Specimen resource

        // Check specimen reference points to mapped Specimen resource
        Assert.Contains(specimen["id"]!.GetValue<string>(), fentanylObservation["specimen"]!["reference"]!.GetValue<string>());

        // Check second observation (Ethanol)
        JsonObject ethanolObservation = observationsArray[1];
        Assert.Equal("Ethanol", ethanolObservation["code"]!["text"]!.GetValue<string>());
        Assert.Contains("Detected", ethanolObservation["valueString"]!.GetValue<string>());

        // Check specimen reference
        Assert.Contains(specimen["id"]!.GetValue<string>(), ethanolObservation["specimen"]!["reference"]!.GetValue<string>());

        // Check third observation (not tested)
        JsonObject notTestedObservation = observationsArray[2];
        Assert.Equal("not tested", notTestedObservation["code"]!["text"]!.GetValue<string>());
        Assert.Equal("Not Applicable", notTestedObservation["valueString"]!.GetValue<string>());

        // Check specimen reference
        Assert.Contains(specimen["id"]!.GetValue<string>(), notTestedObservation["specimen"]!["reference"]!.GetValue<string>());

        // Assert all observations have effectiveDateTime beginning '2024-01-22'
        foreach (JsonObject observation in observationsArray)
        {
            Assert.StartsWith("2024-01-22", observation["effectiveDateTime"]?.GetValue<string>());
        }


    }

    [Fact]
    public void MapsMdiCaseNumberToDiagnosticReportExtension()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-mdi-case-number";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["MDI Case System"] = "urn:mdi:raven";
        record.Fields["MDI Case Number"] = "25-0001";

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Find DiagnosticReport
        JsonObject? diagnosticReportEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport")?.AsObject();
        Assert.NotNull(diagnosticReportEntry);
        JsonObject diagnosticReport = diagnosticReportEntry!["resource"]!.AsObject();

        // Find extension URL http://hl7.org/fhir/us/mdi/StructureDefinition/Extension-tracking-number
        // whose valueIdentifier.type.coding.code is mdi-case-number
        JsonArray? extensions = diagnosticReport["extension"]?.AsArray();
        Assert.NotNull(extensions);

        JsonObject? trackingNumberExtension = extensions!
            .FirstOrDefault(extension =>
                ((JsonObject)extension!)["url"]?.GetValue<string>() == "http://hl7.org/fhir/us/mdi/StructureDefinition/Extension-tracking-number")?.AsObject();

        Assert.NotNull(trackingNumberExtension);

        // Navigate to valueIdentifier.type.coding.code
        JsonObject valueIdentifier = ((JsonObject)trackingNumberExtension!["valueIdentifier"]!).AsObject();
        JsonObject identifierType = ((JsonObject)valueIdentifier!["type"]!).AsObject();
        JsonArray? codingArray = identifierType["coding"]?.AsArray();
        Assert.NotNull(codingArray);
        JsonObject coding = ((JsonObject)codingArray![0]!).AsObject();

        Assert.Equal("mdi-case-number", coding["code"]?.GetValue<string>());

        // Assert valueIdentifier.system is urn:mdi:raven
        Assert.Equal("urn:mdi:raven", valueIdentifier["system"]?.GetValue<string>());

        // Assert value is 25-0001
        Assert.Equal("25-0001", valueIdentifier["value"]?.GetValue<string>());

        // Assert coding.system/display match http://hl7.org/fhir/us/mdi/CodeSystem/cs-mdi-codes / MDI Case Number
        Assert.Equal("http://hl7.org/fhir/us/mdi/CodeSystem/cs-mdi-codes", coding["system"]?.GetValue<string>());
        Assert.Equal("MDI Case Number", coding["display"]?.GetValue<string>());
    }

    [Fact]
    public void MapsToxicologyResultsWithLessThanComparator()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-less-than";
        record.Fields["MDI Case Number"] = "ME-1";
        record.Fields["Decedent Name"] = "John Doe";
        record.Fields["Date/Time of Issuance"] = "2024-01-22";

        // Add one specimen row
        record.Tables["Specimens"] = [new Dictionary<string, string?> { ["Name"] = "Blood" }];

        // Add Results row with less than comparator
        record.Tables["Results"] = [
            new Dictionary<string, string?> {
                ["Analyte/Analysis"] = "http://loinc.org^LP1^Fentanyl",
                ["Method"] = "http://snomed.info/sct^123^Mass spectrometry",
                ["Value"] = "<2.5 ng/mL"
            }
        ];

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Find the Observation resource
        JsonObject? observationEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Observation")?.AsObject();
        Assert.NotNull(observationEntry);
        JsonObject observation = observationEntry!["resource"]!.AsObject();

        // Check valueString with less than comparator
        Assert.Equal("<2.5 ng/mL", observation["valueString"]!.GetValue<string>());
        Assert.Null(observation["valueQuantity"]);
    }

    [Fact]
    public void MapsToxicologyLabReportCodeWithFullCodingStructure()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-code-1";
        record.Fields["Decedent Name"] = "John Doe";
        record.Fields["Lab Report Code"] = "LOINC^LP29684-5^Toxicology report";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? diagnosticReportEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport")?.AsObject();
        Assert.NotNull(diagnosticReportEntry);
        JsonObject diagnosticReport = diagnosticReportEntry!["resource"]!.AsObject();
        Assert.NotNull(diagnosticReport["code"]!["coding"]?.AsArray()[0]);
        JsonObject coding = diagnosticReport["code"]!["coding"]!.AsArray()[0]!.AsObject();
        Assert.Equal(UriString.LOINC, coding["system"]?.GetValue<string>());
        Assert.Equal("LP29684-5", coding["code"]?.GetValue<string>());
        Assert.Equal("Toxicology report", coding["display"]?.GetValue<string>());
    }

    [Fact]
    public void MapsToxicologyLabReportCodeWithTextOnly()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-text-1";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["Lab Report Code"] = "Toxicology report";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? diagnosticReportEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport")?.AsObject();
        Assert.NotNull(diagnosticReportEntry);
        JsonObject diagnosticReport = diagnosticReportEntry!["resource"]!.AsObject();
        Assert.Equal("Toxicology report", diagnosticReport["code"]!["text"]?.GetValue<string>());
        Assert.Null(diagnosticReport["code"]!["coding"]);
    }

    [Fact]
    public void MapsPerformerAndLaboratoryToDiagnosticReport()
    {
        ImportRecord record = new(ImportTemplateKind.Toxicology);
        record.Fields["File Id"] = "tox-performer-lab-1";
        record.Fields["Decedent Name"] = "Jane Doe";
        record.Fields["Performer"] = "Ruben C. Sutton";
        record.Fields["Performer ID"] = "1234567890";
        record.Fields["Performer ID System"] = "NPI";
        record.Fields["Toxicology Lab Name"] = "State Forensic Laboratory";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? diagnosticReportEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "DiagnosticReport")?.AsObject();
        Assert.NotNull(diagnosticReportEntry);
        JsonObject diagnosticReport = diagnosticReportEntry!["resource"]!.AsObject();

        // Assert performer contains only a Practitioner reference (no Organization reference)
        JsonArray performerReferences = diagnosticReport["performer"]!.AsArray();
        Assert.Single(performerReferences);
        string performerReference = performerReferences[0]!["reference"]?.GetValue<string>()!;
        Assert.True(performerReference.StartsWith("Practitioner/"), $"Performer should reference a Practitioner, not an Organization: {performerReference}");

        // Assert resultsInterpreter contains an Organization reference
        JsonArray resultsInterpreterReferences = diagnosticReport["resultsInterpreter"]!.AsArray();
        Assert.Single(resultsInterpreterReferences);
        string resultsInterpreterReference = resultsInterpreterReferences[0]!["reference"]?.GetValue<string>()!;
        Assert.True(
            resultsInterpreterReference.StartsWith("Organization/", StringComparison.Ordinal) ||
            resultsInterpreterReference.StartsWith("urn:uuid:organization-", StringComparison.Ordinal),
            $"Results interpreter should reference an Organization: {resultsInterpreterReference}");

        // Find the Practitioner resource in the bundle and assert its identifier
        JsonObject? practitionerEntry = bundle["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Practitioner")?.AsObject();
        Assert.NotNull(practitionerEntry);
        JsonObject practitioner = practitionerEntry!["resource"]!.AsObject();
        JsonArray practitionerIdentifiers = practitioner["identifier"]!.AsArray();
        Assert.NotEmpty(practitionerIdentifiers);
        JsonObject practitionerIdentifier = practitionerIdentifiers[0]!.AsObject();
        Assert.Equal("1234567890", practitionerIdentifier["value"]?.GetValue<string>());
        Assert.Equal("http://hl7.org/fhir/sid/us-npi", practitionerIdentifier["system"]?.GetValue<string>());
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

        // Organization and Practitioner are now inside the nested document bundle, not in the outer bundle
        JsonObject? documentBundle = FindResourceInBundle(bundle, "Bundle");
        Assert.NotNull(documentBundle);
        JsonArray documentEntries = documentBundle!["entry"]!.AsArray();
        Assert.Contains(documentEntries, entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Organization");
        Assert.Contains(documentEntries, entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Practitioner" && entry!["resource"]!["id"]?.GetValue<string>()?.Contains("submitter") == true);
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
    public void MapsOnlyNewCremationFieldsToCremationClearanceInfo()
    {
        ImportRecord record = new(ImportTemplateKind.Dcr);
        record.Fields["BASEFHIRID"] = "dcr-only-cremation";
        record.Fields["Funeral Home: Name"] = "New Funeral Home";
        record.Fields["Funeral Home: Phone"] = "555-0100";
        record.Fields["Funeral Home: Fax"] = "555-0101";
        record.Fields["Funeral Home: City"] = "Springfield";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? documentBundle = FindResourceInBundle(bundle, "Bundle");
        Assert.NotNull(documentBundle);

        JsonObject composition = FindResourceInBundle(documentBundle!, "Composition")!;
        Assert.NotNull(composition);

        JsonArray? sections = composition["section"]?.AsArray();
        Assert.NotNull(sections);
        Assert.NotEmpty(sections);

        List<string> sectionCodes = sections!.Select(section =>
                section!["code"]?["coding"]?.AsArray()[0]?["code"]?.GetValue<string>())
            .Where(code => code is not null)
            .Cast<string>()
            .ToList();

        Assert.Contains("cremation-clearance-info", sectionCodes);
        Assert.DoesNotContain("DecedentDisposition", sectionCodes);

        JsonObject? cremationSection = sections!.FirstOrDefault(section => section!["code"]?["coding"]?.AsArray()[0]?["code"]?.GetValue<string>() == "cremation-clearance-info")?.AsObject();
        Assert.NotNull(cremationSection);
        Assert.NotNull(cremationSection!["entry"]);
        JsonArray? cremationEntries = cremationSection!["entry"]!.AsArray();
        Assert.NotEmpty(cremationEntries);

        JsonObject? organizationEntry = documentBundle!["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Organization")?.AsObject();
        Assert.NotNull(organizationEntry);
        JsonObject? organization = organizationEntry!["resource"]!.AsObject();
        Assert.Equal("New Funeral Home", organization!["name"]?.GetValue<string>());

        JsonArray? telecom = organization!["telecom"]?.AsArray();
        Assert.NotNull(telecom);

        JsonObject? phonePoint = telecom!.FirstOrDefault(point => point!["system"]?.GetValue<string>() == "phone")?.AsObject();
        Assert.NotNull(phonePoint);
        Assert.Equal("555-0100", phonePoint!["value"]?.GetValue<string>());
        // Assert phone use is work
        Assert.Equal("work", phonePoint!["use"]?.GetValue<string>());

        JsonObject? faxPoint = telecom!.FirstOrDefault(point => point!["system"]?.GetValue<string>() == "fax")?.AsObject();
        Assert.NotNull(faxPoint);
        Assert.Equal("555-0101", faxPoint!["value"]?.GetValue<string>());
        // Assert fax use is work
        Assert.Equal("work", faxPoint!["use"]?.GetValue<string>());

        // Assert Organization address use is work
        Assert.NotNull(organization!["address"]?.AsArray()[0]!["use"]);
        Assert.Equal("work", organization!["address"]!.AsArray()[0]!["use"]?.GetValue<string>());
    }

    [Fact]
    public void MapsLegacyFuneralHomeFieldsToDecedentDisposition()
    {
        ImportRecord record = new(ImportTemplateKind.Dcr);
        record.Fields["BASEFHIRID"] = "dcr-legacy-disposition";
        record.Fields["Funeral Home Name"] = "Legacy Funeral Home";
        record.Fields["Funeral Home Address: City"] = "Shelbyville";

        JsonObject bundle = new MdiFhirMapper().Map(record);
        JsonObject? documentBundle = FindResourceInBundle(bundle, "Bundle");
        Assert.NotNull(documentBundle);

        JsonObject composition = FindResourceInBundle(documentBundle!, "Composition")!;
        Assert.NotNull(composition);

        JsonArray? sections = composition["section"]?.AsArray();
        Assert.NotNull(sections);
        Assert.NotEmpty(sections);

        List<string> sectionCodes = sections!.Select(section =>
                section!["code"]?["coding"]?.AsArray()[0]?["code"]?.GetValue<string>())
            .Where(code => code is not null)
            .Cast<string>()
            .ToList();

        Assert.Contains("DecedentDisposition", sectionCodes);
        Assert.DoesNotContain("cremation-clearance-info", sectionCodes);

        JsonObject? dispositionSection = sections!.FirstOrDefault(section => section!["code"]?["coding"]?.AsArray()[0]?["code"]?.GetValue<string>() == "DecedentDisposition")?.AsObject();
        Assert.NotNull(dispositionSection);
        Assert.NotNull(dispositionSection!["entry"]);
        JsonArray? dispositionEntries = dispositionSection!["entry"]!.AsArray();
        Assert.NotEmpty(dispositionEntries);

        JsonObject? organizationEntry = documentBundle!["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Organization")?.AsObject();
        Assert.NotNull(organizationEntry);
        JsonObject? organization = organizationEntry!["resource"]!.AsObject();
        Assert.Equal("Legacy Funeral Home", organization!["name"]?.GetValue<string>());

        // Assert the Organization has the correct resourceType and meta.profile
        Assert.Equal("Organization", organization!["resourceType"]?.GetValue<string>());
        JsonArray? metaProfiles = organization!["meta"]!["profile"]?.AsArray();
        Assert.NotNull(metaProfiles);
        Assert.Contains(metaProfiles, profile => profile!.GetValue<string>().EndsWith("vrdr-funeral-home", StringComparison.Ordinal));

        // Assert Organization address use is work and telecom uses are work
        Assert.NotNull(organization!["address"]?.AsArray()[0]!["use"]);
        Assert.Equal("work", organization!["address"]!.AsArray()[0]!["use"]?.GetValue<string>());

        // Assert telecom uses are work if present
        JsonArray? telecom = organization!["telecom"]?.AsArray();
        if (telecom is not null)
        {
            foreach (JsonNode? point in telecom)
            {
                JsonObject telecomPoint = point!.AsObject();
                Assert.Equal("work", telecomPoint["use"]?.GetValue<string>());
            }
        }
    }

    [Fact]
    public void MapsDcrWithSignatureData()
    {
        ImportRecord record = new(ImportTemplateKind.Dcr);
        record.Fields["BASEFHIRID"] = "dcr-signature";
        record.Fields["Funeral Home Name"] = "Signature Funeral Home";
        record.SignatureData = [1, 2, 3];
        record.SignatureContentType = "image/jpeg";

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Find the nested document Bundle, not the outer message Bundle
        JsonObject? documentBundle = FindResourceInBundle(bundle, "Bundle");
        Assert.NotNull(documentBundle);

        // Check that the document bundle has a signature
        JsonObject signature = documentBundle!["signature"]!.AsObject();

        // Check that signature does not include contentType or sigFormat
        Assert.Null(signature["contentType"]);
        Assert.Null(signature["sigFormat"]);

        // Check that signature.who references an Organization
        string whoReference = signature["who"]!["reference"]!.GetValue<string>();
        Assert.StartsWith("Organization/", whoReference, StringComparison.Ordinal);

        // Check signature data is base64 encoded [1,2,3] = "AQID"
        Assert.Equal("AQID", signature["data"]?.GetValue<string>());

        // Check that signature.type coding is present
        JsonArray? signatureTypes = signature["type"]?.AsArray();
        Assert.NotNull(signatureTypes);
        Assert.NotEmpty(signatureTypes);
        JsonObject signatureType = signatureTypes![0]!.AsObject();
        Assert.Equal("1.2.840.10065.1.12.1.1", signatureType["code"]?.GetValue<string>());

        // Check that signature when is present
        Assert.NotNull(signature["when"]?.GetValue<string>());

        // Check that the outer bundle has no signature
        Assert.Null(bundle["signature"]);
    }

    [Fact]
    public void MapsDcrFuneralDirectorToPractitionerWithContactInfo()
    {
        ImportRecord record = new(ImportTemplateKind.Dcr);
        record.Fields["BASEFHIRID"] = "dcr-director";
        record.Fields["Funeral Home: Director Name"] = "Alex Director";
        record.Fields["Funeral Home: Name"] = "Director Funeral Home";
        record.Fields["Funeral Home: Street"] = "10 Main Street";
        record.Fields["Funeral Home: City"] = "Atlanta";
        record.Fields["Funeral Home: State, U.S. Territory or Canadian Province"] = "GA";
        record.Fields["Funeral Home: Postal Code"] = "30303";
        record.Fields["Funeral Home: Phone"] = "555-0100";
        record.Fields["Funeral Home: Fax"] = "555-0101";
        record.SignatureData = [1, 2, 3];
        record.SignatureContentType = "image/jpeg";

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Find the nested document Bundle and Composition
        JsonObject? documentBundle = FindResourceInBundle(bundle, "Bundle");
        Assert.NotNull(documentBundle);

        // Find the Composition in the document bundle
        JsonObject? composition = FindResourceInBundle(documentBundle!, "Composition");
        Assert.NotNull(composition);

        // Assert Composition.author references a Practitioner whose name contains Alex Director
        string? authorReference = composition!["author"]?[0]!["reference"]?.GetValue<string>();
        Assert.NotNull(authorReference);
        Assert.True(authorReference.StartsWith("Practitioner/"), $"Author should reference a Practitioner: {authorReference}");

        // Find the Practitioner resource in the document bundle
        JsonObject? practitionerEntry = documentBundle!["entry"]!.AsArray()
            .FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Practitioner" &&
                entry!["resource"]!["id"]?.GetValue<string>()?.Contains("funeral-home-director") == true)?.AsObject();
        Assert.NotNull(practitionerEntry);
        JsonObject practitioner = practitionerEntry!["resource"]!.AsObject();

        // Assert the Practitioner name contains "Alex Director" using serialized JSON assertions
        string practitionerJson = JsonSerializer.Serialize(practitioner);
        Assert.Contains("Director", practitionerJson); // Family name
        Assert.Contains("\"Alex\"", practitionerJson); // Given name

        // Assert that Practitioner has address and work phone/fax using serialized JSON assertions
        Assert.Contains("10 Main Street", practitionerJson);
        Assert.Contains("Atlanta", practitionerJson);
        Assert.Contains("GA", practitionerJson);
        Assert.Contains("30303", practitionerJson);
        Assert.Contains("555-0100", practitionerJson);
        Assert.Contains("555-0101", practitionerJson);

        // Assert nested Bundle.signature.who.reference equals the Composition.author reference
        JsonObject? signature = documentBundle!["signature"]?.AsObject();
        Assert.NotNull(signature);
        string? signatureWhoReference = signature!["who"]!["reference"]?.GetValue<string>();
        Assert.NotNull(signatureWhoReference);
        Assert.Equal(authorReference, signatureWhoReference);

        // Assert an Organization resource still exists using serialized JSON assertions
        string documentBundleJson = JsonSerializer.Serialize(documentBundle);
        Assert.Contains("Director Funeral Home", documentBundleJson);
        Assert.Contains("Organization", documentBundleJson);
    }

    [Fact]
    public void MapsDcrRecordWithNoBundleDuplication()
    {
        ImportRecord record = new(ImportTemplateKind.Dcr);
        record.Fields["BASEFHIRID"] = "dcr-no-duplication";
        record.Fields["FIRSTNAME"] = "Jane";
        record.Fields["LASTNAME"] = "Doe";
        record.Fields["GENDER"] = "Female";
        record.Fields["CAUSEA"] = "Fentanyl toxicity";
        record.Fields["CDEATHDATE"] = "01/08/2022";
        record.Fields["CERTIFIER_NAME"] = "Dr Smith";
        record.Fields["Message Reason"] = "DC_MED_DATA_Q_REQ";
        record.Fields["Submittor Name"] = "Alex Submitter";
        record.Fields["Submittor Email"] = "alex@example.org";
        record.Fields["Funeral Home Name"] = "Example Funeral Home";
        record.Fields["Funeral Home Address: City"] = "Atlanta";

        JsonObject bundle = new MdiFhirMapper().Map(record);

        // Assert the outer bundle is a message type
        Assert.Equal("message", bundle["type"]?.GetValue<string>());

        // Assert the outer message Bundle contains exactly two entries: MessageHeader and BundleDocumentMdiDcr
        JsonArray outerEntries = bundle["entry"]!.AsArray();
        Assert.Equal(2, outerEntries.Count);

        // Find the MessageHeader and Bundle entries
        JsonObject? messageHeaderEntry = outerEntries.FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "MessageHeader")?.AsObject();
        JsonObject? bundleEntry = outerEntries.FirstOrDefault(entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Bundle")?.AsObject();

        Assert.NotNull(messageHeaderEntry);
        Assert.NotNull(bundleEntry);

        // Assert no outer entry resource is Patient, Practitioner, Organization, Observation, Location, or Procedure
        List<string> forbiddenResourceTypes = new List<string> { "Patient", "Practitioner", "Organization", "Observation", "Location", "Procedure" };
        foreach (JsonNode? entry in outerEntries)
        {
            JsonObject resource = entry!["resource"]!.AsObject();
            string resourceType = resource["resourceType"]?.GetValue<string>()!;
            Assert.DoesNotContain(resourceType, forbiddenResourceTypes);
        }

        // Assert the nested document Bundle still contains the Composition and mapped Patient/Practitioner/resources
        JsonObject documentBundle = bundleEntry!["resource"]!.AsObject();
        JsonArray documentEntries = documentBundle["entry"]!.AsArray();

        // Should contain Composition
        Assert.Contains(documentEntries, entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Composition");

        // Should contain Patient
        Assert.Contains(documentEntries, entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Patient");

        // Should contain Practitioner (certifier)
        Assert.Contains(documentEntries, entry => entry!["resource"]!["resourceType"]?.GetValue<string>() == "Practitioner");

        // Assert the MessageHeader focus reference equals Bundle/{document.id}
        string documentId = documentBundle["id"]?.GetValue<string>()!;
        JsonObject messageHeader = messageHeaderEntry!["resource"]!.AsObject();
        string focusReference = messageHeader["focus"]![0]!["reference"]?.GetValue<string>()!;
        Assert.Equal($"Bundle/{documentId}", focusReference);
    }
    private static JsonObject? FindResourceInBundle(JsonObject bundle, string resourceType)
    {
        JsonArray? entries = bundle["entry"]?.AsArray();
        if (entries is null) return null;
        return entries
            .Select(entry => entry!["resource"]?.AsObject())
            .FirstOrDefault(resource => resource?["resourceType"]?.GetValue<string>() == resourceType);
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
