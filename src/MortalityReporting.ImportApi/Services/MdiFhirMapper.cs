using System.Globalization;
using System.Text.Json.Nodes;
using GaTech.Chai.Mdi;
using GaTech.Chai.Share;
using GaTech.Chai.UsCore;
using GaTech.Chai.Vrcl;
using GaTech.Chai.Vrdr;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using MortalityReporting.ImportApi.Models;

namespace MortalityReporting.ImportApi.Services;

public sealed class MdiFhirMapper
{
    private readonly object registryLock = new();

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
            return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("FHIR serializer returned invalid JSON.");
        }
    }

    private static Bundle MapMdi(ImportRecord record)
    {
        string caseKey = record.Get("BASEFHIRID", "File Id", "MDICASEID", "EDRSCASEID") ?? $"case-{Guid.NewGuid():N}";
        string id = SafeId(caseKey);
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
            (null, new Narrative { Status = Narrative.NarrativeStatus.Generated, Div = "Medical history is not available." }, ListEmptyReason.Unavailable);
        composition.CompositionMdiAndEdrs().ExamAutopsy = (null, null, ListEmptyReason.Unavailable);

        Bundle bundle = BundleDocumentMdiAndEdrs.Create(new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{Guid.NewGuid()}"), composition);
        bundle.Id = $"bundle-{id}";
        bundle.TimestampElement = Instant.Now();
        return bundle;
    }

    private static Bundle MapToxicology(ImportRecord record)
    {
        string id = SafeId(record.Get("File Id", "MDI Case Number") ?? $"tox-{Guid.NewGuid():N}");
        Patient patient = CreatePatient(record, $"patient-{id}");
        Practitioner performer = VrdrCertifier.Create();
        performer.Id = $"practitioner-{id}";
        performer.Name = [new HumanName { Family = record.Get("Performer") ?? "Unknown" }];
        Organization? laboratory = CreateLaboratory(record, $"organization-{id}");

        DiagnosticReport report = DiagnosticReportToxicologyLabResultToMdi.Create(patient);
        report.Id = $"diagnostic-report-{id}";
        report.Status = DiagnosticReport.DiagnosticReportStatus.Final;
        report.DiagnosticReportToxicologyLabResultToMdi().AddPerformer(performer);
        if (laboratory is not null) report.DiagnosticReportToxicologyLabResultToMdi().AddPerformer(laboratory);
        report.DiagnosticReportToxicologyLabResultToMdi().MdiCaseNumber = ("urn:mdi:case", record.Get("MDI Case Number") ?? id);
        if (record.Get("Laboratory Case Number") is { } laboratoryCase)
            report.DiagnosticReportToxicologyLabResultToMdi().ToxLabCaseNumber = ("urn:mdi:tox-lab-case", laboratoryCase);
        string[] notes = (record.Tables.GetValueOrDefault("Notes") ?? [])
            .Select(row => row.GetValueOrDefault("Note Content"))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
        if (notes.Length > 0) report.Conclusion = string.Join(Environment.NewLine, notes);

        Dictionary<string, Specimen> specimens = new(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, string?> row in record.Tables.GetValueOrDefault("Specimens") ?? [])
        {
            string name = row.GetValueOrDefault("Name") ?? "Specimen";
            Specimen specimen = SpecimenToxicologyLab.Create(name, patient);
            specimen.Id = $"specimen-{id}-{SafeId(row.GetValueOrDefault("Identifier") ?? name)}";
            report.DiagnosticReportToxicologyLabResultToMdi().AddSpecimen(specimen);
            specimens[name.Trim()] = specimen;
        }
        foreach (Dictionary<string, string?> row in record.Tables.GetValueOrDefault("Results") ?? [])
        {
            string analyte = row.GetValueOrDefault("Analyte/Analysis") ?? "Toxicology result";
            Observation result = ObservationToxicologyLabResult.Create(ObservationStatus.Final, analyte, patient);
            result.Id = $"observation-{id}-{SafeId(analyte)}";
            result.ObservationToxicologyLabResult().ValueText = row.GetValueOrDefault("Value") ?? "";
            if (row.GetValueOrDefault("Specimen") is { } specimenName && specimens.TryGetValue(specimenName.Trim(), out Specimen? specimen))
                result.ObservationToxicologyLabResult().Specimen = specimen;
            report.DiagnosticReportToxicologyLabResultToMdi().AddResult(result);
        }

        MessageHeader header = MessageHeaderToxicologyToMdi.Create("urn:mdi:import", report);
        header.Id = $"message-header-{id}";
        Bundle bundle = BundleMessageToxicologyToMdi.Create(new Identifier("urn:mdi:case", id), header);
        bundle.Id = $"bundle-{id}";
        bundle.TimestampElement = Instant.Now();
        return bundle;
    }

    private static Bundle MapDcr(ImportRecord record)
    {
        string caseKey = record.Get("BASEFHIRID", "File Id", "MDICASEID", "EDRSCASEID") ?? $"dcr-{Guid.NewGuid():N}";
        string id = SafeId(caseKey);
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

        Composition composition = CompositionMdiDcr.Create(
            new Identifier("urn:mdi:case", caseKey), CompositionStatus.Preliminary, patient, submitter ?? practitioner);
        composition.Id = $"composition-{id}";
        if (record.Get("MDICASEID", "Tracking Number: Mdi Case Number") is { } mdiCase)
            composition.CompositionMdiDcr().MdiCaseNumber = ("urn:mdi:case", mdiCase);
        if (record.Get("EDRSCASEID", "Tracking Number: EDRS File Number") is { } edrsCase)
            composition.CompositionMdiDcr().EdrsFileNumber = ("urn:mdi:edrs", edrsCase);

        composition.CompositionMdiDcr().DecedentDemographics = (Resources(patient), null);
        List<Resource?> investigationResources = [deathDate, manner, injury, deathLocation, injuryLocation];
        investigationResources.AddRange(causes);
        composition.CompositionMdiDcr().DeathInvestigation = (Resources(investigationResources.ToArray()), null);
        composition.CompositionMdiDcr().DeathCertification = (Resources(practitioner, submitter, certification), null);
        composition.CompositionMdiDcr().DecedentDisposition = (Resources(disposition, funeralHome), null);
        composition.CompositionMdiDcr().DeathCertificateDataReview = (null, ListEmptyReason.Unavailable);
        composition.CompositionMdiDcr().CremationClearanceInfo = (null, ListEmptyReason.Unavailable);

        Bundle document = BundleDocumentMdiDcr.Create(new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{Guid.NewGuid()}"), composition);
        document.Id = $"bundle-document-{id}";
        List<Resource?> dcrResources = [patient, practitioner, submitter, funeralHome, deathDate, manner, injury, disposition, certification, deathLocation, injuryLocation];
        dcrResources.AddRange(causes);
        AppendResources(document, dcrResources);
        CodeableConcept reason = DcrReason(record.Get("Message Reason"));
        MessageHeader header = MessageHeaderDeathCertificateReview.Create("urn:mdi:import", reason, document);
        header.Id = $"message-header-{id}";
        Bundle message = BundleMessageDeathCertificateReview.Create(header);
        message.Id = $"bundle-{id}";
        message.TimestampElement = Instant.Now();
        AppendResources(message, dcrResources);
        return message;
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
        if (record.Get("MRNNUMBER") is { } mrn)
            patient.Identifier.Add(new Identifier("urn:mdi:mrn", mrn) { Use = Identifier.IdentifierUse.Usual, Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MR", "Medical record number", "") });
        if (BuildAddress(record, "RES") is { } address) patient.Address = [address];
        if (record.Get("LANGUAGE") is { } language) patient.Communication = [new Patient.CommunicationComponent { Language = new CodeableConcept { Text = language } }];
        if (record.Get("MARITAL", "Decedent Marital status") is { } marital) patient.MaritalStatus = new CodeableConcept { Text = marital };
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
        return practitioner;
    }

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
        string? name = record.Get("FUNERAL_HOME_NAME", "Funeral Home Name");
        Address? address = BuildAddress(record, "Funeral Home Address");
        string? phone = record.Get("FUNERAL_HOME_PHONE", "Funeral Home Phone Number");
        string? fax = record.Get("FUNERAL_HOME_FAX", "Funeral Home Fax Number");
        if (name is null && address is null && phone is null && fax is null) return null;
        Organization organization = UsCoreOrganization.Create(name ?? "Funeral home");
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
        Address? address = BuildAddress(record, "Lab Address");
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
        observation.Id = $"observation-{SafeId(patient.Id ?? "case")}-cause-part1-{line}";
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
        observation.Value = new Quantity(age, unit) { System = "http://unitsofmeasure.org", Code = unit }; return observation;
    }

    private static Observation? CreateDisposition(ImportRecord record, Patient patient, string id)
    {
        if (record.Get("DISPMETHOD") is not { } value) return null;
        Observation observation = VrdrDecedentDispositionMethod.Create(patient); observation.Id = $"observation-{id}-disposition"; observation.Value = new CodeableConcept { Text = value }; return observation;
    }

    private static Procedure? CreateCertification(ImportRecord record, Patient patient, Practitioner practitioner, string id)
    {
        if (record.Get("CERTIFIER_NAME", "Certifier Name", "CERTIFIER_TYPE", "Certifier Type") is null) return null;
        Procedure procedure = ProcedureDeathCertification.Create(patient); procedure.Id = $"procedure-{id}-death-certification"; procedure.Status = EventStatus.Completed;
        procedure.ProcedureDeathCertification().Certifier = (MdiVsCertifierTypes.MedicalExaminerCornerExamination, practitioner);
        if (DateTimeValue(record.Get("REPORTDATE"), record.Get("REPORTTIME")) is { } date) procedure.Performed = new FhirDateTime(date); return procedure;
    }

    private static Observation? CreatePregnancy(ImportRecord record, Patient patient, string id)
    {
        if (record.Get("PREGNANT", "Pregnancy status") is not { } value) return null;
        Observation observation = VrdrDecedentPregnancyStatus.Create(patient); observation.Id = $"observation-{id}-pregnancy"; observation.Value = new CodeableConcept { Text = value }; return observation;
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

    private static Address? BuildAddress(ImportRecord record, string prefix)
    {
        string? street = record.Get($"{prefix}_STREET", $"{prefix}: Street"); string? city = record.Get($"{prefix}_CITY", $"{prefix}: City");
        string? county = record.Get($"{prefix}_COUNTY", $"{prefix}: County"); string? state = record.Get($"{prefix}_STATE", $"{prefix}: State, U.S. Territory or Canadian Province");
        string? zip = record.Get($"{prefix}_ZIP", $"{prefix}: Postal Code"); string? country = record.Get($"{prefix}_COUNTRY", $"{prefix}: Country");
        if (new[] { street, city, county, state, zip, country }.All(value => value is null)) return null;
        return new Address { Use = Address.AddressUse.Home, Line = street is null ? null : [street], City = city, District = county, State = state, PostalCode = zip, Country = country };
    }

    private static (List<string>, string?) Name(ImportRecord record)
    {
        string? first = record.Get("FIRSTNAME", "First Name"); string? middle = record.Get("MIDNAME", "Middle Name"); string? last = record.Get("LASTNAME", "Last Name");
        string? full = record.Get("Decedent Name");
        if (first is null && last is null && full is not null)
        {
            string[] parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries); first = parts.FirstOrDefault(); last = parts.Length > 1 ? parts[^1] : null; middle = parts.Length > 2 ? string.Join(' ', parts[1..^1]) : null;
        }
        return (new[] { first, middle }.Where(value => value is not null).Cast<string>().ToList(), last);
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
        string? onlyDate = DateOnly(date); if (onlyDate is null) return null; if (string.IsNullOrWhiteSpace(time)) return onlyDate;
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed)) throw new FormatException($"Invalid time value '{time}'.");
        return $"{onlyDate}T{parsed:HH:mm:ss}";
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
        string reason = value ?? "DC_MED_DATA_Q_REQ";
        return new CodeableConcept("http://hl7.org/fhir/us/mdi/CodeSystem/cs-death-cert-review-example", reason, reason, "");
    }

    private static string SafeId(string value)
    {
        string id = new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(48).ToArray());
        return id.Length > 0 ? id : Guid.NewGuid().ToString("N");
    }
}
