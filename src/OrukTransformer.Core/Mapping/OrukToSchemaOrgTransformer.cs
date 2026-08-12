using OrukModels.Models;
using OrukModels.SchemaOrg;
using OrukTransformer.Core.Vodim;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrukTransformer.Core.Mapping;

/// <summary>
/// Transforms a single <see cref="OrukService"/> (with its navigation graph) into a
/// Schema.org JSON-LD <c>@graph</c> document, recording a field-by-field VODIM
/// data-quality classification for every ORUK field that could potentially be mapped.
///
/// <para>
/// The transformer never throws for data-quality reasons; all issues are captured in
/// the returned <see cref="TransformationReport"/>.
/// </para>
/// </summary>
/// <remarks>
/// Mapping rules follow <c>plan/mapping.md</c> in the repository.
/// </remarks>
public sealed partial class OrukToSchemaOrgTransformer : IOrukToSchemaOrgTransformer
{
    // ── Constants ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Upper bound for a plausible human age in years.
    /// Values above this are classified as Invalid.
    /// </summary>
    private const double MaxPlausibleHumanAge = 130;

    // Slug: alphanumeric segments joined by single hyphens/underscores (e.g. "food-bank-2").
    private static readonly Regex SlugRegex = new(@"^[A-Za-z0-9]+([-_][A-Za-z0-9]+)*$");

    // ── ORUK vocabulary constants ────────────────────────────────────────────────

    private static readonly HashSet<string> ValidOrukStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "active", "inactive", "defunct", "temporarily closed"
        };

    /// <summary>
    /// RRULE short-form day codes → Schema.org DayOfWeek URI.
    /// Long-form English day names are also accepted (classified as "Other").
    /// </summary>
    private static readonly Dictionary<string, string> RruleDayToSchemaOrg =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MO"] = SchemaDayOfWeek.Monday,
            ["TU"] = SchemaDayOfWeek.Tuesday,
            ["WE"] = SchemaDayOfWeek.Wednesday,
            ["TH"] = SchemaDayOfWeek.Thursday,
            ["FR"] = SchemaDayOfWeek.Friday,
            ["SA"] = SchemaDayOfWeek.Saturday,
            ["SU"] = SchemaDayOfWeek.Sunday,
        };

    private static readonly Dictionary<string, string> LongDayToSchemaOrg =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Monday"] = SchemaDayOfWeek.Monday,
            ["Tuesday"] = SchemaDayOfWeek.Tuesday,
            ["Wednesday"] = SchemaDayOfWeek.Wednesday,
            ["Thursday"] = SchemaDayOfWeek.Thursday,
            ["Friday"] = SchemaDayOfWeek.Friday,
            ["Saturday"] = SchemaDayOfWeek.Saturday,
            ["Sunday"] = SchemaDayOfWeek.Sunday,
            ["PublicHolidays"] = SchemaDayOfWeek.PublicHolidays,
        };

    private static readonly HashSet<string> KnownTaxonomyPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "esdstandards", "esd standards", "snomed", "snomed ct", "loinc", "icd-10"
        };

    // ── IOrukToSchemaOrgTransformer ──────────────────────────────────────────────

    /// <inheritdoc/>
    public TransformationResult Transform(OrukService service, TransformationOptions options)
    {
        var report = new TransformationReport(service.Id);
        var nodes = new List<SchemaOrgThing>();

        // 1. Service → GovernmentService
        var schemaService = MapService(service, options, report);
        nodes.Add(schemaService);

        // 2. Organization → Organization (if present)
        if (service.Organization is not null)
        {
            var org = MapOrganization(service.Organization, options, report);
            nodes.Add(org);
        }
        else
        {
            Record(report, "service.organization", "GovernmentService.provider",
                VodimClassification.Missing, note: "No organization navigation object present.");
        }

        // 3. Locations via ServiceAtLocations
        foreach (var sal in service.ServiceAtLocations)
        {
            if (sal.Location is not null)
            {
                var place = MapLocation(sal.Location, options, report);
                nodes.Add(place);
            }
        }

        var document = new SchemaOrgDocument { Graph = nodes };
        return new TransformationResult { Document = document, Report = report };
    }

    // ── Service mapping ──────────────────────────────────────────────────────────

    private SchemaOrgGovernmentService MapService(
        OrukService service, TransformationOptions options, TransformationReport report)
    {
        // @id
        var serviceId = MapRequiredIdentifier(report,
            "service.id", "GovernmentService.@id", service.Id);
        var serviceUri = serviceId is not null ? options.ServiceUri(serviceId) : null;

        // name (required by Schema.org)
        var name = MapRequiredString(report,
            "service.name", "GovernmentService.name", service.Name);

        // alternate_name
        Record(report, "service.alternate_name", "GovernmentService.alternateName",
            Classify(service.AlternateName), service.AlternateName, service.AlternateName);

        // description — HTML stripped for schema.org compatibility
        var description = StripHtml(service.Description);
        Record(report, "service.description", "GovernmentService.description",
            Classify(service.Description), Truncate(service.Description), Truncate(description),
            HtmlNote(service.Description));

        // url
        var (urlClass, urlNote) = ClassifyUrl(service.Url);
        Record(report, "service.url", "GovernmentService.url",
            urlClass, service.Url, urlClass == VodimClassification.Valid ? service.Url : null, urlNote);

        // email — no schema.org mapping; service-level email is not representable
        // as a property of GovernmentService or its ContactPoint in schema.org.
        Record(report, "service.email", "—",
            string.IsNullOrWhiteSpace(service.Email)
                ? VodimClassification.Missing : VodimClassification.Unmapped,
            service.Email, null,
            "No schema.org mapping for service-level email. Field is intentionally omitted from output.");

        // status → additionalProperty[orukStatus]
        var (statusClass, statusNote) = ClassifyOrukStatus(service.Status);
        Record(report, "service.status", "GovernmentService.additionalProperty[orukStatus]",
            statusClass, service.Status,
            statusClass is VodimClassification.Valid or VodimClassification.Other
                ? service.Status : null,
            statusNote);

        // interpretation_services — VODIM recorded inside MapLanguagesWithFallback
        // once we know whether the fallback is needed (structured languages take priority).

        // application_process → termsOfService — HTML stripped for schema.org compatibility
        var applicationProcess = StripHtml(service.ApplicationProcess);
        Record(report, "service.application_process", "GovernmentService.termsOfService",
            Classify(service.ApplicationProcess),
            Truncate(service.ApplicationProcess), Truncate(applicationProcess),
            HtmlNote(service.ApplicationProcess));

        // fees_description → used as fallback in Offer.description — HTML stripped
        var feesDescription = StripHtml(service.FeesDescription);
        Record(report, "service.fees_description", "GovernmentService.offers[].description",
            Classify(service.FeesDescription),
            service.FeesDescription, feesDescription,
            service.FeesDescription is not null
                ? string.Join(" ", new[]
                  {
                      "Used as fallback offer description when no cost_options are present.",
                      HtmlNote(service.FeesDescription)
                  }.Where(n => n is not null))
                : null);

        // fees (deprecated)
        Record(report, "service.fees", "—",
            string.IsNullOrEmpty(service.Fees) ? VodimClassification.Missing : VodimClassification.Unmapped,
            service.Fees, null,
            "Deprecated HSDS field — superseded by cost_options. No Schema.org mapping.");

        // wait_time (deprecated) → additionalProperty[waitTime]
        Record(report, "service.wait_time", "GovernmentService.additionalProperty[waitTime]",
            string.IsNullOrEmpty(service.WaitTime)
                ? VodimClassification.Missing
                : VodimClassification.Other,
            service.WaitTime, service.WaitTime,
            service.WaitTime is not null ? "Deprecated HSDS field. Preserved in additionalProperty." : null);

        // accreditations → hasCredential — HTML stripped
        var accreditations = StripHtml(service.Accreditations);
        Record(report, "service.accreditations", "GovernmentService.hasCredential",
            Classify(service.Accreditations), service.Accreditations, accreditations,
            HtmlNote(service.Accreditations));

        // eligibility_description → audience.description — HTML stripped
        var eligibilityDescription = StripHtml(service.EligibilityDescription);
        Record(report, "service.eligibility_description", "GovernmentService.audience.description",
            Classify(service.EligibilityDescription),
            Truncate(service.EligibilityDescription), Truncate(eligibilityDescription),
            HtmlNote(service.EligibilityDescription));

        // minimum_age
        var (minAgeClass, minAgeNote, minAgeVal) = ClassifyAge(service.MinimumAge, "minimum_age");
        Record(report, "service.minimum_age", "GovernmentService.audience.suggestedMinAge",
            minAgeClass, service.MinimumAge?.ToString(), minAgeVal?.ToString(), minAgeNote);

        // maximum_age
        var (maxAgeClass, maxAgeNote, maxAgeVal) = ClassifyAge(service.MaximumAge, "maximum_age");
        Record(report, "service.maximum_age", "GovernmentService.audience.suggestedMaxAge",
            maxAgeClass, service.MaximumAge?.ToString(), maxAgeVal?.ToString(), maxAgeNote);

        // assured_date → additionalProperty[assuredDate]
        var (dateClass, dateNote) = ClassifyDate(service.AssuredDate);
        Record(report, "service.assured_date", "GovernmentService.additionalProperty[assuredDate]",
            dateClass, service.AssuredDate,
            dateClass == VodimClassification.Valid ? service.AssuredDate : null, dateNote);

        // assurer_email → additionalProperty[assuredBy]
        var (assurerClass, assurerNote) = ClassifyEmail(service.AssurerEmail);
        Record(report, "service.assurer_email", "GovernmentService.additionalProperty[assuredBy]",
            assurerClass, service.AssurerEmail,
            assurerClass == VodimClassification.Valid ? service.AssurerEmail : null, assurerNote);

        // licenses (deprecated)
        Record(report, "service.licenses", "—",
            string.IsNullOrEmpty(service.Licenses) ? VodimClassification.Missing : VodimClassification.Unmapped,
            service.Licenses, null,
            "Deprecated HSDS field. No Schema.org mapping.");

        // alert → no Schema.org equivalent. A feed that supplies a JSON object/array here
        // (e.g. Southampton's {"text": "…"}) is flagged as a nonconformance by Record(),
        // which detects structured JSON in any string-typed field.
        Record(report, "service.alert", "—",
            string.IsNullOrEmpty(service.Alert) ? VodimClassification.Missing : VodimClassification.Unmapped,
            service.Alert, null, "No Schema.org mapping defined for alert.");

        // last_modified → additionalProperty[dateModified]
        var (dateModClass, dateModNote) = ClassifyDate(service.LastModified);
        Record(report, "service.last_modified", "additionalProperty[dateModified]",
            dateModClass, service.LastModified,
            dateModClass == VodimClassification.Valid ? service.LastModified : null,
            dateModNote);

        // ── Build mapped navigation properties ──────────────────────────────────

        // provider @id reference
        string? providerRef = service.Organization is not null
            ? options.OrganisationUri(service.Organization.Id)
            : null;

        // serviceOperator — for GovernmentService, same as provider
        string? serviceOperatorRef = providerRef;

        // jurisdiction — derive from service_areas or feed base URL
        string? jurisdiction = DetermineJurisdiction(service, options);

        // locations (via service_at_locations)
        var locationRefs = service.ServiceAtLocations
            .Where(sal => sal.Location is not null)
            .Select(sal => (object)new Dictionary<string, string>
                { ["@id"] = options.LocationUri(sal.Location!.Id) })
            .ToList();
        RecordCollectionMapping(report,
            "service.service_at_locations", "GovernmentService.location",
            service.ServiceAtLocations.Count, locationRefs.Count);

        // schedules → openingHoursSpecification
        var allSchedules = service.Schedules.ToList();
        // Also pick up schedules from service_at_locations
        foreach (var sal in service.ServiceAtLocations)
            allSchedules.AddRange(sal.Schedules);

        var openingHours = MapSchedules(allSchedules, "service.schedules", report);

        // contacts → contactPoint
        var contactPoints = new List<SchemaOrgContactPoint>();
        contactPoints.AddRange(MapServiceContacts(service.Contacts, service.Phones, report));

        // languages → availableLanguage (with interpretation_services fallback)
        var languages = MapLanguagesWithFallback(service.Languages, service.InterpretationServices,
            "service.languages", report);

        // cost_options → offers (pass stripped feesDescription as fallback)
        var offers = MapCostOptions(service.CostOptions, feesDescription, options, report);

        // eligibility → audience (prefer structured over eligibility_description)
        var audience = MapAudience(service.Eligibility, eligibilityDescription,
            service.MinimumAge, service.MaximumAge, report);

        // service_areas → areaServed
        var areasServed = MapServiceAreas(service.ServiceAreas, options, report);

        // attributes / taxonomy terms → additionalType + keywords
        var (additionalTypes, keywords) = MapAttributes(service.Attributes, "service", report);

        // accreditations → hasCredential (free-text, HTML-stripped)
        var credentials = string.IsNullOrWhiteSpace(accreditations)
            ? null
            : new List<SchemaOrgEducationalOccupationalCredential>
            {
                new() { Name = accreditations }
            };

        // additionalProperty — unmappable-but-preserve fields (including dateModified via additionalProperty)
        var additionalProps = BuildServiceAdditionalProperties(service, dateModClass, service.LastModified);

        return new SchemaOrgGovernmentService
        {
            Id = serviceUri,
            Name = name,
            AlternateName = service.AlternateName,
            Description = description,
            Url = urlClass == VodimClassification.Valid ? service.Url : null,
            Provider = providerRef is not null
                ? new Dictionary<string, string> { ["@id"] = providerRef }
                : null,
            ServiceOperator = serviceOperatorRef is not null
                ? new Dictionary<string, string> { ["@id"] = serviceOperatorRef }
                : null,
            Jurisdiction = jurisdiction,
            Location = locationRefs.Count > 0 ? locationRefs : null,
            OpeningHoursSpecification = openingHours.Count > 0 ? openingHours : null,
            ContactPoint = contactPoints.Count > 0 ? contactPoints : null,
            AvailableLanguage = languages.Count > 0 ? languages : null,
            Offers = offers.Count > 0 ? offers : null,
            Audience = audience,
            AreaServed = areasServed.Count > 0 ? areasServed : null,
            AdditionalType = additionalTypes.Count > 0 ? additionalTypes : null,
            Keywords = keywords.Count > 0 ? string.Join(", ", keywords) : null,
            HasCredential = credentials,
            TermsOfService = applicationProcess,
            AdditionalProperty = additionalProps.Count > 0 ? additionalProps : null,
        };
    }

    // ── Organization mapping ─────────────────────────────────────────────────────

    private SchemaOrgOrganization MapOrganization(
        OrukOrganization org, TransformationOptions options, TransformationReport report)
    {
        // @id
        var orgUri = options.OrganisationUri(org.Id);
        var (orgIdClass, orgIdNote) = ClassifyIdentifier(org.Id);
        Record(report, "organization.id", "Organization.@id", orgIdClass, org.Id, orgUri, orgIdNote);

        // name
        Record(report, "organization.name", "Organization.name",
            string.IsNullOrWhiteSpace(org.Name)
                ? VodimClassification.Missing : VodimClassification.Valid,
            org.Name, org.Name);

        // alternate_name
        Record(report, "organization.alternate_name", "Organization.alternateName",
            Classify(org.AlternateName), org.AlternateName, org.AlternateName);

        // description — HTML stripped
        var orgDescription = StripHtml(org.Description);
        Record(report, "organization.description", "Organization.description",
            Classify(org.Description), Truncate(org.Description), Truncate(orgDescription),
            HtmlNote(org.Description));

        // email
        var (emailClass, emailNote) = ClassifyEmail(org.Email);
        Record(report, "organization.email", "Organization.email",
            emailClass, org.Email,
            emailClass == VodimClassification.Valid ? org.Email : null, emailNote);

        // url / website (prefer url, fall back to website)
        var effectiveUrl = org.Url ?? org.Website;
        var (urlClass, urlNote) = ClassifyUrl(effectiveUrl);
        Record(report, "organization.url", "Organization.url",
            urlClass, effectiveUrl,
            urlClass == VodimClassification.Valid ? effectiveUrl : null, urlNote);
        if (org.Url is null && org.Website is not null)
            Record(report, "organization.website", "Organization.url",
                VodimClassification.Other, org.Website, org.Website,
                "Non-standard 'website' field used as fallback for 'url'.");

        // legal_status → additionalProperty[legalStatus]
        // (ORUK legal_status = type of legal entity e.g. "CIC"; Schema.org legalName = registered name)
        Record(report, "organization.legal_status",
            "Organization.additionalProperty[legalStatus]",
            Classify(org.LegalStatus), org.LegalStatus, org.LegalStatus,
            org.LegalStatus is not null
                ? "Maps to additionalProperty; legal_status is entity-type not legal name." : null);

        // logo → ImageObject
        var (logoUrlClass, logoUrlNote) = ClassifyUrl(org.Logo);
        Record(report, "organization.logo", "Organization.logo",
            logoUrlClass, org.Logo,
            logoUrlClass == VodimClassification.Valid ? org.Logo : null, logoUrlNote);

        // uri → sameAs
        var (uriClass, uriNote) = ClassifyUrl(org.Uri);
        Record(report, "organization.uri", "Organization.sameAs",
            uriClass, org.Uri,
            uriClass == VodimClassification.Valid ? org.Uri : null, uriNote);

        // year_incorporated → foundingDate
        Record(report, "organization.year_incorporated", "Organization.foundingDate",
            Classify(org.YearIncorporated), org.YearIncorporated, org.YearIncorporated);

        // parent_organization_id → parentOrganization @id reference
        Record(report, "organization.parent_organization_id",
            "Organization.parentOrganization",
            Classify(org.ParentOrganizationId),
            org.ParentOrganizationId,
            org.ParentOrganizationId is not null
                ? options.OrganisationUri(org.ParentOrganizationId) : null);

        // contacts → contactPoint
        var contactPoints = MapOrganizationContacts(org.Contacts, org.Phones, report);

        // additionalProperty
        var additionalProps = new List<SchemaOrgPropertyValue>();
        if (!string.IsNullOrWhiteSpace(org.LegalStatus))
            additionalProps.Add(new SchemaOrgPropertyValue
            {
                Name = "legalStatus",
                Value = org.LegalStatus
            });

        return new SchemaOrgOrganization
        {
            Id = orgUri,
            Name = string.IsNullOrWhiteSpace(org.Name) ? null : org.Name,
            AlternateName = org.AlternateName,
            Description = orgDescription,
            Email = emailClass == VodimClassification.Valid ? org.Email : null,
            Url = urlClass == VodimClassification.Valid ? effectiveUrl : null,
            SameAs = uriClass == VodimClassification.Valid ? org.Uri : null,
            Logo = logoUrlClass == VodimClassification.Valid
                ? new SchemaOrgImageObject { Url = org.Logo } : null,
            FoundingDate = org.YearIncorporated,
            ParentOrganization = org.ParentOrganizationId is not null
                ? new Dictionary<string, string>
                    { ["@id"] = options.OrganisationUri(org.ParentOrganizationId) }
                : null,
            ContactPoint = contactPoints.Count > 0 ? contactPoints : null,
            AdditionalProperty = additionalProps.Count > 0 ? additionalProps : null,
        };
    }

    // ── Location mapping ─────────────────────────────────────────────────────────

    private SchemaOrgPlace MapLocation(
        OrukLocation location, TransformationOptions options, TransformationReport report)
    {
        var locUri = options.LocationUri(location.Id);
        var (locIdClass, locIdNote) = ClassifyIdentifier(location.Id);
        Record(report, "location.id", "Place.@id", locIdClass, location.Id, locUri, locIdNote);

        Record(report, "location.name", "Place.name",
            Classify(location.Name), location.Name, location.Name);

        Record(report, "location.alternate_name", "Place.alternateName",
            Classify(location.AlternateName), location.AlternateName, location.AlternateName);

        // description — HTML stripped
        var locDescription = StripHtml(location.Description);
        Record(report, "location.description", "Place.description",
            Classify(location.Description), Truncate(location.Description),
            Truncate(locDescription), HtmlNote(location.Description));

        // url
        var (urlClass, urlNote) = ClassifyUrl(location.Url);
        Record(report, "location.url", "Place.url",
            urlClass, location.Url,
            urlClass == VodimClassification.Valid ? location.Url : null, urlNote);

        // transportation — no Schema.org mapping
        Record(report, "location.transportation", "—",
            Classify(location.Transportation) == VodimClassification.Missing
                ? VodimClassification.Missing : VodimClassification.Unmapped,
            location.Transportation, null, "No Schema.org mapping defined for transportation.");

        // latitude / longitude → GeoCoordinates
        var (geoClass, geoNote, geo) = MapGeoCoordinates(location.Latitude, location.Longitude);
        Record(report, "location.latitude+longitude", "Place.geo",
            geoClass, $"({location.Latitude},{location.Longitude})",
            geo is not null ? $"({geo.Latitude},{geo.Longitude})" : null, geoNote);

        // uprn → Place.identifier[UPRN]
        // Sourced from either external_identifiers[scheme=UPRN] or the scalar location.uprn;
        // MapUprnIdentifier reconciles the two sources and records VODIM for both source paths.
        var identifier = MapUprnIdentifier(location, report);

        // usrn → additionalProperty[usrn]
        Record(report, "location.usrn", "Place.additionalProperty[usrn]",
            Classify(location.Usrn), location.Usrn, location.Usrn);

        // location_type → additionalProperty[locationType]
        Record(report, "location.location_type", "Place.additionalProperty[locationType]",
            Classify(location.LocationType), location.LocationType, location.LocationType);

        // address (first physical address)
        var primaryAddress = location.PhysicalAddresses.FirstOrDefault()
            ?? location.PostalAddresses.FirstOrDefault();
        var postalAddress = primaryAddress is not null
            ? MapAddress(primaryAddress, report) : null;

        // accessibility → amenityFeature
        var amenityFeatures = MapAccessibility(location.Accessibility, report);

        // phones
        var phones = location.Phones
            .Where(p => !string.IsNullOrWhiteSpace(p.Number))
            .Select(p => p.Number!)
            .ToList();
        var telephone = phones.Count > 0 ? phones[0] : null;
        if (phones.Count > 0)
            Record(report, "location.phones", "Place.telephone",
                VodimClassification.Valid, phones[0], phones[0],
                phones.Count > 1 ? $"First of {phones.Count} phones used for telephone." : null);
        else
            Record(report, "location.phones", "Place.telephone",
                VodimClassification.Missing);

        // opening hours (location-level schedules)
        var locSchedules = MapSchedules(
            location.Schedules.ToList(), $"location[{location.Id}].schedules", report);

        // additionalProperty
        var additionalProps = BuildLocationAdditionalProperties(location);

        return new SchemaOrgPlace
        {
            Id = locUri,
            Name = location.Name,
            AlternateName = location.AlternateName,
            Description = locDescription,
            Url = urlClass == VodimClassification.Valid ? location.Url : null,
            Address = postalAddress,
            Geo = geoClass == VodimClassification.Valid ? geo : null,
            Telephone = telephone,
            AmenityFeature = amenityFeatures.Count > 0 ? amenityFeatures : null,
            OpeningHoursSpecification = locSchedules.Count > 0 ? locSchedules : null,
            Identifier = identifier,
            AdditionalProperty = additionalProps.Count > 0 ? additionalProps : null,
        };
    }

    // ── Address mapping ──────────────────────────────────────────────────────────

    private static SchemaOrgPostalAddress MapAddress(
        OrukAddress address, TransformationReport report)
    {
        var prefix = "location.physical_addresses";
        Record(report, $"{prefix}.address_1", "Place.address.streetAddress",
            Classify(address.Address1), address.Address1, address.Address1);
        Record(report, $"{prefix}.address_2", "Place.address.streetAddress",
            string.IsNullOrWhiteSpace(address.Address2)
                ? VodimClassification.Missing : VodimClassification.Other,
            address.Address2, address.Address2,
            address.Address2 is not null
                ? "address_2 concatenated with address_1 or omitted; no separate Schema.org property." : null);
        Record(report, $"{prefix}.city", "Place.address.addressLocality",
            Classify(address.City), address.City, address.City);
        Record(report, $"{prefix}.region", "Place.address.addressRegion",
            Classify(address.Region), address.Region, address.Region);
        Record(report, $"{prefix}.state_province", "Place.address.addressRegion",
            Classify(address.StateProvince), address.StateProvince, address.StateProvince,
            address.StateProvince is not null
                ? "Maps to same target as region; whichever is non-null is used." : null);
        Record(report, $"{prefix}.postal_code", "Place.address.postalCode",
            Classify(address.PostalCode), address.PostalCode, address.PostalCode);
        Record(report, $"{prefix}.country", "Place.address.addressCountry",
            Classify(address.Country), address.Country, address.Country);

        // Combine address lines
        var streetParts = new[] { address.Address1, address.Address2 }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var streetAddress = string.Join(", ", streetParts);

        return new SchemaOrgPostalAddress
        {
            StreetAddress = string.IsNullOrWhiteSpace(streetAddress) ? null : streetAddress,
            AddressLocality = address.City,
            AddressRegion = address.StateProvince ?? address.Region,
            PostalCode = address.PostalCode,
            AddressCountry = address.Country,
        };
    }

    // ── Schedule → OpeningHoursSpecification ─────────────────────────────────────

    private List<SchemaOrgOpeningHoursSpecification> MapSchedules(
        IReadOnlyList<OrukSchedule> schedules, string sourcePrefix,
        TransformationReport report)
    {
        if (schedules.Count == 0)
        {
            Record(report, sourcePrefix, "GovernmentService.openingHoursSpecification",
                VodimClassification.Missing);
            return [];
        }

        var result = new List<SchemaOrgOpeningHoursSpecification>();

        for (var i = 0; i < schedules.Count; i++)
        {
            var schedule = schedules[i];
            var prefix = $"{sourcePrefix}[{i}]";

            // opens_at / closes_at
            var (opensClass, opensNote) = ClassifyTime(schedule.OpensAt);
            Record(report, $"{prefix}.opens_at", "OpeningHoursSpecification.opens",
                opensClass, schedule.OpensAt,
                opensClass == VodimClassification.Valid ? schedule.OpensAt : null, opensNote);

            var (closesClass, closesNote) = ClassifyTime(schedule.ClosesAt);
            Record(report, $"{prefix}.closes_at", "OpeningHoursSpecification.closes",
                closesClass, schedule.ClosesAt,
                closesClass == VodimClassification.Valid ? schedule.ClosesAt : null, closesNote);

            // valid_from / valid_to
            var (vfClass, vfNote) = ClassifyDate(schedule.ValidFrom);
            Record(report, $"{prefix}.valid_from", "OpeningHoursSpecification.validFrom",
                vfClass, schedule.ValidFrom,
                vfClass == VodimClassification.Valid ? schedule.ValidFrom : null, vfNote);

            var (vtClass, vtNote) = ClassifyDate(schedule.ValidTo);
            Record(report, $"{prefix}.valid_to", "OpeningHoursSpecification.validThrough",
                vtClass, schedule.ValidTo,
                vtClass == VodimClassification.Valid ? schedule.ValidTo : null, vtNote);

            // description — HTML stripped
            var schedDescription = StripHtml(schedule.Description);
            Record(report, $"{prefix}.description", "OpeningHoursSpecification.description",
                Classify(schedule.Description),
                Truncate(schedule.Description), Truncate(schedDescription),
                HtmlNote(schedule.Description));

            // iCalendar fields that have no Schema.org equivalent
            RecordICalFields(schedule, prefix, report);

            // byday → expand into one OpeningHoursSpecification per day
            var expandedDays = ExpandByDay(schedule.ByDay, $"{prefix}.byday", report);

            if (expandedDays.Count > 0)
            {
                foreach (var (dayUri, _) in expandedDays)
                {
                    result.Add(new SchemaOrgOpeningHoursSpecification
                    {
                        DayOfWeek = dayUri,
                        Opens = opensClass == VodimClassification.Valid ? schedule.OpensAt : null,
                        Closes = closesClass == VodimClassification.Valid ? schedule.ClosesAt : null,
                        ValidFrom = vfClass == VodimClassification.Valid ? schedule.ValidFrom : null,
                        ValidThrough = vtClass == VodimClassification.Valid ? schedule.ValidTo : null,
                        Description = schedDescription,
                    });
                }
            }
            else
            {
                // No day info — emit a date-range-only spec if we have dates/times
                if (schedule.ValidFrom is not null || schedule.ValidTo is not null
                    || schedule.OpensAt is not null || schedule.ClosesAt is not null)
                {
                    result.Add(new SchemaOrgOpeningHoursSpecification
                    {
                        Opens = opensClass == VodimClassification.Valid ? schedule.OpensAt : null,
                        Closes = closesClass == VodimClassification.Valid ? schedule.ClosesAt : null,
                        ValidFrom = vfClass == VodimClassification.Valid ? schedule.ValidFrom : null,
                        ValidThrough = vtClass == VodimClassification.Valid ? schedule.ValidTo : null,
                        Description = schedDescription,
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Expands an RRULE BYDAY string (e.g. "MO,WE,FR") into a list of
    /// (Schema.org day URI, VODIM classification) pairs, recording one report entry
    /// per day token.
    /// </summary>
    private static List<(string DayUri, VodimClassification Class)> ExpandByDay(
        string? byDay, string sourcePath, TransformationReport report)
    {
        if (string.IsNullOrWhiteSpace(byDay))
        {
            Record(report, sourcePath, "OpeningHoursSpecification.dayOfWeek",
                VodimClassification.Missing);
            return [];
        }

        var tokens = byDay.Split(',', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        var result = new List<(string, VodimClassification)>();

        foreach (var token in tokens)
        {
            // Strip leading ordinal digits/signs (e.g. "2MO" → "MO", "-1FR" → "FR")
            var stripped = System.Text.RegularExpressions.Regex.Replace(token, @"^[-+]?\d*", "");

            if (RruleDayToSchemaOrg.TryGetValue(stripped, out var schemaUri))
            {
                Record(report, sourcePath, "OpeningHoursSpecification.dayOfWeek",
                    VodimClassification.Valid, token, schemaUri,
                    stripped != token ? $"Ordinal prefix stripped from '{token}'; day-of-month detail lost." : null);
                result.Add((schemaUri, VodimClassification.Valid));
            }
            else if (LongDayToSchemaOrg.TryGetValue(stripped, out var longSchemaUri))
            {
                Record(report, sourcePath, "OpeningHoursSpecification.dayOfWeek",
                    VodimClassification.Other, token, longSchemaUri,
                    $"Long-form day name '{token}' used instead of RRULE short-form.");
                result.Add((longSchemaUri, VodimClassification.Other));
            }
            else
            {
                Record(report, sourcePath, "OpeningHoursSpecification.dayOfWeek",
                    VodimClassification.Invalid, token, null,
                    $"Unrecognised day-of-week token '{token}'. Field omitted.");
            }
        }

        return result;
    }

    private static void RecordICalFields(
        OrukSchedule schedule, string prefix, TransformationReport report)
    {
        // These iCalendar fields have no direct Schema.org OpeningHoursSpecification mapping.
        // They are recorded as Other (present, no target) to preserve provenance.
        RecordICalField(report, $"{prefix}.freq", schedule.Freq);
        RecordICalField(report, $"{prefix}.interval", schedule.Interval?.ToString());
        RecordICalField(report, $"{prefix}.count", schedule.Count?.ToString());
        RecordICalField(report, $"{prefix}.until", schedule.Until);
        RecordICalField(report, $"{prefix}.wkst", schedule.Wkst);
        RecordICalField(report, $"{prefix}.bymonthday", schedule.ByMonthDay);
        RecordICalField(report, $"{prefix}.dtstart", schedule.DtStart);
        RecordICalField(report, $"{prefix}.timezone", schedule.Timezone);
        RecordICalField(report, $"{prefix}.schedule_link", schedule.ScheduleLink,
            "No Schema.org mapping. Could be surfaced as additionalProperty if needed.");
        RecordICalField(report, $"{prefix}.attending_type", schedule.AttendingType,
            "No Schema.org mapping. Could be surfaced as additionalProperty if needed.");
        RecordICalField(report, $"{prefix}.notes", schedule.Notes,
            "No Schema.org mapping for schedule notes.");
    }

    private static void RecordICalField(
        TransformationReport report, string sourcePath, string? value, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        Record(report, sourcePath, "—",
            VodimClassification.Unmapped, value, null,
            note ?? "iCalendar recurrence field — no Schema.org OpeningHoursSpecification equivalent.");
    }

    // ── Contacts + Phones → ContactPoint ─────────────────────────────────────────

    private List<SchemaOrgContactPoint> MapServiceContacts(
        ICollection<OrukContact> contacts,
        ICollection<OrukPhone> directPhones,
        TransformationReport report)
    {
        var result = new List<SchemaOrgContactPoint>();

        // Direct service-level phones (no contact entity)
        foreach (var phone in directPhones.Where(p => !string.IsNullOrWhiteSpace(p.Number)))
        {
            Record(report, "service.phones[].number", "GovernmentService.contactPoint.telephone",
                VodimClassification.Valid, phone.Number, phone.Number);
            result.Add(new SchemaOrgContactPoint
            {
                ContactType = "enquiries",
                Telephone = phone.Number,
            });
        }
        if (!directPhones.Any())
            Record(report, "service.phones", "GovernmentService.contactPoint.telephone",
                VodimClassification.Missing);

        // Contacts (each maps to a ContactPoint)
        foreach (var contact in contacts)
        {
            var cp = MapContact(contact, "service.contacts", report);
            if (cp is not null) result.Add(cp);
        }
        if (!contacts.Any())
            Record(report, "service.contacts", "GovernmentService.contactPoint",
                VodimClassification.Missing);

        return result;
    }

    private List<SchemaOrgContactPoint> MapOrganizationContacts(
        ICollection<OrukContact> contacts,
        ICollection<OrukPhone> directPhones,
        TransformationReport report)
    {
        var result = new List<SchemaOrgContactPoint>();

        foreach (var phone in directPhones.Where(p => !string.IsNullOrWhiteSpace(p.Number)))
        {
            Record(report, "organization.phones[].number", "Organization.contactPoint.telephone",
                VodimClassification.Valid, phone.Number, phone.Number);
            result.Add(new SchemaOrgContactPoint
            {
                ContactType = "enquiries",
                Telephone = phone.Number,
            });
        }

        foreach (var contact in contacts)
        {
            var cp = MapContact(contact, "organization.contacts", report);
            if (cp is not null) result.Add(cp);
        }

        return result;
    }

    private static SchemaOrgContactPoint? MapContact(
        OrukContact contact, string sourcePrefix, TransformationReport report)
    {
        var hasContent = !string.IsNullOrWhiteSpace(contact.Name)
            || !string.IsNullOrWhiteSpace(contact.Email)
            || !string.IsNullOrWhiteSpace(contact.Url)
            || !string.IsNullOrWhiteSpace(contact.Title)
            || !string.IsNullOrWhiteSpace(contact.Department)
            || contact.Phones.Any(p => !string.IsNullOrWhiteSpace(p.Number));

        if (!hasContent)
        {
            Record(report, $"{sourcePrefix}[{contact.Id}]", "ContactPoint",
                VodimClassification.Missing, null, null,
                "Contact has no mappable content.");
            return null;
        }

        // name → contactType (role/purpose label) — HTML stripped
        var rawContactType = contact.Name ?? contact.Department;
        var contactType = StripHtml(rawContactType);
        Record(report, $"{sourcePrefix}[{contact.Id}].name", "ContactPoint.contactType",
            Classify(rawContactType), rawContactType, contactType,
            HtmlNote(rawContactType));

        // title → name (person title) — HTML stripped
        var contactTitle = StripHtml(contact.Title);
        Record(report, $"{sourcePrefix}[{contact.Id}].title", "ContactPoint.name",
            Classify(contact.Title), contact.Title, contactTitle,
            HtmlNote(contact.Title));

        // email
        var (emailClass, emailNote) = ClassifyEmail(contact.Email);
        Record(report, $"{sourcePrefix}[{contact.Id}].email", "ContactPoint.email",
            emailClass, contact.Email,
            emailClass == VodimClassification.Valid ? contact.Email : null, emailNote);

        // url
        var (urlClass, urlNote) = ClassifyUrl(contact.Url);
        Record(report, $"{sourcePrefix}[{contact.Id}].url", "ContactPoint.url",
            urlClass, contact.Url,
            urlClass == VodimClassification.Valid ? contact.Url : null, urlNote);

        // phones
        var firstPhone = contact.Phones.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Number));
        if (firstPhone is not null)
            Record(report, $"{sourcePrefix}[{contact.Id}].phones[0].number",
                "ContactPoint.telephone", VodimClassification.Valid,
                firstPhone.Number, firstPhone.Number);

        // languages on contact
        var langs = MapLanguages(contact.Languages, $"{sourcePrefix}[{contact.Id}].languages", report);

        return new SchemaOrgContactPoint
        {
            ContactType = contactType,
            Name = contactTitle,
            Email = emailClass == VodimClassification.Valid ? contact.Email : null,
            Url = urlClass == VodimClassification.Valid ? contact.Url : null,
            Telephone = firstPhone?.Number,
            AvailableLanguage = langs.Count > 0 ? langs : null,
        };
    }

    // ── Languages ────────────────────────────────────────────────────────────────

    private static List<SchemaOrgLanguage> MapLanguages(
        ICollection<OrukLanguage> languages, string sourcePrefix, TransformationReport report)
    {
        if (!languages.Any())
            return [];

        var result = new List<SchemaOrgLanguage>();
        foreach (var lang in languages)
        {
            Record(report, $"{sourcePrefix}[{lang.Id}].name", "Language.name",
                Classify(lang.Name), lang.Name, lang.Name);
            
            var (codeClass, codeNote) = ClassifyLanguageCode(lang.Code);
            Record(report, $"{sourcePrefix}[{lang.Id}].code", "Language.identifier",
                codeClass, lang.Code, codeClass == VodimClassification.Valid ? lang.Code : null, codeNote);

            if (codeClass == VodimClassification.Valid)
            {
                result.Add(new SchemaOrgLanguage
                {
                    Name = lang.Name,
                    Identifier = lang.Code,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Maps languages with fallback to interpretation_services free-text when no structured languages exist.
    /// Records the VODIM entry for interpretation_services here — after we know whether the structured
    /// languages collection produced usable output — so the classification accurately reflects what
    /// actually reached the output document.
    /// </summary>
    private static List<SchemaOrgLanguage> MapLanguagesWithFallback(
        ICollection<OrukLanguage> languages,
        string? interpretationServices,
        string sourcePrefix,
        TransformationReport report)
    {
        var result = MapLanguages(languages, sourcePrefix, report);

        if (string.IsNullOrWhiteSpace(interpretationServices))
        {
            // Field absent — nothing to fall back to.
            Record(report, "service.interpretation_services", "GovernmentService.availableLanguage",
                VodimClassification.Missing, interpretationServices, null);
        }
        else if (result.Count > 0)
        {
            // Structured languages produced valid output; interpretation_services is not used.
            Record(report, "service.interpretation_services", "GovernmentService.availableLanguage",
                VodimClassification.Other, interpretationServices, null,
                "Superseded by service.languages collection; value not used in output.");
        }
        else
        {
            // No structured language output — attempt free-text fallback.
            var stripped = StripHtml(interpretationServices);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                result.Add(new SchemaOrgLanguage { Name = stripped });
                Record(report, "service.interpretation_services", "GovernmentService.availableLanguage",
                    VodimClassification.Other, interpretationServices, stripped,
                    string.Join(" ", new[]
                    {
                        "Mapped as free-text Language name. Prefer service.languages collection.",
                        HtmlNote(interpretationServices)
                    }.Where(n => n is not null)));
            }
            else
            {
                // Value was present but HTML stripping produced an empty string — nothing emitted.
                Record(report, "service.interpretation_services", "GovernmentService.availableLanguage",
                    VodimClassification.Invalid, interpretationServices, null,
                    "HTML markup stripped to empty string; no language name could be extracted.");
            }
        }

        return result;
    }

    // ── CostOption → Offer ───────────────────────────────────────────────────────

    private List<SchemaOrgOffer> MapCostOptions(
        ICollection<OrukCostOption> costOptions,
        string? feesDescriptionFallback,
        TransformationOptions options,
        TransformationReport report)
    {
        if (!costOptions.Any())
        {
            Record(report, "service.cost_options", "GovernmentService.offers",
                VodimClassification.Missing,
                note: feesDescriptionFallback is not null
                    ? "fees_description present but no structured cost_options." : null);
            return [];
        }

        var result = new List<SchemaOrgOffer>();

        for (var i = 0; i < costOptions.Count; i++)
        {
            var co = costOptions.ElementAt(i);
            var prefix = $"service.cost_options[{i}]";

            // amount / option → price
            decimal price;
            VodimClassification priceClass;
            string? priceNote = null;
            string? priceSource;

            if (string.Equals(co.Option, "free", StringComparison.OrdinalIgnoreCase))
            {
                price = 0m;
                priceClass = VodimClassification.Valid;
                priceSource = "free";
                priceNote = "option='free' normalised to price=0.";
            }
            else if (co.Amount.HasValue)
            {
                price = co.Amount.Value;
                priceClass = co.Amount >= 0
                    ? VodimClassification.Valid
                    : VodimClassification.Invalid;
                priceSource = co.Amount.Value.ToString();
                if (priceClass == VodimClassification.Invalid)
                    priceNote = "Negative amount is not valid for Schema.org Offer.price.";
            }
            else
            {
                price = 0m;
                priceClass = VodimClassification.Default;
                priceSource = null;
                priceNote = "No amount or free option; defaulted to price=0.";
            }
            Record(report, $"{prefix}.amount", "Offer.price", priceClass,
                priceSource, price.ToString(), priceNote);

            // currency → priceCurrency
            string currency;
            VodimClassification currencyClass;
            if (string.IsNullOrWhiteSpace(co.Currency))
            {
                currency = options.DefaultCurrency;
                currencyClass = VodimClassification.Default;
                Record(report, $"{prefix}.currency", "Offer.priceCurrency",
                    currencyClass, null, currency,
                    $"Currency absent; defaulted to '{options.DefaultCurrency}'.");
            }
            else
            {
                var (cc, cn) = ClassifyIso4217(co.Currency);
                currency = cc == VodimClassification.Valid ? co.Currency : options.DefaultCurrency;
                currencyClass = cc;
                if (cc != VodimClassification.Valid)
                    Record(report, $"{prefix}.currency", "Offer.priceCurrency",
                        cc, co.Currency, currency, cn);
                else
                    Record(report, $"{prefix}.currency", "Offer.priceCurrency",
                        cc, co.Currency, co.Currency);
            }

            // amount_description → description — HTML stripped
            var amountDescription = StripHtml(co.AmountDescription);
            Record(report, $"{prefix}.amount_description", "Offer.description",
                Classify(co.AmountDescription), co.AmountDescription, amountDescription,
                HtmlNote(co.AmountDescription));

            // option (free-text label) — HTML stripped
            var optionLabel = StripHtml(co.Option);
            var optionNote = co.Option is not null
                && !string.Equals(co.Option, "free", StringComparison.OrdinalIgnoreCase)
                    ? string.Join(" ", new[]
                      {
                          "Free-text cost option label; not a controlled vocabulary.",
                          HtmlNote(co.Option)
                      }.Where(n => n is not null))
                    : null;
            Record(report, $"{prefix}.option", "Offer.description",
                Classify(co.Option), co.Option, optionLabel,
                string.IsNullOrWhiteSpace(optionNote) ? null : optionNote);

            // valid_to → priceValidUntil
            var (vtClass, vtNote) = ClassifyDate(co.ValidTo);
            Record(report, $"{prefix}.valid_to", "Offer.priceValidUntil",
                vtClass, co.ValidTo,
                vtClass == VodimClassification.Valid ? co.ValidTo : null, vtNote);

            // valid_from → validFrom
            var (vfClass, vfNote) = ClassifyDate(co.ValidFrom);
            Record(report, $"{prefix}.valid_from", "Offer.validFrom",
                vfClass, co.ValidFrom,
                vfClass == VodimClassification.Valid ? co.ValidFrom : null, vfNote);

            if (priceClass != VodimClassification.Invalid)
            {
                result.Add(new SchemaOrgOffer
                {
                    Price = price,
                    PriceCurrency = currency,
                    Description = amountDescription ?? optionLabel,
                    PriceValidUntil = vtClass == VodimClassification.Valid ? co.ValidTo : null,
                    ValidFrom = vfClass == VodimClassification.Valid ? co.ValidFrom : null,
                });
            }
        }

        return result;
    }

    // ── Eligibility → Audience ───────────────────────────────────────────────────

    private static SchemaOrgAudience? MapAudience(
        ICollection<OrukEligibility> eligibilities,
        string? eligibilityDescription,
        double? serviceMinAge,
        double? serviceMaxAge,
        TransformationReport report)
    {
        // eligibilityDescription is already HTML-stripped by the caller (MapService)
        var hasDescription = !string.IsNullOrWhiteSpace(eligibilityDescription);
        var (minClass, _, minVal) = ClassifyAge(serviceMinAge, "minimum_age");
        var (maxClass, _, maxVal) = ClassifyAge(serviceMaxAge, "maximum_age");
        var hasAgeConstraint = minVal.HasValue || maxVal.HasValue;

        // Use structured eligibility if present
        if (eligibilities.Any())
        {
            var first = eligibilities.First();
            var (eMinClass, _, eMinVal) = ClassifyAge(first.MinimumAge, "eligibility.minimum_age");
            var (eMaxClass, _, eMaxVal) = ClassifyAge(first.MaximumAge, "eligibility.maximum_age");

            // eligibility description — HTML stripped
            var eligDesc = StripHtml(first.Description);
            Record(report, "service.eligibility[0].description",
                "GovernmentService.audience.description",
                Classify(first.Description), first.Description, eligDesc,
                HtmlNote(first.Description));
            Record(report, "service.eligibility[0].minimum_age",
                "GovernmentService.audience.suggestedMinAge",
                eMinClass, first.MinimumAge?.ToString(), eMinVal?.ToString());
            Record(report, "service.eligibility[0].maximum_age",
                "GovernmentService.audience.suggestedMaxAge",
                eMaxClass, first.MaximumAge?.ToString(), eMaxVal?.ToString());

            if (eMinVal.HasValue || eMaxVal.HasValue)
            {
                return new SchemaOrgPeopleAudience
                {
                    Description = eligDesc ?? eligibilityDescription,
                    SuggestedMinAge = eMinVal,
                    SuggestedMaxAge = eMaxVal,
                };
            }

            return new SchemaOrgAudience
            {
                Description = eligDesc ?? eligibilityDescription,
            };
        }

        // Fall back to service-level age fields
        if (!hasDescription && !hasAgeConstraint) return null;

        if (hasAgeConstraint)
        {
            return new SchemaOrgPeopleAudience
            {
                Description = eligibilityDescription,
                SuggestedMinAge = minVal,
                SuggestedMaxAge = maxVal,
            };
        }

        return new SchemaOrgAudience { Description = eligibilityDescription };
    }

    // ── ServiceArea → areaServed ─────────────────────────────────────────────────

    private static List<object> MapServiceAreas(
        ICollection<OrukServiceArea> areas, TransformationOptions options, TransformationReport report)
    {
        if (!areas.Any())
        {
            Record(report, "service.service_areas", "GovernmentService.areaServed",
                VodimClassification.Missing);
            return [];
        }

        var result = new List<object>();
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas.ElementAt(i);
            var prefix = $"service.service_areas[{i}]";

            Record(report, $"{prefix}.name", "AdministrativeArea.name",
                Classify(area.Name), area.Name, area.Name);

            Record(report, $"{prefix}.extent", "AdministrativeArea.identifier",
                Classify(area.Extent), area.Extent, area.Extent,
                area.Extent is not null ? "ONS geographic code mapped as identifier." : null);

            Record(report, $"{prefix}.extent_type", "AdministrativeArea.additionalProperty[extentType]",
                Classify(area.ExtentType), area.ExtentType, area.ExtentType);

            var (uriClass, _) = ClassifyUrl(area.Uri);
            Record(report, $"{prefix}.uri", "AdministrativeArea.sameAs",
                uriClass, area.Uri,
                uriClass == VodimClassification.Valid ? area.Uri : null);

            var areaAdditionalProps = new List<SchemaOrgPropertyValue>();
            if (!string.IsNullOrWhiteSpace(area.ExtentType))
                areaAdditionalProps.Add(new SchemaOrgPropertyValue
                {
                    Name = "extentType",
                    Value = area.ExtentType
                });

            SchemaOrgPropertyValue? areaIdentifier = null;
            if (!string.IsNullOrWhiteSpace(area.Extent))
                areaIdentifier = new SchemaOrgPropertyValue
                {
                    Name = "extent",
                    Value = area.Extent,
                    PropertyId = area.ExtentType,
                };

            result.Add(new SchemaOrgAdministrativeArea
            {
                Name = area.Name,
                SameAs = uriClass == VodimClassification.Valid ? area.Uri : null,
                Identifier = areaIdentifier,
                AdditionalProperty = areaAdditionalProps.Count > 0 ? areaAdditionalProps : null,
            });
        }

        return result;
    }

    // ── Attributes / TaxonomyTerms → additionalType + keywords ───────────────────

    private static (List<object> AdditionalTypes, List<string> Keywords) MapAttributes(
        ICollection<OrukAttribute> attributes, string sourcePrefix, TransformationReport report)
    {
        var additionalTypes = new List<object>();
        var keywords = new List<string>();

        if (!attributes.Any())
        {
            Record(report, $"{sourcePrefix}.attributes", "Thing.additionalType",
                VodimClassification.Missing);
            return (additionalTypes, keywords);
        }

        for (var i = 0; i < attributes.Count; i++)
        {
            var attr = attributes.ElementAt(i);
            var term = attr.TaxonomyTerm;
            if (term is null) continue;

            var termPrefix = $"{sourcePrefix}.attributes[{i}].taxonomy_term";

            // Always add name to keywords
            if (!string.IsNullOrWhiteSpace(term.Name))
            {
                keywords.Add(term.Name);
                Record(report, $"{termPrefix}.name", "Thing.keywords",
                    VodimClassification.Valid, term.Name, term.Name);
            }
            else
            {
                Record(report, $"{termPrefix}.name", "Thing.keywords",
                    VodimClassification.Missing);
            }

            // Priority 1: term_uri is present
            if (!string.IsNullOrWhiteSpace(term.TermUri))
            {
                Record(report, $"{termPrefix}.term_uri", "Thing.additionalType",
                    VodimClassification.Valid, term.TermUri, term.TermUri);

                additionalTypes.Add(new SchemaOrgDefinedTerm
                {
                    Id = term.TermUri,
                    Name = term.Name,
                    InDefinedTermSet = ExtractBaseUri(term.TermUri),
                    TermCode = term.Code,
                });
                continue;
            }

            // Priority 2: code + known vocabulary → construct URI
            if (!string.IsNullOrWhiteSpace(term.Code)
                && !string.IsNullOrWhiteSpace(term.Taxonomy)
                && KnownTaxonomyPrefixes.Contains(term.Taxonomy))
            {
                var constructed = ConstructTaxonomyUri(term.Taxonomy, term.Code);
                if (constructed is not null)
                {
                    Record(report, $"{termPrefix}.code+taxonomy", "Thing.additionalType",
                        VodimClassification.Valid, $"{term.Taxonomy}:{term.Code}", constructed,
                        "URI constructed from taxonomy registry.");
                    additionalTypes.Add(new SchemaOrgDefinedTerm
                    {
                        Id = constructed,
                        Name = term.Name,
                        InDefinedTermSet = ExtractBaseUri(constructed),
                        TermCode = term.Code,
                    });
                    continue;
                }
            }

            // Priority 3: keywords only
            Record(report, $"{termPrefix}.code", "Thing.keywords",
                string.IsNullOrWhiteSpace(term.Code)
                    ? VodimClassification.Missing : VodimClassification.Other,
                term.Code, null,
                "No resolvable URI; term name added to keywords only.");
        }

        return (additionalTypes, keywords);
    }

    private static string? ConstructTaxonomyUri(string taxonomy, string code)
    {
        return taxonomy.ToLowerInvariant() switch
        {
            "esdstandards" or "esd standards"
                => $"https://standards.esd.org.uk/?uri=esd%3Aservice%2F{Uri.EscapeDataString(code)}",
            "snomed" or "snomed ct"
                => $"http://snomed.info/sct/{Uri.EscapeDataString(code)}",
            "loinc"
                => $"http://loinc.org/{Uri.EscapeDataString(code)}",
            "icd-10"
                => $"http://hl7.org/fhir/sid/icd-10/{Uri.EscapeDataString(code)}",
            _ => null
        };
    }

    private static string? ExtractBaseUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        return $"{parsed.Scheme}://{parsed.Host}/";
    }

    // ── Accessibility → LocationFeatureSpecification ──────────────────────────────

    private static List<SchemaOrgLocationFeatureSpecification> MapAccessibility(
        ICollection<OrukAccessibility> items, TransformationReport report)
    {
        if (!items.Any()) return [];

        var result = new List<SchemaOrgLocationFeatureSpecification>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items.ElementAt(i);
            var prefix = $"location.accessibility[{i}]";

            // description — HTML stripped
            var accessDesc = StripHtml(item.Description);
            Record(report, $"{prefix}.description",
                "Place.amenityFeature.name",
                Classify(item.Description), item.Description, accessDesc,
                HtmlNote(item.Description));

            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                result.Add(new SchemaOrgLocationFeatureSpecification
                {
                    Name = accessDesc ?? item.Description,
                    Value = (object?)item.Details ?? true,
                });
            }
        }
        return result;
    }

    // ── UPRN → Place.identifier ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves the single UPRN <c>Place.identifier</c> from the two possible ORUK sources —
    /// the structured <c>external_identifiers[scheme=UPRN]</c> entry and the scalar
    /// <c>location.uprn</c> field — recording an accurate VODIM entry for <em>each</em>
    /// source path so that neither over-reports what actually reached the output.
    ///
    /// <para>
    /// <b>Design decision — single identifier slot.</b>
    /// <see cref="SchemaOrgThing.Identifier"/> is a single <see cref="SchemaOrgPropertyValue"/>.
    /// Both sources here describe the <em>same</em> scheme (UPRN), so they compete for the same
    /// slot rather than needing to coexist; a list (or an <c>additionalProperty</c> overflow) is
    /// therefore unnecessary. When both sources carry a value the structured
    /// <c>external_identifiers[UPRN]</c> entry wins and the scalar is recorded as
    /// <see cref="VodimClassification.Other"/> (present but superseded, no mapped value) — mirroring
    /// how <c>interpretation_services</c> is reported when superseded by the structured
    /// <c>languages</c> collection. Whichever source is actually emitted is the only one classified
    /// <see cref="VodimClassification.Valid"/>, so VODIM never double-counts one emitted identifier.
    /// </para>
    /// </summary>
    private static SchemaOrgPropertyValue? MapUprnIdentifier(
        OrukLocation location, TransformationReport report)
    {
        const string target = "Place.identifier[UPRN]";

        var externalUprn = location.ExternalIdentifiers.FirstOrDefault(x =>
            string.Equals(x.IdentifierScheme, "UPRN", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(x.Identifier));

        var scalarUprn = string.IsNullOrWhiteSpace(location.Uprn) ? null : location.Uprn.Trim();

        // Preference order: external_identifiers[UPRN] > scalar location.uprn.
        if (externalUprn is not null)
        {
            var value = externalUprn.Identifier!.Trim();

            // external_identifiers is the emitted source → Valid.
            Record(report, "location.external_identifiers", target,
                VodimClassification.Valid, value, value);

            // Scalar is either absent (Missing) or present-but-superseded (Other, not emitted).
            if (scalarUprn is null)
                Record(report, "location.uprn", target, VodimClassification.Missing);
            else
                Record(report, "location.uprn", target,
                    VodimClassification.Other, location.Uprn, null,
                    "Superseded by external_identifiers[UPRN]; value not used in output.");

            return new SchemaOrgPropertyValue
            {
                Name = "UPRN",
                Value = value,
                PropertyId = "UPRN",
            };
        }

        if (scalarUprn is not null)
        {
            // Scalar is the emitted source → Valid; no structured identifier present.
            Record(report, "location.uprn", target,
                VodimClassification.Valid, location.Uprn, scalarUprn);
            Record(report, "location.external_identifiers", target,
                VodimClassification.Missing, null, null, "No UPRN external identifier found.");

            return new SchemaOrgPropertyValue
            {
                Name = "UPRN",
                Value = scalarUprn,
                PropertyId = "UPRN",
            };
        }

        // Neither source carries a UPRN — nothing emitted.
        Record(report, "location.uprn", target, VodimClassification.Missing);
        Record(report, "location.external_identifiers", target,
            VodimClassification.Missing, null, null, "No UPRN external identifier found.");
        return null;
    }

    // ── AdditionalProperty builders ───────────────────────────────────────────────

    private static List<SchemaOrgPropertyValue> BuildServiceAdditionalProperties(
        OrukService service, VodimClassification dateModClass, string? lastModified)
    {
        var props = new List<SchemaOrgPropertyValue>();
        if (!string.IsNullOrWhiteSpace(service.Status))
            props.Add(new SchemaOrgPropertyValue { Name = "orukStatus", Value = service.Status });
        if (!string.IsNullOrWhiteSpace(service.AssuredDate))
            props.Add(new SchemaOrgPropertyValue { Name = "assuredDate", Value = service.AssuredDate });
        if (!string.IsNullOrWhiteSpace(service.AssurerEmail))
            props.Add(new SchemaOrgPropertyValue { Name = "assuredBy", Value = service.AssurerEmail });
        if (!string.IsNullOrWhiteSpace(service.WaitTime))
            props.Add(new SchemaOrgPropertyValue { Name = "waitTime", Value = service.WaitTime });
        if (dateModClass == VodimClassification.Valid && !string.IsNullOrWhiteSpace(lastModified))
            props.Add(new SchemaOrgPropertyValue { Name = "dateModified", Value = lastModified });
        return props;
    }

    private static List<SchemaOrgPropertyValue> BuildLocationAdditionalProperties(OrukLocation location)
    {
        var props = new List<SchemaOrgPropertyValue>();
        if (!string.IsNullOrWhiteSpace(location.Usrn))
            props.Add(new SchemaOrgPropertyValue { Name = "usrn", Value = location.Usrn });
        if (!string.IsNullOrWhiteSpace(location.LocationType))
            props.Add(new SchemaOrgPropertyValue { Name = "locationType", Value = location.LocationType });
        return props;
    }

    // ── GeoCoordinates validation ─────────────────────────────────────────────────

    private static (VodimClassification Class, string? Note, SchemaOrgGeoCoordinates? Geo)
        MapGeoCoordinates(double? latitude, double? longitude)
    {
        if (latitude is null && longitude is null)
            return (VodimClassification.Missing, null, null);

        if (latitude is null || longitude is null)
            return (VodimClassification.Invalid,
                "Only one of latitude/longitude is present; GeoCoordinates requires both.", null);

        if (latitude < -90 || latitude > 90)
            return (VodimClassification.Invalid,
                $"Latitude {latitude} is outside the valid range −90 to 90.", null);

        if (longitude < -180 || longitude > 180)
            return (VodimClassification.Invalid,
                $"Longitude {longitude} is outside the valid range −180 to 180.", null);

        return (VodimClassification.Valid, null,
            new SchemaOrgGeoCoordinates
            {
                Latitude = (decimal)latitude.Value,
                Longitude = (decimal)longitude.Value,
            });
    }

    // ── HTML stripping ────────────────────────────────────────────────────────────

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]+>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HtmlTagPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s{2,}")]
    private static partial System.Text.RegularExpressions.Regex MultipleWhitespacePattern();

    /// <summary>
    /// Removes HTML tags and decodes HTML entities from a free-text value,
    /// then normalises internal whitespace.  Returns the original value unchanged
    /// when no HTML is present; returns <c>null</c> when the stripped result is empty.
    /// </summary>
    private static string? StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (!HtmlTagPattern().IsMatch(value)) return value;

        var tagsRemoved = HtmlTagPattern().Replace(value, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(tagsRemoved);
        var normalized = MultipleWhitespacePattern().Replace(decoded, " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Returns <c>"HTML markup stripped; plain text emitted."</c> when the value
    /// contained HTML tags, otherwise <c>null</c>.  Used to populate VODIM notes.
    /// </summary>
    private static string? HtmlNote(string? value) =>
        value is not null && HtmlTagPattern().IsMatch(value)
            ? "HTML markup stripped; plain text emitted."
            : null;

    // ── Field-level validators ────────────────────────────────────────────────────

    private static VodimClassification Classify(string? value) =>
        string.IsNullOrWhiteSpace(value) ? VodimClassification.Missing : VodimClassification.Valid;

    private static (VodimClassification Class, string? Note) ClassifyUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return (VodimClassification.Valid, null);
        return (VodimClassification.Invalid,
            $"Value '{Truncate(value, 80)}' is not a valid absolute HTTP/HTTPS URL.");
    }

    private static (VodimClassification Class, string? Note) ClassifyEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        try
        {
            _ = new System.Net.Mail.MailAddress(value);
            return (VodimClassification.Valid, null);
        }
        catch
        {
            return (VodimClassification.Invalid,
                $"Value '{Truncate(value, 80)}' is not a valid email address.");
        }
    }

    private static (VodimClassification Class, string? Note) ClassifyDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        if (DateTimeOffset.TryParse(value, out _) || DateOnly.TryParse(value, out _))
            return (VodimClassification.Valid, null);
        return (VodimClassification.Invalid,
            $"Value '{Truncate(value, 40)}' is not a parseable ISO 8601 date.");
    }

    /// <summary>
    /// Validates language code as BCP-47 or ISO 639-1 format.
    /// Accepts: 2-3 letter codes (en, eng), with optional script (en-Latn), region (en-US), or variant.
    /// Pattern: ^[a-z]{2,3}(-[A-Z][a-z]{3})?(-[A-Z]{2}|\d{3})?$
    /// </summary>
    private static (VodimClassification Class, string? Note) ClassifyLanguageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        
        const string languageCodePattern = @"^[a-z]{2,3}(-[A-Z][a-z]{3})?(-[A-Z]{2}|\d{3})?$";
        var regex = new System.Text.RegularExpressions.Regex(languageCodePattern);
        
        if (regex.IsMatch(value))
            return (VodimClassification.Valid, null);
        return (VodimClassification.Invalid,
            $"Language code '{Truncate(value, 20)}' is not valid BCP-47 (expected format: en, en-US, zh-Hans, etc.).");
    }

    private static (VodimClassification Class, string? Note) ClassifyTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        if (TimeOnly.TryParse(value, out _)) return (VodimClassification.Valid, null);
        return (VodimClassification.Invalid,
            $"Value '{Truncate(value, 40)}' is not a parseable ISO 8601 time.");
    }

    private static (VodimClassification Class, string? Note) ClassifyOrukStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        if (ValidOrukStatuses.Contains(value)) return (VodimClassification.Valid, null);
        return (VodimClassification.Other,
            $"'{value}' is not in the ORUK status vocabulary " +
            $"(active|inactive|defunct|temporarily closed).");
    }

    private static (VodimClassification Class, string? Note) ClassifyIso4217(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (VodimClassification.Missing, null);
        // ISO 4217 requires exactly 3 uppercase ASCII letters.
        // Full list validation is omitted deliberately: we accept any structurally-valid
        // code rather than maintain a static list of ~150 codes that can change.
        // A structurally invalid value (wrong length or non-letter characters) is Invalid;
        // an unknown-but-structurally-valid code is treated as Valid to be receive-liberal.
        if (value.Length == 3 && value.All(char.IsAsciiLetterUpper))
            return (VodimClassification.Valid, null);
        return (VodimClassification.Invalid,
            $"'{value}' does not appear to be a valid ISO 4217 3-letter currency code.");
    }

    /// <summary>
    /// Detects a string whose content is actually a JSON object or array — the shape the
    /// tolerant deserializer (<c>TolerantStringConverter</c>) produces when a feed supplies
    /// structured JSON for a field ORUK defines as a string.
    /// </summary>
    private static bool ArrivedAsJsonStructure(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var span = value.AsSpan().TrimStart();
        if (span.IsEmpty || (span[0] != '{' && span[0] != '[')) return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (VodimClassification Class, string? Note, double? Value)
        ClassifyAge(double? value, string fieldName)
    {
        if (value is null) return (VodimClassification.Missing, null, null);
        if (value == -1)
            return (VodimClassification.Other,
                $"{fieldName}=-1 is a common sentinel for 'no constraint' but is not standard ORUK.",
                null);
        if (value < 0)
            return (VodimClassification.Invalid,
                $"{fieldName}={value} is negative. Schema.org suggestedMinAge/MaxAge must be ≥ 0.",
                null);
        if (value > MaxPlausibleHumanAge)
            return (VodimClassification.Invalid,
                $"{fieldName}={value} exceeds plausible maximum human age ({MaxPlausibleHumanAge}).",
                null);
        return (VodimClassification.Valid, null, value);
    }

    // ── Required-field helper ─────────────────────────────────────────────────────

    private static string? MapRequiredString(
        TransformationReport report, string sourcePath, string targetPath, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Record(report, sourcePath, targetPath, VodimClassification.Missing, null, null,
                $"Required field '{sourcePath}' is absent.");
            return null;
        }
        Record(report, sourcePath, targetPath, VodimClassification.Valid, value, value);
        return value;
    }

    /// <summary>
    /// Maps a required identifier to its <c>@id</c>. Like <see cref="MapRequiredString"/> but
    /// routes a present value through <see cref="ClassifyIdentifier"/> so a malformed-UUID id is
    /// flagged while still being carried through unchanged.
    /// </summary>
    private static string? MapRequiredIdentifier(
        TransformationReport report, string sourcePath, string targetPath, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Record(report, sourcePath, targetPath, VodimClassification.Missing, null, null,
                $"Required field '{sourcePath}' is absent.");
            return null;
        }
        var (classification, note) = ClassifyIdentifier(value);
        Record(report, sourcePath, targetPath, classification, value, value, note);
        return value;
    }

    /// <summary>
    /// Classifies an ORUK identifier. ORUK/HSDS identifiers should be canonical RFC 4122 UUIDs, so
    /// a valid UUID (any case) is <see cref="VodimClassification.Valid"/> and anything else is
    /// <see cref="VodimClassification.Invalid"/> — with a note naming the kind of value present
    /// (integer, slug, URL, malformed UUID, or string) to aid diagnosis. The value is still carried
    /// through to the <c>@id</c> (a node must have an identifier); the classification records the
    /// data-quality defect. An empty value is left to the caller's required-field handling.
    /// </summary>
    private static (VodimClassification Class, string? Note) ClassifyIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Guid.TryParseExact(value, "D", out _))
            return (VodimClassification.Valid, null);
        return (VodimClassification.Invalid,
            $"Identifier '{Truncate(value, 40)}' is not a valid UUID (kind: " +
            $"{DescribeIdentifierKind(value)}); ORUK identifiers should be RFC 4122 UUIDs.");
    }

    /// <summary>Names the kind of a non-UUID identifier value, for diagnostics in the report.</summary>
    private static string DescribeIdentifierKind(string value)
    {
        if (LooksLikeMalformedUuid(value)) return "malformed UUID";
        if (value.All(char.IsAsciiDigit)) return "integer";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return "URL";
        if (SlugRegex.IsMatch(value)) return "slug";
        return "string";
    }

    /// <summary>
    /// True when <paramref name="value"/> looks like an attempt at a canonical UUID — five
    /// hyphen-separated groups of hex digits — but is not a valid one. Canonical UUIDs (any case)
    /// and values that are plainly a different id scheme both return false.
    /// </summary>
    private static bool LooksLikeMalformedUuid(string value)
    {
        // A canonical 8-4-4-4-12 UUID (upper or lower case) is well-formed — not a defect.
        if (Guid.TryParseExact(value, "D", out _)) return false;

        // Otherwise only flag a clear attempt at that layout: exactly five non-empty hex groups.
        var groups = value.Split('-');
        if (groups.Length != 5) return false;
        foreach (var group in groups)
            if (group.Length == 0 || !group.All(char.IsAsciiHexDigit))
                return false;
        return true;
    }

    // ── Collection-level reporting helpers ────────────────────────────────────────

    private static void RecordIfMapped(
        TransformationReport report, string sourcePath, string targetPath,
        string? sourceValue, string? mappedValue)
    {
        if (sourceValue is not null && mappedValue is not null)
            Record(report, sourcePath, targetPath,
                VodimClassification.Valid, sourceValue, mappedValue);
    }

    private static void RecordCollectionMapping(
        TransformationReport report, string sourcePath, string targetPath,
        int sourceCount, int mappedCount)
    {
        if (sourceCount == 0)
            Record(report, sourcePath, targetPath, VodimClassification.Missing,
                "0", "0");
        else
            Record(report, sourcePath, targetPath, VodimClassification.Valid,
                sourceCount.ToString(), mappedCount.ToString(),
                mappedCount < sourceCount
                    ? $"{sourceCount - mappedCount} items had no mappable location." : null);
    }

    // ── Core Record() helper ──────────────────────────────────────────────────────

    private static void Record(
        TransformationReport report,
        string sourcePath,
        string targetPath,
        VodimClassification classification,
        string? sourceValue = null,
        string? mappedValue = null,
        string? note = null)
    {
        // Cross-cutting nonconformance check: ORUK defines these as string fields, but the
        // tolerant deserializer captures an object/array-valued field as its raw JSON so the
        // record survives. Any such value — for any string field, not just `alert` — is a type
        // nonconformance, so it is flagged Invalid rather than reported as a clean mapping.
        if (classification is not (VodimClassification.Invalid or VodimClassification.Missing)
            && ArrivedAsJsonStructure(sourceValue))
        {
            classification = VodimClassification.Invalid;
            mappedValue = null;
            note = "ORUK defines this field as a string, but the feed supplied a JSON object/array (nonconformant).";
        }

        report.Add(new FieldMappingRecord
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Classification = classification,
            SourceValue = Truncate(sourceValue, 200),
            MappedValue = Truncate(mappedValue, 200),
            Note = note,
        });
    }

    private static string? Truncate(string? s, int max = 200) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Determines the jurisdiction for a GovernmentService.
    /// Derives from service_areas names (e.g., "Bristol") or feed base URL hostname.
    /// Returns the first service area name if present; otherwise derives from URI.
    /// </summary>
    private static string? DetermineJurisdiction(OrukService service, TransformationOptions options)
    {
        // Try service_areas first
        var firstArea = service.ServiceAreas?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstArea?.Name))
            return firstArea.Name;

        // Fallback: derive from service URI hostname if available
        // (e.g., "bristol.example.com" → "Bristol" or similar heuristic)
        // For now, just return null if no service areas
        return null;
    }
}
