using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GaTech.Chai.Mdi;
using GaTech.Chai.Share;
using GaTech.Chai.UsCore;
using GaTech.Chai.Vrcl;
using GaTech.Chai.Vrdr;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using MortalityReporting.ImportApi.Configuration;
using MortalityReporting.ImportApi.Models;

namespace MortalityReporting.ImportApi.Services;

public sealed class MdiFhirMapper
{
    private static readonly object registryLock = new();
    private readonly string fhirFullUrlBase;

    public MdiFhirMapper(ImportApiOptions? options = null)
    {
        fhirFullUrlBase = (options?.FhirFullUrlBase ?? "https://mdi.local/fhir").TrimEnd('/');
    }

    public JsonObject Map(ImportRecord record)
    {
        lock (registryLock)
        {
            Record.GetResources().Clear();
            Bundle bundle = record.Kind switch
            {
                ImportTemplateKind.Toxicology => MapToxicology(record),
                ImportTemplateKind.Dcr => MapDcr(record),
                _ => MapMdi(record)
            };
            string json = new FhirJsonSerializer(new SerializerSettings { Pretty = false }).SerializeToString(bundle);
            JsonObject result = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("FHIR serializer returned invalid JSON.");
            if (record.Kind == ImportTemplateKind.Dcr) AddDcrSignature(result, record);
            RewriteResourceUris(result);
            return result;
        }
    }

    private void RewriteResourceUris(JsonObject root)
    {
        Dictionary<string, (string Relative, string Absolute)> replacements = [];
        CollectResourceUris(root, replacements);
        ReplaceResourceUris(root, replacements);
    }

    private void CollectResourceUris(JsonNode? node, Dictionary<string, (string Relative, string Absolute)> replacements)
    {
        if (node is JsonObject obj)
        {
            if (obj["resourceType"]?.GetValue<string>() is { } resourceType && obj["id"]?.GetValue<string>() is { } id)
                replacements[$"urn:uuid:{id}"] = ($"{resourceType}/{id}", $"{fhirFullUrlBase}/{resourceType}/{Uri.EscapeDataString(id)}");
            foreach (JsonNode? child in obj.Select(item => item.Value)) CollectResourceUris(child, replacements);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) CollectResourceUris(child, replacements);
        }
    }

    private static void ReplaceResourceUris(JsonNode? node, IReadOnlyDictionary<string, (string Relative, string Absolute)> replacements)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(item => item.Key).ToList())
            {
                JsonNode? child = obj[key];
                if (child is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null && replacements.TryGetValue(text, out (string Relative, string Absolute) replacement))
                    obj[key] = key == "fullUrl" ? replacement.Absolute : replacement.Relative;
                else ReplaceResourceUris(child, replacements);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) ReplaceResourceUris(child, replacements);
        }
    }

    private static Bundle MapMdi(ImportRecord record)
    {
        string caseKey = CaseKey(record, "case");
        string id = CaseToken(record, caseKey);
        Patient patient = CreatePatient(record, $"patient-{id}");
        Practitioner practitioner = CreatePractitioner(record, $"practitioner-{id}");
        List<Observation> causes = [];
        AddCause(record, causes, patient, practitioner, 1, "CAUSEA", "Cause of Death Part I Line a", "DURATIONA", "Cause of Death Part I Interval, Line a");
        AddCause(record, causes, patient, practitioner, 2, "CAUSEB", "Cause of Death Part I Line b", "DURATIONB", "Cause of Death Part I Interval, Line b");
        AddCause(record, causes, patient, practitioner, 3, "CAUSEC", "Cause of Death Part I Line c", "DURATIONC", "Cause of Death Part I Interval, Line c");
        AddCause(record, causes, patient, practitioner, 4, "CAUSED", "Cause of Death Part I Line d", "DURATIOND", "Cause of Death Part I Interval, Line d");

        Observation? cause2 = null;
        if (record.Get("OSCOND", "Cause of Death Part II") is { } cause2Text)
        {
            cause2 = VrdrCauseOfDeathPart2.Create(patient, practitioner);
            cause2.Id = $"observation-{id}-cause-part2";
            cause2.VrdrCauseOfDeathPart2().ValueText = cause2Text;
        }

        Observation? manner = CreateManner(record, patient, practitioner, id);
        Observation? injury = CreateInjury(record, patient, practitioner, id);
        Observation? deathDate = CreateDeathDate(record, patient, practitioner, id);
        Observation? age = CreateAge(record, patient, id);
        Observation? disposition = CreateDisposition(record, patient, id);
        Procedure? certification = CreateCertification(record, patient, practitioner, id);
        Observation? pregnancy = CreatePregnancy(record, patient, id);
        Observation? tobacco = CreateTobacco(record, patient, id);
        Location? deathLocation = CreateLocation(record, id, false);
        Location? injuryLocation = CreateLocation(record, id, true);
        Observation? autopsy = CreateAutopsy(record, patient, practitioner, id);
        Location? autopsyLocation = CreateAutopsyLocation(record, id);

        Composition composition = CompositionMdiAndEdrs.Create(
            new Identifier("urn:mdi:case", caseKey), CompositionStatus.Final, patient, practitioner,
            null, practitioner, null, Composition.CompositionAttestationMode.Official);
        composition.Id = $"composition-{id}";
        composition.DateElement = new FhirDateTime(DateOnly(record.Get("REPORTDATE")) ?? DateTime.UtcNow.ToString("yyyy-MM-dd"));
        composition.Title = "MDI to EDRS Composition";
        if (record.Get("MDICASEID", "Tracking Number: Mdi Case Number") is { } mdiCase)
            composition.CompositionMdiAndEdrs().MdiCaseNumber = ("urn:mdi:case", mdiCase);
        if (record.Get("EDRSCASEID", "Tracking Number: EDRS File Number") is { } edrsCase)
            composition.CompositionMdiAndEdrs().EdrsFileNumber = ("urn:mdi:edrs", edrsCase);

        List<Resource> circumstances = Resources(deathLocation, tobacco, pregnancy, injuryLocation, disposition);
        composition.CompositionMdiAndEdrs().Circumstances = SectionValue(circumstances);
        List<Resource> jurisdiction = Resources(deathDate, certification);
        composition.CompositionMdiAndEdrs().Jurisdiction = SectionValue(jurisdiction);
        composition.CompositionMdiAndEdrs().CauseManner =
            causes.Count > 0 || cause2 is not null || manner is not null || injury is not null
                ? (causes, cause2, manner, injury, null)
                : (null, null, null, null, ListEmptyReason.Unavailable);
        composition.CompositionMdiAndEdrs().AdditionalDemographics = new Narrative
        {
            Status = Narrative.NarrativeStatus.Additional,
            Div = "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Demographic information imported.</p></div>"
        };
        if (age is not null)
        {
            Record.GetResources()[age.AsReference().Reference] = age;
            Composition.SectionComponent section = composition.Section.First(item => item.Code?.Coding.FirstOrDefault()?.Code == "demographics");
            section.Entry.Add(age.AsReference());
        }
        composition.CompositionMdiAndEdrs().MedicalHistory =
            (null, new Narrative { Status = Narrative.NarrativeStatus.Generated, Div = "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Medical history is not available.</p></div>" }, ListEmptyReason.Unavailable);
        List<Resource> examAutopsy = Resources(autopsy, autopsyLocation);
        composition.CompositionMdiAndEdrs().ExamAutopsy = SectionValue(examAutopsy);

        Bundle bundle = BundleDocumentMdiAndEdrs.Create(new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{DeterministicUuid($"mdi|{id}")}"), composition);
        bundle.Id = $"bundle-mdi-{id}";
        bundle.TimestampElement = Instant.Now();
        return bundle;
    }

    private static Bundle MapToxicology(ImportRecord record)
    {
        string caseKey = CaseKey(record, "tox");
        string id = CaseToken(record, caseKey);
        Patient patient = CreatePatient(record, $"patient-{id}");
        Practitioner performer = VrdrCertifier.Create();
        performer.Id = $"practitioner-{id}";
        (List<string> performerGiven, string? performerFamily) = SplitName(record.Get("Performer") ?? "Unknown");
        performer.Name = [new HumanName { Family = performerFamily, GivenElement = performerGiven.Select(value => new FhirString(value)).ToList() }];
        Organization? laboratory = CreateLaboratory(record, $"organization-{id}");
        if (laboratory is not null) Record.GetResources()[laboratory.AsReference().Reference] = laboratory;

        DiagnosticReport report = DiagnosticReportToxicologyLabResultToMdi.Create(patient);
        report.Id = $"diagnostic-report-{id}";
        report.Status = DiagnosticReport.DiagnosticReportStatus.Final;

        if (record.Get("Performer ID") is { } performerId && !string.IsNullOrWhiteSpace(performerId))
        {
            string? performerSystem = IdentifierSystem(record.Get("Performer System", "Performer ID System"));
            performer.Identifier.Add(new Identifier
            {
                Use = Identifier.IdentifierUse.Usual,
                System = performerSystem,
                Value = performerId
            });
        }

        report.DiagnosticReportToxicologyLabResultToMdi().AddPerformer(performer);
        if (laboratory is not null)
        {
            string laboratoryReference = laboratory.AsReference().Reference;
            report.DiagnosticReportToxicologyLabResultToMdi().AddPerformer(laboratory);
            report.Performer.RemoveAll(reference => reference.Reference == laboratoryReference);
            report.ResultsInterpreter = [laboratory.AsReference()];
        }
        string caseSystem = record.Get("MDI Case System") ?? "urn:mdi:case";
        if (record.Get("Laboratory Case Number") is { } laboratoryCase)
            report.DiagnosticReportToxicologyLabResultToMdi().ToxLabCaseNumber = ("urn:mdi:tox-lab-case", laboratoryCase);
        SetMdiCaseNumber(report, caseSystem, record.Get("MDI Case Number") ?? id);
        if (DateTimeValueFromText(record.Get("Date/Time of Receipt")) is { } receiptDate)
            report.Effective = new FhirDateTime(receiptDate);
        if (InstantValueFromText(record.Get("Date/Time of Issuance")) is { } issuance)
            report.IssuedElement = issuance;
        else if (InstantValueFromText(record.Get("Date of Report Issuance")) is { } legacyIssuance)
            report.IssuedElement = legacyIssuance;
        string[] notes = (record.Tables.GetValueOrDefault("Notes") ?? [])
            .Select(row => row.GetValueOrDefault("Note Content"))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
        string? caseNotes = record.Get("ME/C Case Notes");
        if (!string.IsNullOrWhiteSpace(caseNotes)) notes = [.. notes, caseNotes];
        if (notes.Length > 0) report.Conclusion = string.Join(Environment.NewLine, notes);

        // Handle Lab Report Code mapping
        string? labReportCode = record.Get("Lab Report Code");
        if (!string.IsNullOrWhiteSpace(labReportCode))
        {
            string trimmedCode = labReportCode.Trim();
            if (trimmedCode.Contains('^'))
            {
                string[] segments = trimmedCode.Split('^');
                report.Code = new CodeableConcept
                {
                    Coding = new List<Coding>
                    {
                        new Coding
                        {
                            System = FhirCodingSystemUri(segments[0]),
                            Code = segments[1].Trim(),
                            Display = segments.Length > 2 ? string.Join("^", segments.Skip(2).Select(s => s.Trim())) : null
                        }
                    }
                };
            }
            else
            {
                report.Code = new CodeableConcept { Text = trimmedCode };
            }
        }

        Dictionary<string, Specimen> specimens = new(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, string?>> specimenRows = record.Tables.GetValueOrDefault("Specimens") ?? [];
        for (int rowIndex = 0; rowIndex < specimenRows.Count; rowIndex++)
        {
            Dictionary<string, string?> row = specimenRows[rowIndex];
            string name = row.GetValueOrDefault("Name") ?? "Specimen";
            Specimen specimen = SpecimenToxicologyLab.Create(name, patient);
            specimen.Id = $"specimen-{id}-{StableToken($"specimen|{rowIndex}|{CanonicalRow(row)}", 12)}";
            if (row.GetValueOrDefault("Identifier") is { } specimenIdentifier)
                specimen.Identifier.Add(new Identifier("urn:mdi:specimen", specimenIdentifier.Trim()));
            Specimen.CollectionComponent collection = specimen.Collection ??= new Specimen.CollectionComponent();
            if (row.GetValueOrDefault("Body Site") is { } bodySite)
                collection.BodySite = new CodeableConcept { Text = bodySite };
            if (ParseQuantity(row.GetValueOrDefault("Amount")) is { } amount)
                collection.Quantity = amount;
            string? collected = row.GetValueOrDefault("Date/Time Collected") ?? record.Get("Date/Time of Specimen Collection");
            if (DateTimeValueFromText(collected) is { } collectedDateTime)
                collection.Collected = new FhirDateTime(collectedDateTime);
            string? received = row.GetValueOrDefault("Date/Time of Receipt") ?? record.Get("Date/Time of Receipt");
            if (DateTimeValueFromText(received) is { } receivedDateTime)
                specimen.ReceivedTimeElement = new FhirDateTime(receivedDateTime);
            if (row.GetValueOrDefault("Condition") is { } condition)
                specimen.Condition.Add(new CodeableConcept { Text = condition });
            if (row.GetValueOrDefault("Container") is { } container)
                specimen.Container.Add(new Specimen.ContainerComponent { Description = container });
            if (row.GetValueOrDefault("Notes") is { } specimenNotes)
                specimen.Note.Add(new Annotation { Text = specimenNotes });
            report.DiagnosticReportToxicologyLabResultToMdi().AddSpecimen(specimen);
            specimens[name.Trim()] = specimen;
        }
        List<Dictionary<string, string?>> resultRows = record.Tables.GetValueOrDefault("Results") ?? [];
        for (int rowIndex = 0; rowIndex < resultRows.Count; rowIndex++)
        {
            Dictionary<string, string?> row = resultRows[rowIndex];
            string? analyteValue = row.GetValueOrDefault("Analyte/Analysis");
            bool hasAnalyte = !string.IsNullOrWhiteSpace(analyteValue);
            string analyte = hasAnalyte ? analyteValue!.Trim() : "Toxicology result";
            Observation result = ObservationToxicologyLabResult.Create(ObservationStatus.Final, analyte, patient);
            result.Id = $"observation-{id}-{StableToken($"result|{rowIndex}|{CanonicalRow(row)}", 12)}";
            if (DateTimeValueFromText(record.Get("Date of Report Issuance", "Date/Time of Issuance")) is { } resultDate)
                result.Effective = new FhirDateTime(resultDate);
            result.Code = CodeableConceptFromValue(analyte);
            string? resultValue = row.GetValueOrDefault("Value")?.Trim();
            if (string.IsNullOrWhiteSpace(resultValue) && hasAnalyte)
                result.ObservationToxicologyLabResult().ValueText = "Not Applicable";
            else
                result.ObservationToxicologyLabResult().ValueText = resultValue ?? "";
            if (row.GetValueOrDefault("Specimen") is { } specimenName && specimens.TryGetValue(specimenName.Trim(), out Specimen? specimen))
                result.ObservationToxicologyLabResult().Specimen = specimen;
            if (row.GetValueOrDefault("Method") is { } method && !string.IsNullOrWhiteSpace(method))
                result.Method = CodeableConceptFromValue(method);
            if (row.GetValueOrDefault("Description") is { } description)
                result.Note.Add(new Annotation { Text = description });
            Quantity? rangeLow = ParseQuantity(row.GetValueOrDefault("Range Low"));
            Quantity? rangeHigh = ParseQuantity(row.GetValueOrDefault("Range High"));
            if (rangeLow is not null || rangeHigh is not null)
                result.ReferenceRange.Add(new Observation.ReferenceRangeComponent { Low = rangeLow, High = rangeHigh });
            report.DiagnosticReportToxicologyLabResultToMdi().AddResult(result);
        }

        MessageHeader header = MessageHeaderToxicologyToMdi.Create("urn:mdi:import", report);
        header.Id = $"message-header-{id}";
        Bundle bundle = BundleMessageToxicologyToMdi.Create(new Identifier(caseSystem, caseKey), header);
        bundle.Id = $"bundle-tox-{id}";
        bundle.TimestampElement = Instant.Now();
        return bundle;
    }

    private static Bundle MapDcr(ImportRecord record)
    {
        string caseKey = CaseKey(record, "dcr");
        string id = CaseToken(record, caseKey);
        Patient patient = CreatePatient(record, $"patient-{id}");
        Practitioner practitioner = CreatePractitioner(record, $"practitioner-{id}");
        Practitioner? submitter = CreateSubmitter(record, $"practitioner-{id}-submitter");
        Organization? funeralHome = CreateFuneralHome(record, $"organization-{id}-funeral-home");
        List<Observation> causes = [];
        AddCause(record, causes, patient, practitioner, 1, "CAUSEA", "Cause of Death Part I Line a", "DURATIONA", "Cause of Death Part I Interval, Line a");
        AddCause(record, causes, patient, practitioner, 2, "CAUSEB", "Cause of Death Part I Line b", "DURATIONB", "Cause of Death Part I Interval, Line b");
        AddCause(record, causes, patient, practitioner, 3, "CAUSEC", "Cause of Death Part I Line c", "DURATIONC", "Cause of Death Part I Interval, Line c");
        AddCause(record, causes, patient, practitioner, 4, "CAUSED", "Cause of Death Part I Line d", "DURATIOND", "Cause of Death Part I Interval, Line d");
        Observation? deathDate = CreateDeathDate(record, patient, practitioner, id);
        Observation? manner = CreateManner(record, patient, practitioner, id);
        Observation? injury = CreateInjury(record, patient, practitioner, id);
        Observation? disposition = CreateDisposition(record, patient, id);
        Procedure? certification = CreateCertification(record, patient, practitioner, id);
        Location? deathLocation = CreateLocation(record, id, false);
        Location? injuryLocation = CreateLocation(record, id, true);
        Observation? autopsy = CreateAutopsy(record, patient, practitioner, id);
        Location? autopsyLocation = CreateAutopsyLocation(record, id);

        Composition composition = CompositionMdiDcr.Create(
            new Identifier("urn:mdi:case", caseKey), CompositionStatus.Preliminary, patient, submitter ?? practitioner);
        composition.Id = $"composition-{id}";
        if (record.Get("MDICASEID", "Tracking Number: Mdi Case Number") is { } mdiCase)
            composition.CompositionMdiDcr().MdiCaseNumber = ("urn:mdi:case", mdiCase);
        if (record.Get("EDRSCASEID", "Tracking Number: EDRS File Number") is { } edrsCase)
            composition.CompositionMdiDcr().EdrsFileNumber = ("urn:mdi:edrs", edrsCase);

        var dcrComposition = composition.CompositionMdiDcr();
        List<Resource> demographicResources = HasImportedDemographicData(record) ? Resources(patient) : [];
        if (demographicResources.Count > 0) dcrComposition.DecedentDemographics = (demographicResources, null);
        List<Resource?> investigationResources = [deathDate, manner, injury, deathLocation, injuryLocation, autopsy, autopsyLocation];
        investigationResources.AddRange(causes);
        List<Resource> deathInvestigationResources = Resources(investigationResources.ToArray());
        if (deathInvestigationResources.Count > 0) dcrComposition.DeathInvestigation = (deathInvestigationResources, null);
        List<Resource> deathCertificationResources = [];
        if (HasImportedCertifierData(record)) deathCertificationResources.Add(practitioner);
        if (submitter is not null) deathCertificationResources.Add(submitter);
        if (certification is not null) deathCertificationResources.Add(certification);
        if (deathCertificationResources.Count > 0) dcrComposition.DeathCertification = (deathCertificationResources, null);
        bool hasLegacyFuneralHomeData = HasLegacyFuneralHomeData(record);
        bool hasNewCremationData = HasNewCremationData(record);
        List<Resource> dispositionResources = Resources(disposition, hasLegacyFuneralHomeData ? funeralHome : null);
        if (dispositionResources.Count > 0) dcrComposition.DecedentDisposition = (dispositionResources, null);
        List<Resource> cremationResources = Resources(hasNewCremationData ? funeralHome : null);
        if (cremationResources.Count > 0) dcrComposition.CremationClearanceInfo = (cremationResources, null);
        RemoveDcrSectionIfEmpty(composition, "DecedentDemographics", demographicResources.Count == 0);
        RemoveDcrSectionIfEmpty(composition, "DeathInvestigation", deathInvestigationResources.Count == 0);
        RemoveDcrSectionIfEmpty(composition, "DeathCertification", deathCertificationResources.Count == 0);
        RemoveDcrSectionIfEmpty(composition, "DecedentDisposition", dispositionResources.Count == 0);
        RemoveDcrSectionIfEmpty(composition, "death-certificate-data-review", true);
        RemoveDcrSectionIfEmpty(composition, "cremation-clearance-info", cremationResources.Count == 0);

        Bundle document = BundleDocumentMdiDcr.Create(new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{DeterministicUuid($"dcr-document|{id}")}"), composition);
        document.Id = $"bundle-document-dcr-{id}";
        List<Resource?> dcrResources = [patient, practitioner, submitter, funeralHome, deathDate, manner, injury, disposition, certification, deathLocation, injuryLocation];
        dcrResources.AddRange(causes);
        AppendResources(document, dcrResources);
        CodeableConcept reason = DcrReason(record.Get("Message Reason"));
        MessageHeader header = MessageHeaderDeathCertificateReview.Create("urn:mdi:import", reason, document);
        header.Id = $"message-header-{id}";
        Bundle message = BundleMessageDeathCertificateReview.Create(header);
        message.Id = $"bundle-dcr-{id}";
        message.TimestampElement = Instant.Now();
        return message;
    }

    private static void AddDcrSignature(JsonObject message, ImportRecord record)
    {
        if (record.SignatureData is not { Length: > 0 }) return;
        JsonObject? document = message["entry"]?.AsArray()
            .Select(entry => entry?["resource"]?.AsObject())
            .FirstOrDefault(resource => resource?["resourceType"]?.GetValue<string>() == "Bundle" &&
                resource["meta"]?["profile"]?.AsArray().Any(profile => profile?.GetValue<string>()?.EndsWith("Bundle-document-mdi-dcr", StringComparison.Ordinal) == true) == true);
        JsonObject? organization = document?["entry"]?.AsArray()
            .Select(entry => entry?["resource"]?.AsObject())
            .FirstOrDefault(resource => resource?["resourceType"]?.GetValue<string>() == "Organization");
        string? organizationId = organization?["id"]?.GetValue<string>();
        if (document is null || string.IsNullOrWhiteSpace(organizationId)) return;
        document["signature"] = new JsonObject
        {
            ["type"] = new JsonArray(new JsonObject
            {
                ["system"] = "urn:iso-astm:E1762-95:2013",
                ["code"] = "1.2.840.10065.1.12.1.1",
                ["display"] = "Author's Signature"
            }),
            ["when"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["who"] = new JsonObject { ["reference"] = $"Organization/{organizationId}" },
            ["data"] = Convert.ToBase64String(record.SignatureData)
        };
    }

    private static bool HasImportedDemographicData(ImportRecord record) =>
        HasAnyValue(record,
            "FIRSTNAME", "MIDNAME", "LASTNAME", "First Name", "Middle Name", "Last Name", "Decedent Name",
            "GENDER", "Decedent SexAtDeath", "Decedent Sex", "BIRTHDATE", "Decedent DOB", "Decedent SSN", "SSN", "SSNNUMBER",
            "MRNNUMBER", "POSSIBLEID", "LANGUAGE", "MARITAL", "Decedent Marital status", "RACE", "Decedent Race", "ETHNICITY", "Decedent Ethnicity") ||
        BuildAddress(record, "RES") is not null;

    private static bool HasImportedCertifierData(ImportRecord record) =>
        HasAnyValue(record,
            "MENAME", "Medical Examiner Name", "CERTIFIER_NAME", "Certifier Name", "CERTIFIER_TYPE", "Certifier Type",
            "MEPHONE", "Medical Examiner Phone Number", "Certifier ID", "CERTIFIER_IDENTIFIER", "Certifier ID System", "CERTIFIER_IDENTIFIER_SYSTEM") ||
        BuildAddress(record, "ME") is not null;

    private static bool HasLegacyFuneralHomeData(ImportRecord record) =>
        HasExactValue(record,
            "FUNERAL_HOME_NAME", "Funeral Home Name", "FUNERAL_HOME_PHONE", "Funeral Home Phone Number",
            "FUNERAL_HOME_FAX", "Funeral Home Fax Number", "FUNERAL_HOME_STREET", "FUNERAL_HOME_CITY",
            "FUNERAL_HOME_COUNTY", "FUNERAL_HOME_STATE", "FUNERAL_HOME_ZIP", "Funeral Home Address: Street",
            "Funeral Home Address: City", "Funeral Home Address: County", "Funeral Home Address: State, U.S. Territory or Canadian Province",
            "Funeral Home Address: Postal Code", "Funeral Home Address: Country");

    private static bool HasNewCremationData(ImportRecord record) =>
        HasExactValue(record,
            "Funeral Home: Name", "Funeral Home: Phone", "Funeral Home: Fax", "Funeral Home: Street",
            "Funeral Home: City", "Funeral Home: County", "Funeral Home: State, U.S. Territory or Canadian Province",
            "Funeral Home: Postal Code");

    private static bool HasAnyValue(ImportRecord record, params string[] names) => names.Any(name => record.Get(name) is not null);

    private static bool HasExactValue(ImportRecord record, params string[] names) => names.Any(name =>
        record.Fields.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value));

    private static void RemoveDcrSectionIfEmpty(Composition composition, string code, bool remove)
    {
        if (!remove) return;
        string normalizedCode = ImportRecord.Normalize(code);
        composition.Section.RemoveAll(section => section.Code?.Coding.Any(coding => ImportRecord.Normalize(coding.Code ?? "") == normalizedCode) == true);
    }

    private static Patient CreatePatient(ImportRecord record, string id)
    {
        Patient patient = PatientVr.Create();
        patient.Id = id;
        (List<string> given, string? family) = Name(record);
        if (given.Count > 0 || family is not null)
            patient.Name = [new HumanName { Family = family, GivenElement = given.Select(value => new FhirString(value)).ToList() }];
        patient.Gender = record.Get("GENDER", "Decedent SexAtDeath", "Decedent Sex")?.ToLowerInvariant() switch
        {
            "male" => AdministrativeGender.Male,
            "female" => AdministrativeGender.Female,
            "other" => AdministrativeGender.Other,
            _ => AdministrativeGender.Unknown
        };
        if (DateOnly(record.Get("BIRTHDATE", "Decedent DOB")) is { } birthDate) patient.BirthDate = birthDate;
        string? ssn = record.Get("Decedent SSN", "SSN", "SSNNUMBER");
        if (ssn is not null)
        {
            patient.Identifier.Add(new Identifier
            {
                Use = Identifier.IdentifierUse.Usual,
                Type = CodedConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "SB", "Social Beneficiary Identifier"),
                System = "http://hl7.org/fhir/sid/us-ssn",
                Value = ssn
            });
        }
        if (record.Get("MRNNUMBER") is { } mrn)
            patient.Identifier.Add(new Identifier("urn:mdi:mrn", mrn) { Use = Identifier.IdentifierUse.Usual, Type = CodedConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MR", "Medical record number") });
        if (record.Get("POSSIBLEID", "Possible ID") is { } possibleId)
            patient.Identifier.Add(new Identifier("urn:mdi:possible-id", possibleId) { Use = Identifier.IdentifierUse.Usual, Type = CodedConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "PI", "Patient internal identifier") });
        if (record.Get("Decedent ID") is { } decedentId)
        {
            patient.Identifier.Add(new Identifier
            {
                Use = Identifier.IdentifierUse.Usual,
                System = IdentifierSystem(record.Get("Decedent ID System")),
                Value = decedentId
            });
        }
        if (patient.Identifier.Count == 0)
        {
            Identifier absentIdentifier = new();
            absentIdentifier.Extension.AddOrUpdateExtension("http://hl7.org/fhir/StructureDefinition/data-absent-reason", new Code("unknown"));
            patient.Identifier.Add(absentIdentifier);
        }
        if (BuildAddress(record, "RES") is { } address) patient.Address = [address];
        if (record.Get("LANGUAGE") is { } language) patient.Communication = [new Patient.CommunicationComponent { Language = new CodeableConcept { Text = NullIfWhiteSpace(language) } }];
        if (record.Get("MARITAL", "Decedent Marital status") is { } marital) patient.MaritalStatus = new CodeableConcept { Text = NullIfWhiteSpace(marital) };
        AddRaceAndEthnicity(patient, record);
        return patient;
    }

    private static Practitioner CreatePractitioner(ImportRecord record, string id)
    {
        Practitioner practitioner = VrdrCertifier.Create();
        practitioner.Id = id;
        string name = record.Get("MENAME", "Medical Examiner Name", "CERTIFIER_NAME", "Certifier Name") ?? "Unknown";
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        practitioner.Name = [new HumanName { Family = parts.LastOrDefault() ?? "Unknown", GivenElement = parts.Take(Math.Max(0, parts.Length - 1)).Select(value => new FhirString(value)).ToList() }];
        if (record.Get("MEPHONE", "Medical Examiner Phone Number") is { } phone)
            practitioner.Telecom = [new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Work, phone)];
        if (BuildAddress(record, "ME") is { } address) { address.Use = Address.AddressUse.Work; practitioner.Address = [address]; }
        if (record.Get("Certifier ID", "CERTIFIER_IDENTIFIER") is { } identifierValue)
        {
            string? identifierSystem = IdentifierSystem(record.Get("Certifier ID System", "CERTIFIER_IDENTIFIER_SYSTEM"));
            practitioner.Identifier.Add(new Identifier
            {
                Use = Identifier.IdentifierUse.Usual,
                System = identifierSystem,
                Value = identifierValue
            });
        }
        return practitioner;
    }

    private static string? IdentifierSystem(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : ImportRecord.Normalize(value) switch
        {
            "npi" => "http://hl7.org/fhir/sid/us-npi",
            "ss" or "ssn" => "http://hl7.org/fhir/sid/us-ssn",
            _ => value.Trim()
        };

    private static Practitioner? CreateSubmitter(ImportRecord record, string id)
    {
        string? name = record.Get("SUBMITTOR_NAME", "Submittor Name");
        string? phone = record.Get("SUBMITTOR_PHONE", "Submittor Phone Number");
        string? email = record.Get("SUBMITTOR_EMAIL", "Submittor Email");
        string? fax = record.Get("SUBMITTOR_FAX", "Submittor Fax Number");
        if (name is null && phone is null && email is null && fax is null) return null;
        Practitioner practitioner = PractitionerVr.Create();
        practitioner.Id = id;
        string[] parts = (name ?? "Unknown").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        practitioner.Name = [new HumanName { Family = parts.LastOrDefault() ?? "Unknown", GivenElement = parts.Take(Math.Max(0, parts.Length - 1)).Select(value => new FhirString(value)).ToList() }];
        practitioner.Telecom = [];
        if (phone is not null) practitioner.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Work, phone));
        if (email is not null) practitioner.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Email, ContactPoint.ContactPointUse.Work, email));
        if (fax is not null) practitioner.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Fax, ContactPoint.ContactPointUse.Work, fax));
        return practitioner;
    }

    private static Organization? CreateFuneralHome(ImportRecord record, string id)
    {
        // Support new labels with precedence over legacy aliases
        string? name = GetExactValue(record, "Funeral Home: Name", "FUNERAL_HOME_NAME", "Funeral Home Name");
        // Handle address with new label support
        Address? address = BuildAddress(record, "Funeral Home Address", Address.AddressUse.Work);
        string? phone = GetExactValue(record, "Funeral Home: Phone", "FUNERAL_HOME_PHONE", "Funeral Home Phone Number");
        string? fax = GetExactValue(record, "Funeral Home: Fax", "FUNERAL_HOME_FAX", "Funeral Home Fax Number");
        if (name is null && address is null && phone is null && fax is null) return null;
        Organization organization = GaTech.Chai.Vrdr.VrdrFuneralHome.Create(name ?? "Funeral home");
        organization.Id = id;
        if (address is not null) organization.Address = [address];
        organization.Telecom = [];
        if (phone is not null) organization.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Work, phone));
        if (fax is not null) organization.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Fax, ContactPoint.ContactPointUse.Work, fax));
        return organization;
    }

    private static Organization? CreateLaboratory(ImportRecord record, string id)
    {
        string? name = record.Get("Toxicology Lab Name");
        Address? address = BuildAddress(record, "Lab Address", Address.AddressUse.Work);
        if (name is null && address is null) return null;
        Organization organization = UsCoreOrganization.Create(name ?? "Toxicology laboratory");
        organization.Id = id;
        if (address is not null) organization.Address = [address];
        return organization;
    }

    private static void AddRaceAndEthnicity(Patient patient, ImportRecord record)
    {
        if (record.Get("RACE", "Decedent Race") is { } race)
        {
            string code = race.ToLowerInvariant() switch
            {
                "american indian or alaska native" => "1002-5", "asian" => "2028-9", "black or african american" => "2054-5",
                "native hawaiian or other pacific islander" => "2076-8", "white" => "2106-3", _ => "UNK"
            };
            patient.UsCorePatient().Race.Category = UsCorePatientRace.RaceCoding.Encode(code, race);
            patient.UsCorePatient().Race.RaceText = race;
        }
        if (record.Get("ETHNICITY", "Decedent Ethnicity") is { } ethnicity)
        {
            string code = ethnicity.ToLowerInvariant() switch { "hispanic or latino" => "2135-2", "not hispanic or latino" => "2186-5", _ => "UNK" };
            patient.UsCorePatient().Ethnicity.Category = UsCorePatientEthnicity.EthnicityCoding.Encode(code, ethnicity);
            patient.UsCorePatient().Ethnicity.EthnicityText = ethnicity;
        }
    }

    private static void AddCause(ImportRecord record, List<Observation> causes, Patient patient, Practitioner practitioner, int line, string causeKey, string causeLabel, string durationKey, string durationLabel)
    {
        string? cause = record.Get(causeKey, causeLabel);
        if (string.IsNullOrWhiteSpace(cause)) return;
        string interval = record.Get(durationKey, durationLabel) ?? "unknown";
        Observation observation = ObservationMdiCauseOfDeathPart1.Create(patient, practitioner, cause, line, interval);
        observation.Id = $"observation-{StableToken(patient.Id ?? "case", 24)}-cause-part1-{line}";
        observation.Status = ObservationStatus.Final;
        causes.Add(observation);
    }

    private static Observation? CreateManner(ImportRecord record, Patient patient, Practitioner practitioner, string id)
    {
        string? value = record.Get("MANNER", "Manner of Death");
        if (value is null) return null;
        CodeableConcept concept = value.ToLowerInvariant() switch
        {
            "natural death" => MdiVsMannerOfDeath.NaturalDeath, "accidental death" => MdiVsMannerOfDeath.AccidentalDeath,
            "suicide" => MdiVsMannerOfDeath.Suicide, "homicide" => MdiVsMannerOfDeath.Homicide,
            "patient awaiting investigation" => MdiVsMannerOfDeath.PatientAwaitingInvestigation, _ => MdiVsMannerOfDeath.DeathmannerUndetermined
        };
        Observation observation = VrdrMannerOfDeath.Create(patient, practitioner);
        observation.Id = $"observation-{id}-manner"; observation.Value = concept; return observation;
    }

    private static Observation? CreateDeathDate(ImportRecord record, Patient patient, Practitioner practitioner, string id)
    {
        string? death = DateTimeValue(record.Get("CDEATHDATE", "Decedent Date of death"), record.Get("CDEATHTIME", "Decedent Time of death"));
        if (death is null) return null;
        Observation observation = VrdrDeathDate.Create(patient);
        observation.Id = $"observation-{id}-death-date"; observation.Status = ObservationStatus.Final;
        observation.Effective = new FhirDateTime(death); observation.Value = new FhirDateTime(DateOnly(death));
        observation.Performer = [practitioner.AsReference()];
        if (DateTimeValue(record.Get("PRNDATE", "Date pronounced dead"), record.Get("PRNTIME", "Time pronounced dead")) is { } pronounced)
            observation.VrdrDeathDate().DateTimePronouncedDead = new FhirDateTime(pronounced);
        if (record.Get("DEATHLOCATIONTYPE", "Place of death") is { } place) observation.VrdrDeathDate().PlaceOfDeath = PlaceOfDeath(place);
        return observation;
    }

    private static Observation? CreateInjury(ImportRecord record, Patient patient, Practitioner practitioner, string id)
    {
        string? value = record.Get("EVENTPLACE", "How Injury Occurred", "INJURYLOCATIONTYPE");
        if (value is null) return null;
        Observation observation = VrdrInjuryIncident.Create(patient);
        observation.Id = $"observation-{id}-injury"; observation.Status = ObservationStatus.Final;
        observation.VrdrInjuryIncident().Certifier = practitioner; observation.VrdrInjuryIncident().HowInjuredDescription = value;
        if (DateTimeValue(record.Get("CINJDATE", "Date of Injury"), record.Get("CINJTIME", "Time of Injury")) is { } injuryDate) observation.Effective = new FhirDateTime(injuryDate);
        return observation;
    }

    private static Observation? CreateAge(ImportRecord record, Patient patient, string id)
    {
        string? raw = record.Get("AGE", "Decedent Age");
        string? number = raw?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal age)) return null;
        string unit = record.Get("AGEUNIT") ?? (raw?.Contains(' ') == true ? raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() : null) ?? "a";
        Observation observation = VrdrDecedentAge.Create(patient);
        observation.Id = $"observation-{id}-age"; observation.Status = ObservationStatus.Final;
        observation.Value = new Quantity(age, unit) { System = "http://unitsofmeasure.org", Code = AgeUnitCode(unit) }; return observation;
    }

    private static string? AgeUnitCode(string unit) => ImportRecord.Normalize(unit) switch
    {
        "a" or "y" or "yr" or "year" or "years" => "a",
        "mo" or "month" or "months" => "mo",
        "wk" or "week" or "weeks" => "wk",
        "d" or "day" or "days" => "d",
        "h" or "hr" or "hour" or "hours" => "h",
        _ => null
    };

    private static Observation? CreateDisposition(ImportRecord record, Patient patient, string id)
    {
        if (record.Get("DISPMETHOD") is not { } value) return null;
        Observation observation = VrdrDecedentDispositionMethod.Create(patient); observation.Id = $"observation-{id}-disposition"; observation.Value = new CodeableConcept { Text = NullIfWhiteSpace(value) }; return observation;
    }

    private static Procedure? CreateCertification(ImportRecord record, Patient patient, Practitioner practitioner, string id)
    {
        if (record.Get("CERTIFIER_NAME", "Certifier Name", "CERTIFIER_TYPE", "Certifier Type") is null) return null;
        Procedure procedure = VrdrDeathCertification.Create(patient); procedure.Id = $"procedure-{id}-death-certification"; procedure.Status = EventStatus.Completed;
        Procedure.PerformerComponent performer = new() { Actor = practitioner.AsReference() };
        if (record.Get("CERTIFIER_TYPE", "Certifier Type") is { } certifierType)
            performer.Function = CertifierType(certifierType);
        procedure.Performer = [performer];
        string? certifiedDate = record.Get("Certified Date");
        string? reportDate = certifiedDate ?? record.Get("REPORTDATE");
        string? reportTime = record.Get("REPORTTIME");
        if (DateTimeValue(reportDate, reportTime) is { } date) procedure.Performed = new FhirDateTime(date); return procedure;
    }

    private static CodeableConcept CertifierType(string value)
    {
        CodeableConcept concept = ImportRecord.Normalize(value) switch
        {
            "medicalexaminercoroner" or "medicalexaminerorcoroner" or "deathcertificationbymedicalexaminerorcoronerprocedure" => VrdrCertifierTypesVs.DeathCertByCME,
            "pronouncingcertifyingphysician" or "deathcertificationandverificationbyphysicianprocedure" => VrdrCertifierTypesVs.DeathCertAndVerifByPhysician,
            "certifyingphysician" or "deathcertificationbyphysicianprocedure" => VrdrCertifierTypesVs.DeathCertByPhysician,
            _ => VrdrCertifierTypesVs.Other
        };
        return (CodeableConcept)concept.DeepCopy();
    }

    private static Observation? CreatePregnancy(ImportRecord record, Patient patient, string id)
    {
        if (record.Get("PREGNANT", "Pregnancy status") is not { } value) return null;
        CodeableConcept[] knownStatuses =
        [
            CodeSystemDeathPregnancyStatus.NotPregnantPastYear,
            CodeSystemDeathPregnancyStatus.PregnantAtTimeOfDeath,
            CodeSystemDeathPregnancyStatus.NotpregnantButPregnant42DaysOfDeath,
            CodeSystemDeathPregnancyStatus.NotPregnantButPregnant43DaysTo1YearBeforeDeath,
            CodeSystemDeathPregnancyStatus.NotReportedOnCertificate,
            CodeSystemDeathPregnancyStatus.UnknownIfPregnantPastYear,
            CodeSystemDeathPregnancyStatus.NA
        ];
        string input = value.Trim();
        CodeableConcept? matchedStatus = knownStatuses.FirstOrDefault(status =>
            status.Coding.Any(coding => string.Equals(coding.Display?.Trim(), input, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(status.Text?.Trim(), input, StringComparison.OrdinalIgnoreCase));

        Observation observation = VrdrDecedentPregnancyStatus.Create(patient);
        observation.Id = $"observation-{id}-pregnancy";
        observation.Value = matchedStatus is null
            ? new CodeableConcept { Text = NullIfWhiteSpace(value) }
            : (CodeableConcept)matchedStatus.DeepCopy();
        return observation;
    }

    private static Observation? CreateTobacco(ImportRecord record, Patient patient, string id)
    {
        if (record.Get("TOBACCO", "Did Tobacco Use Contribute to Death?") is not { } value) return null;
        Observation observation = ObservationTobaccoUseContributedToDeath.Create(patient); observation.Id = $"observation-{id}-tobacco"; observation.ObservationTobaccoUseContributedToDeath().Value = Tobacco(value); return observation;
    }

    private static Location? CreateLocation(ImportRecord record, string id, bool injury)
    {
        string prefix = injury ? "INJURYLOCATION" : "DEATHLOCATION";
        Address? address = BuildAddress(record, prefix);
        string? text = record.Get(injury ? "Location of injury" : "Location of death");
        if (address is null && text is null) return null;
        Location location = injury ? VrdrInjuryLocation.Create("Injury location") : VrdrDeathLocation.Create("Death location");
        location.Id = $"location-{id}-{(injury ? "injury" : "death")}"; location.Status = Location.LocationStatus.Active; location.Address = address ?? new Address { Text = text }; return location;
    }

    private static Observation? CreateAutopsy(ImportRecord record, Patient patient, Practitioner practitioner, string id)
    {
        string? performed = record.Get("AUTOPSYPERFORMED", "Autopsy Performed?");
        if (performed is null) return null;
        Observation observation = new()
        {
            Id = $"observation-{id}-autopsy",
            Status = ObservationStatus.Final,
            Code = CodedConcept("http://loinc.org", "85699-7", "Autopsy was performed"),
            Subject = patient.AsReference(),
            Performer = [practitioner.AsReference()],
            Value = YesNoUnknown(performed)
        };
        observation.AddProfile("http://hl7.org/fhir/us/vrdr/StructureDefinition/vrdr-autopsy-performed-indicator");
        if (record.Get("AUTOPSYRESULTSAVAILABLE", "Autopsy Results Available?") is { } available)
        {
            observation.Component.Add(new Observation.ComponentComponent
            {
                Code = CodedConcept("http://loinc.org", "69436-4", "Autopsy results available"),
                Value = YesNoUnknown(available)
            });
        }
        return observation;
    }

    private static Location? CreateAutopsyLocation(ImportRecord record, string id)
    {
        string? name = record.Get("AUTOPSYPERFORMED_OFFICENAME", "Autopsy Performed Office Name");
        Address? address = BuildAddress(record, "AUTOPSYLOCATION");
        if (name is null && address is null) return null;
        Location location = UsCoreLocation.Create(name ?? "Autopsy location");
        location.Id = $"location-{id}-autopsy";
        location.Status = Location.LocationStatus.Active;
        if (address is not null) location.Address = address;
        return location;
    }

    private static CodeableConcept YesNoUnknown(string value) => value.ToLowerInvariant() switch
    {
        "yes" => MdiVsYesNoUnknown.Yes,
        "no" => MdiVsYesNoUnknown.No,
        _ => MdiVsYesNoUnknown.UNK
    };

    private static Address? BuildAddress(ImportRecord record, string prefix, Address.AddressUse use = Address.AddressUse.Home)
    {
        string? street = AddressPart(record, prefix, "Street"); string? city = AddressPart(record, prefix, "City");
        string? county = AddressPart(record, prefix, "County"); string? state = AddressPart(record, prefix, "State, U.S. Territory or Canadian Province");
        string? zip = AddressPart(record, prefix, "Postal Code"); string? country = AddressPart(record, prefix, "Country");
        if (new[] { street, city, county, state, zip, country }.All(value => value is null)) return null;
        return new Address { Use = use, Line = street is null ? null : [street], City = city, District = county, State = state, PostalCode = zip, Country = country };
    }

    private static string? AddressPart(ImportRecord record, string prefix, string part)
    {
        List<string> names = [$"{prefix}_{part.ToUpperInvariant()}", $"{prefix}: {part}"];
        switch (prefix)
        {
            case "RES": names.Add($"Decedent Residence: {part}"); break;
            case "ME": names.Add($"Medical Examiner Office: {part}"); break;
            case "DEATHLOCATION": names.Add($"Location of Death: {part}"); break;
            case "INJURYLOCATION": names.Add($"Location of Injury: {part}"); break;
            case "AUTOPSYLOCATION": names.Add($"Autopsy Performed Location: {part}"); break;
            case "Funeral Home Address": names.Add($"Funeral Home: {part}"); break;
        }
        if (part == "State, U.S. Territory or Canadian Province") names.Add($"{prefix}: State");
        if (part == "Postal Code") names.Add($"{prefix}: Zip");
        return GetExactValue(record, names.ToArray());
    }

    private static string? GetExactValue(ImportRecord record, params string[] names)
    {
        foreach (string name in names)
        {
            if (record.Fields.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static (List<string>, string?) Name(ImportRecord record)
    {
        string? first = record.Get("FIRSTNAME", "First Name"); string? middle = record.Get("MIDNAME", "Middle Name"); string? last = record.Get("LASTNAME", "Last Name");
        string? full = record.Get("Decedent Name");
        if (first is null && last is null && full is not null)
        {
            (List<string> given, string? family) = SplitName(full);
            return (given, family);
        }
        return (new[] { first, middle }.Where(value => value is not null).Cast<string>().ToList(), last);
    }

    private static (List<string> Given, string? Family) SplitName(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length > 1 ? parts[..^1].ToList() : [], parts.Length > 0 ? parts[^1] : null);
    }

    private static Quantity? ParseQuantity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string input = value.Trim();
        Quantity.QuantityComparator? comparator = null;
        string? comparatorText = input switch
        {
            ['<', '=', ..] => "<=",
            ['>', '=', ..] => ">=",
            ['<', ..] => "<",
            ['>', ..] => ">",
            _ => null
        };
        if (comparatorText is not null)
        {
            input = input[comparatorText.Length..].TrimStart();
            comparator = comparatorText switch
            {
                "<" => Quantity.QuantityComparator.LessThan,
                ">" => Quantity.QuantityComparator.GreaterThan,
                "<=" => Quantity.QuantityComparator.LessOrEqual,
                ">=" => Quantity.QuantityComparator.GreaterOrEqual,
                _ => null
            };
        }
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)) return null;
        string? unit = parts.Length > 1 ? parts[1].Trim() : null;
        return new Quantity(amount, unit ?? "") { System = "http://unitsofmeasure.org", Code = unit, Comparator = comparator };
    }

    private static CodeableConcept CodeableConceptFromValue(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.Contains('^')) return new CodeableConcept { Text = trimmed };
        string[] segments = trimmed.Split('^');
        return new CodeableConcept
        {
            Coding = [new Coding
            {
                System = FhirCodingSystemUri(segments[0]),
                Code = segments[1].Trim(),
                Display = segments.Length > 2 ? string.Join("^", segments.Skip(2).Select(segment => segment.Trim())) : null
            }]
        };
    }

    private static string? DateTimeValueFromText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset offset))
            return offset.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime local))
            return local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return null;
    }

    private static Instant? InstantValueFromText(string? value)
    {
        string? dateTime = DateTimeValueFromText(value);
        return dateTime is not null && DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset offset)
            ? new Instant(offset)
            : null;
    }

    private static string FhirCodingSystemUri(string value)
    {
        string system = value.Trim();
        string normalized = ImportRecord.Normalize(system);
        return normalized switch
        {
            "loinc" => UriString.LOINC,
            "sct" or "snomed" or "snomedct" => UriString.SCT,
            "ucum" or "unitsofmeasure" => UriString.UnitsOfMeasure,
            "ssn" or "usssn" => UriString.UsSsnUri,
            _ when Uri.TryCreate(system, UriKind.Absolute, out _) => system,
            _ => throw new FormatException($"Lab Report Code system '{system}' must be a FHIR URI.")
        };
    }

    private static void SetMdiCaseNumber(DiagnosticReport report, string system, string value)
    {
        Identifier identifier = new(system, value)
        {
            Type = (CodeableConcept)VsTrackingNumberType.MdiCaseNumber.DeepCopy()
        };
        report.Extension.Add(new Extension(
            "http://hl7.org/fhir/us/mdi/StructureDefinition/Extension-tracking-number",
            identifier));
    }

    private static (List<Resource>, Narrative?, CodeableConcept?) SectionValue(List<Resource> resources) =>
        resources.Count == 0 ? (null!, null, ListEmptyReason.Unavailable) : (resources, null, null);

    private static List<Resource> Resources(params Resource?[] values) => values.Where(value => value is not null).Cast<Resource>().ToList();

    private static void AppendResources(Bundle bundle, IEnumerable<Resource?> resources)
    {
        foreach (Resource resource in resources.Where(value => value is not null).Cast<Resource>())
        {
            if (bundle.Entry.Any(entry => entry.Resource?.GetType() == resource.GetType() && entry.Resource.Id == resource.Id)) continue;
            bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = resource.AsReference().Reference, Resource = resource });
        }
    }

    private static string? DateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset date)) return date.ToString("yyyy-MM-dd");
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime local)) return local.ToString("yyyy-MM-dd");
        throw new FormatException($"Invalid date value '{value}'.");
    }

    private static string? DateTimeValue(string? date, string? time)
    {
        string? onlyDate = DateOnly(date);
        if (onlyDate is null) return null;
        if (string.IsNullOrWhiteSpace(time)) return onlyDate;
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed)) throw new FormatException($"Invalid time value '{time}'.");
        return $"{onlyDate}T{parsed:HH:mm:ss}Z";
    }

    private static CodeableConcept PlaceOfDeath(string value) => value.ToLowerInvariant() switch
    {
        "dead on arrival at hospital" => MdiVsPlaceOfDeath.DeadOnArrivalAtHospital, "death in home" => MdiVsPlaceOfDeath.DeathInHome,
        "death in hospice" => MdiVsPlaceOfDeath.DeathInHospice, "death in hospital" => MdiVsPlaceOfDeath.DeathInHospital, _ => MdiVsPlaceOfDeath.Other
    };

    private static CodeableConcept Tobacco(string value) => value.ToLowerInvariant() switch
    {
        "yes" => MdiVsContributoryTobaccoUse.Yes, "no" => MdiVsContributoryTobaccoUse.No, "probably" => MdiVsContributoryTobaccoUse.Probably,
        "no information" => MdiVsContributoryTobaccoUse.NotAsked, _ => MdiVsContributoryTobaccoUse.Unknown
    };

    private static CodeableConcept DcrReason(string? value)
    {
        string reason = string.IsNullOrWhiteSpace(value) ? "DC_MED_DATA_Q_REQ" : value.Trim();
        CodeableConcept? knownReason = reason.ToUpperInvariant() switch
        {
            "DC_MED_DATA_Q_REQ" => MdiCodeSystem.CsDeathCertReviewExample.DcMedDataQReq,
            "DC_MED_DATA_Q_RSP" => MdiCodeSystem.CsDeathCertReviewExample.DcMedDataQRsp,
            "DC_PER_DATA_Q_REQ" => MdiCodeSystem.CsDeathCertReviewExample.DcPerDataQReq,
            "DC_PER_DATA_Q_RSP" => MdiCodeSystem.CsDeathCertReviewExample.DcPerDataQRsp,
            "CREM_C_REQ" => MdiCodeSystem.CsDeathCertReviewExample.CremCReq,
            "CREM_C_RSP" => MdiCodeSystem.CsDeathCertReviewExample.CremCRsp,
            _ => null
        };
        if (knownReason is not null)
        {
            CodeableConcept copy = (CodeableConcept)knownReason.DeepCopy();
            foreach (Coding coding in copy.Coding)
            {
                if (coding.System is not null) coding.System = coding.System.Trim();
            }
            return copy;
        }
        return CodedConcept("http://hl7.org/fhir/us/mdi/CodeSystem/cs-death-cert-review-example", reason, reason);
    }

    private static string CaseKey(ImportRecord record, string prefix)
    {
        return record.Get("BASEFHIRID", "File Id", "MDICASEID", "EDRSCASEID") ??
               $"{prefix}-{StableToken(record.CanonicalFingerprint(), 24)}";
    }

    private static string CaseToken(ImportRecord record, string caseKey)
    {
        string identity = string.IsNullOrWhiteSpace(record.IdentityDiscriminator)
            ? caseKey
            : $"{caseKey}|{record.IdentityDiscriminator}";
        return StableToken(identity, 24);
    }

    private static string CanonicalRow(Dictionary<string, string?> row) => string.Join(
        "\n",
        row.OrderBy(pair => ImportRecord.Normalize(pair.Key), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{ImportRecord.Normalize(pair.Key)}={pair.Value?.Trim() ?? ""}"));

    private static string StableToken(string value, int length)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return hash[..Math.Min(length, hash.Length)];
    }

    private static string DeterministicUuid(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static CodeableConcept CodedConcept(string codingSystem, string code, string display, string? text = null) => new()
    {
        Coding = [new Coding(codingSystem, code, display)],
        Text = NullIfWhiteSpace(text)
    };
}
