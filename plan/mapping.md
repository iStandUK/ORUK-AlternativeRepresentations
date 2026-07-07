# ORUK → Schema.org Field Mapping

This document defines how data elements from Open Referral UK (ORUK) feeds are mapped to Schema.org types and properties when producing JSON-LD output.

## Guiding Principles

- **Receive liberally:** If a source field is absent or null, omit the corresponding Schema.org property from the output rather than failing.
- **Supply conservatively:** Only emit properties with well-defined, non-null values.
- **Typed identifiers:** Every output node carries a stable `@id` URI so that nodes can be cross-referenced within the `@graph`.
- **Fidelity over completeness:** A partial but accurate representation is preferred over a padded but inaccurate one.

---

## 1. Service → GovernmentService / Service

The ORUK `Service` entity maps to `schema:GovernmentService` for government or public-sector services, or `schema:Service` for private/voluntary providers.

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `id` | `@id` | Constructed as `<baseUrl>/services/<id>` |
| `name` | `name` | Direct mapping. |
| `alternate_name` | `alternateName` | Direct mapping. |
| `description` | `description` | Direct mapping. |
| `url` | `url` | Direct mapping. |
| `last_modified` | `additionalProperty[dateModified]` | ISO 8601 timestamp of last update. Emitted as an additionalProperty because `dateModified` is a `CreativeWork` property, not a `Thing` property, and `GovernmentService` does not inherit from `CreativeWork`. |
| `email` | — | **Unmapped.** `schema:GovernmentService` has no suitable property for a service-level contact email. Field is intentionally omitted from output (lossy). See [README – Unmapped ORUK fields](../../README.md#unmapped-oruk-fields). |
| `status` | `schema:serviceType` note | `active` → no special annotation; `inactive` → `schema:discontinued` pattern (no standard property; use `additionalProperty`). |
| `interpretation_services` | `availableLanguage` | Map to `Language` type if structured languages present; fall back to free-text name if no structured languages. |
| `application_process` | `termsOfService` | Free-text description of how to access the service. |
| `wait_time` | `additionalProperty` | Name: `waitTime`. |
| `fees_description` | `offers.description` | See CostOption mapping below. |
| `accreditations` | `hasCredential` | Map to `EducationalOccupationalCredential` where possible. |
| `eligibility_description` | `audience.description` | See Eligibility mapping. |
| (organisation reference) | `provider` | `@id` reference to `Organization` node. |
| (organisation reference) | `serviceOperator` | **GovernmentService only.** `@id` reference to `Organization` node (same as provider). |
| (service area) | `jurisdiction` | **GovernmentService only.** Derived from first `service_area.name` if present. |
| (location reference) | `location` | `@id` reference to `Place` node. |
| (schedule reference) | `openingHoursSpecification` | See Schedule mapping. |
| (contact reference) | `contactPoint` | See Contact mapping. |
| (taxonomy terms) | `additionalType` | Schema.org URL or keyword for service category. |
| (service area) | `areaServed` | See ServiceArea mapping. |
| (cost options) | `offers` | See CostOption mapping. |

---

## 2. Organization → Organization

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `id` | `@id` | Constructed as `<baseUrl>/organisations/<id>` |
| `name` | `name` | Direct mapping. |
| `alternate_name` | `alternateName` | Direct mapping. |
| `description` | `description` | Direct mapping. |
| `email` | `email` | Direct mapping. |
| `url` | `url` | Direct mapping. |
| `legal_status` | `legalName` / `additionalProperty` | Free text; use `additionalProperty` if no standard property applies. |
| `logo` | `logo` | URL string → `ImageObject`. |
| `uri` | `sameAs` | Linked-data URI for the organisation. |
| `year_incorporated` | `foundingDate` | Year only. |

---

## 3. Location → Place

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `id` | `@id` | Constructed as `<baseUrl>/locations/<id>` |
| `name` | `name` | Direct mapping. |
| `description` | `description` | Direct mapping. |
| `latitude` | `geo.latitude` | Nested in `GeoCoordinates`. |
| `longitude` | `geo.longitude` | Nested in `GeoCoordinates`. |
| `address.address_1` | `address.streetAddress` | Nested in `PostalAddress`. |
| `address.city` | `address.addressLocality` | Nested in `PostalAddress`. |
| `address.state_province` | `address.addressRegion` | Nested in `PostalAddress`. |
| `address.postal_code` | `address.postalCode` | Nested in `PostalAddress`. |
| `address.country` | `address.addressCountry` | ISO 3166-1 alpha-2 code (e.g. `GB`). |
| `location_type` | `additionalProperty` | Name: `locationType`. |
| `accessibility` | `amenityFeature` | Map each accessibility feature to `LocationFeatureSpecification`. |
| `url` | `url` | Venue URL if available. |
| `external_identifier` (UPRN) | `identifier` | `PropertyValue` with `propertyID: "UPRN"`. Preferred UPRN source. |
| `uprn` (scalar) | `identifier` | `PropertyValue` with `propertyID: "UPRN"`, value trimmed. Fallback UPRN source used only when no `external_identifier` with scheme `UPRN` is present; if both exist the structured `external_identifier` wins and the scalar is recorded (VODIM) as `Other`/superseded. A single `identifier` slot holds the UPRN — both sources describe the same scheme, so no list is required. |

---

## 4. Schedule → OpeningHoursSpecification

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `weekday` | `dayOfWeek` | Map to Schema.org day URI (e.g. `https://schema.org/Monday`). |
| `opens_at` | `opens` | ISO 8601 time string (`HH:mm`). |
| `closes_at` | `closes` | ISO 8601 time string (`HH:mm`). |
| `valid_from` | `validFrom` | ISO 8601 date. |
| `valid_to` | `validThrough` | ISO 8601 date. |
| `description` | `description` | Free-text schedule description. |

**Day of week mapping:**

| ORUK value | Schema.org URI |
|------------|----------------|
| `Monday` | `https://schema.org/Monday` |
| `Tuesday` | `https://schema.org/Tuesday` |
| `Wednesday` | `https://schema.org/Wednesday` |
| `Thursday` | `https://schema.org/Thursday` |
| `Friday` | `https://schema.org/Friday` |
| `Saturday` | `https://schema.org/Saturday` |
| `Sunday` | `https://schema.org/Sunday` |
| `PublicHolidays` | `https://schema.org/PublicHolidays` |

---

## 5. Contact → ContactPoint

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `name` | `contactType` | Role label (e.g. "enquiries", "referrals"). |
| `phone` (number) | `telephone` | Include country code where possible. |
| `email` | `email` | Direct mapping. |
| `url` | `url` | Direct mapping. |
| `title` | `name` | Title of the contact (if person). |
| `department` | `contactType` | Department name if `name` is absent. |

---

## 6. Eligibility → Audience

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `eligibility_description` | `audience.description` | Free-text description of who can access the service. |
| `minimum_age` | `audience.suggestedMinAge` | Integer years. |
| `maximum_age` | `audience.suggestedMaxAge` | Integer years. |
| (audience type) | `audience.@type` | Use `PeopleAudience` for age-range criteria; `Audience` otherwise. |

---

## 7. CostOption → Offer

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `option` / `amount` | `price` | Numeric value. |
| `currency` | `priceCurrency` | ISO 4217 code (default `GBP`). |
| `amount_description` | `description` | Free-text cost description. |
| `valid_from` | `validFrom` | ISO 8601 date. Valid start date for this offer. |
| `valid_to` | `priceValidUntil` | ISO 8601 date. Valid end date for this offer. |
| `option` = "free" | `price: 0` | Normalise free services to `price: 0`. |

**Note:** Schema.org `Offer` type supports both `validFrom` (available since Schema.org v3.4) and `priceValidUntil` properties. These are distinct and should not be conflated.

---

## 8. ServiceArea → areaServed

| ORUK field | Schema.org type/property | Notes |
|------------|--------------------------|-------|
| `service_area` (name) | `areaServed.name` | Name of the area. |
| `extent` (geographic code) | `areaServed.identifier` | ONS code (LAD, LSOA, etc.) as `PropertyValue`. |
| `extent_type` | `areaServed.additionalProperty` | Name: `extentType` (e.g. `LocalAuthorityDistrict`). |

---

## 9. TaxonomyTerm → additionalType / keywords

The handling of taxonomy terms is the most complex vocabulary-mapping challenge in the pipeline.  Full analysis is in [terminology.md](terminology.md); the rules applied during transformation are summarised here.

### Mapping Rules (applied in priority order)

| Priority | Condition | Schema.org output |
|----------|-----------|-------------------|
| 1 | `term_uri` is present | `additionalType = term_uri`; also append `name` to `keywords` |
| 2 | `code` present AND `vocabulary` is in the URI registry | Construct URI from registry template; use as `additionalType`; append `name` to `keywords` |
| 3 | `code` or `name` present; `vocabulary` unknown or absent | `keywords += name` only (no `additionalType`) |

### Field Mapping

| ORUK field | Schema.org property | Notes |
|------------|---------------------|-------|
| `term_uri` | `additionalType` | Preferred: use the URI directly. |
| `name` | `keywords` | Always included as a keyword regardless of whether a URI is available. |
| `code` + known `vocabulary` | `additionalType` | URI constructed from the [vocabulary registry](terminology.md#32-vocabulary-to-uri-registry). |
| `taxonomy_id` | `additionalProperty` | Name: `taxonomyId` — preserves provenance. |
| `parent_id` chain | `keywords` | Hierarchical path (e.g. `"Health > Mental Health > Counselling"`) appended to `keywords`. |

### DefinedTerm Pattern

When a URI exists but is not a standard Schema.org type URL, emit a `DefinedTerm` node to preserve the human label:

```json
{
  "additionalType": {
    "@type": "DefinedTerm",
    "@id": "https://standards.esd.org.uk/?uri=esd%3Aservice%2F841",
    "name": "Day Opportunities",
    "inDefinedTermSet": "https://standards.esd.org.uk/"
  }
}
```

### Known Taxonomy URI Prefixes

The `taxonomy` field (ORUK v3) is free text. Values are matched case-insensitively after trimming.

| `taxonomy` value | URI template |
|------------------|--------------|
| `esdstandards` / `esd standards` | `https://standards.esd.org.uk/?uri=esd%3Aservice%2F{code}` |
| `snomed` / `snomed ct` | `http://snomed.info/sct/{code}` |
| `loinc` | `http://loinc.org/{code}` |
| `icd-10` | `http://hl7.org/fhir/sid/icd-10/{code}` |
| Anything else | No URI — `keywords` only |

### Future FHIR Mapping Note

When producing HL7 FHIR `HealthcareService` output, these same taxonomy terms must be translated to `CodeableConcept` objects (see [terminology.md § 4](terminology.md#4-fhir-healthcareservice--vocabulary-mapping-issues)).  A FHIR Terminology Server (e.g. NHS England Ontoserver) is required for the ESD → SNOMED CT translation step.  See [terminology.md § 5](terminology.md#5-fhir-terminology-server--role-in-the-architecture) for the architecture.

---

## 10. Fields With No Direct Schema.org Mapping

These ORUK fields do not have direct Schema.org equivalents.  They should be included as `additionalProperty` values (`PropertyValue` nodes) to preserve fidelity.

| ORUK field | Suggested `additionalProperty` name |
|------------|-------------------------------------|
| `service.status` | `orukStatus` |
| `service.assured_date` | `assuredDate` |
| `service.assured_by` | `assuredBy` |
| `organization.legal_status` | `legalStatus` |
| `location.usrn` | `usrn` |
| `location.location_type` | `locationType` |

> **Note — `location.uprn`:** UPRN is *not* emitted as an `additionalProperty`. It has a direct
> Schema.org home as `Place.identifier` (a `PropertyValue` with `propertyID: "UPRN"`); see § 3.
> The scalar `location.uprn` field feeds that same `identifier` slot as a fallback for the
> structured `external_identifier[UPRN]`. Emitting it as an `additionalProperty` as well would
> duplicate the value, so it is deliberately excluded here.

---

## Consolidated JSON-LD Structure

```json
{
  "@context": "https://schema.org",
  "@graph": [
    {
      "@type": "GovernmentService",
      "@id": "<baseUrl>/services/<service-id>",
      "name": "Service Name",
      "provider": { "@id": "<baseUrl>/organisations/<org-id>" },
      "location": { "@id": "<baseUrl>/locations/<location-id>" },
      "openingHoursSpecification": [...],
      "contactPoint": [...],
      "audience": {...},
      "offers": [...],
      "areaServed": [...],
      "keywords": "..."
    },
    {
      "@type": "Organization",
      "@id": "<baseUrl>/organisations/<org-id>",
      "name": "Organisation Name"
    },
    {
      "@type": "Place",
      "@id": "<baseUrl>/locations/<location-id>",
      "name": "Location Name",
      "address": { "@type": "PostalAddress", ... },
      "geo": { "@type": "GeoCoordinates", ... }
    }
  ]
}
```
