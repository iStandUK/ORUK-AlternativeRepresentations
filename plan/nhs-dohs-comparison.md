# NHS Directory of Healthcare Services (DoHS) API vs Open Referral UK — Semantic Comparison & Options for an ORUK API Drawing on NHS Data

*Planning-session brief. Prepared from six verified dimension analyses (entity topology, field crosswalk, API surface, identifiers/geography, taxonomy/metadata, coverage/fidelity). Where a verifier corrected an original claim, the correction is used here.*

> **Update — 2026-07-07 (UPRN):** This brief originally treated the missing UPRN as a hard blocker requiring third-party OS/AddressBase geocoding. That is **superseded**. The **NHS Organisation Data Service (ODS)** now holds UPRN and exposes it via the **open-access ORD API** (`GET https://directory.spineservices.nhs.uk/ORD/2-0-0/organisations/{ODSCode}` → `GeoLoc.Location.UPRN`), joinable on the **same `ODSCode`** DoHS returns — verified live (`RBQ → 38150603`; GP practice `A81001 → 100110780253`; site `RBQ07`/RC2 → `40074525`). Caveats: coverage is **partial** (only new/address-changed ODS records are UPRN-stamped — e.g. `Q69` returns none). **Both follow-up checks now confirmed** (see [`code/NHS-oas/uprn-source-verification.md`](../code/NHS-oas/uprn-source-verification.md)): the strategic **R4 Organisation Data Terminology – FHIR API also emits UPRN** (via the UK Core `AddressKey` extension; `RBQ → 38150603`, matching the ORD API), and **real dentist/pharmacy/optician codes resolve with UPRN** (19/21 probed; the DoHS sandbox `V…` codes were just fixture data). §1, §3, §6, §8, §9 and §10 below reflect this.

---

## 1. Executive summary

**The dominating gap is a paradigm inversion.** NHS DoHS is **organisation-centric** and exposes a **single Azure Cognitive Search / OData endpoint** (`GET /` and a `POST /` body twin): one flat `Organisation` record (29 properties) with address, coordinates, opening times, contacts, facilities and quality metrics all baked inline. ORUK/HSDS is **service-centric and relational**: a four-entity graph — `Organization` 1—N `Service` N—M `Location`, joined by `ServiceAtLocation` — served as **per-entity REST collections** (`/services`, `/services/{id}`, `/organizations`, `/service_at_locations`, `/taxonomy_terms`) with required string `id` primary keys on every entity.

Everything else follows from that inversion. NHS has **no Service entity at all**: `Organisation.Services` is a bare `StringArray` of service *names* (`{"items":{"type":"string"}}`). ORUK's core unit — the structured `Service` (~23 scalars + 12 child collections: description, eligibility, cost, languages, schedules, application process, assurance…) — has essentially no NHS source.

**Feasibility verdict — blunt:**

- **How much of a useful ORUK `Service` record is sourceable from NHS? Well under ~15%.** From NHS you can populate a synthesised `Service.id`, `Service.name` (one array element), `Service.organization_id` (from `ODSCode`), a `status` down-cast from org-level `OrganisationStatus`, and `last_modified` (collapsed from `LastUpdatedDates`). Contact channels (`email`/`url`/phone) are partially sourceable **at organisation level** via the NHS `Contacts` array and can be inherited down. Everything else — `description`, `eligibility`, `cost_options`, `languages`, `application_process`, `required_documents`, `funding`, `program`, `service_areas`, assurance, real recurrence schedules — emits **empty**.
- **Realistic conformance ceiling:** **Bronze, reachable via a sibling NHS API (see the UPRN update below).** ORUK marks `Location.uprn` as a required field for Location records, and the DoHS *search* API carries **no UPRN**. However — correcting this brief's original assumption — the **NHS Organisation Data Service (ODS) does now hold UPRN**, exposed via the open-access **ORD API** keyed by the same `ODSCode`. This removes the need for a fuzzy postcode→UPRN geocode and makes Bronze attainable for records ODS has matched (coverage is partial — see §6). **Silver** (which leans on populated taxonomy terms) is reachable only via a *synthesised* NHS-org-type taxonomy with no resolvable `term_uri`. **Gold** (schedules + eligibility + cost + regular assurance reviews) is **not achievable** from NHS alone.

> **One-line takeaway for the session:** This is not a field-renaming exercise. It is *entity synthesis + external enrichment*. NHS gives you a strong Organisation/Location/geolocation skeleton and near-empty Services. Plan the effort around identity minting, entity fan-out, and UPRN sourcing — not attribute copying.

---

## 2. Model topology comparison

| Aspect | NHS DoHS | ORUK / HSDS |
|---|---|---|
| Orientation | Organisation-centric | Service-centric |
| Core entities | 1 real entity: `Organisation` | 4: `Organization`, `Service`, `Location`, `ServiceAtLocation` |
| "Service" representation | `Services`: `StringArray` of names | First-class `Service` (~23 scalars + 12 child collections) |
| Location | Inline address block on Organisation | Separate `Location` entity with own `id` + `organization_id` FK |
| Service↔Location join | None (conflated into the org) | `ServiceAtLocation` join entity |
| Primary keys | `ODSCode` (org only) | Required string `id` on all four entities |
| API shape | One search endpoint | Multiple per-entity REST collections |
| Envelope | OData `{@odata.context,@odata.count,@odata.next, value:[…]}` | `OrukPage {contents,total_items,total_pages,page_number,first_page,last_page,empty}` |

*Envelope precision note (verifier):* in the OAS the response `value[]` is typed as an array of `SearchResult`, whose only declared property is `@search.score`; the Organisation fields are inlined onto each result by Azure Search. Practically each item carries Organisation data **plus** `@search.score`.

### How one NHS Organisation explodes into the ORUK graph

```
                          NHS Organisation (ODSCode)
                                    │  fan-out
        ┌───────────────┬───────────┴────────────┬──────────────────────┐
        ▼               ▼                        ▼                       ▼
 Organization      Location (×1)          Service (×N)         ServiceAtLocation (×N)
 id = ODSCode   id = <ODSCode>-loc   id = <ODSCode>#<slug|idx>  id = <service_id>@<location_id>
 parent = ParentOrganisation.ODSCode   organization_id = ODSCode  service_id + location_id
                (inline address block)   name = Services[i]        (pure join synthesis)
```

- **Typical cardinality:** 1 Organisation → 1 `Organization` + 1 `Location` + **N** `Service` (one per `Services[]` name string, if exploded) + **N** `ServiceAtLocation`.
- **`Location` links two ways:** directly via `Location.organization_id = ODSCode`, and navigationally via `Service → ServiceAtLocation → Location`.

### The identity-synthesis problem

NHS supplies exactly **one** natural key below the feed: `ODSCode` (on the org, and on `ParentOrganisation`). ORUK requires string `id` on **all four** entities. Therefore:

- `Organization.id` = `ODSCode` (clean; also makes `parent_organization_id` resolve directly from `ParentOrganisation.ODSCode`).
- `Location.id`, `Service.id`, `ServiceAtLocation.id` must be **synthesised deterministically** (e.g. UUIDv5 over `ODSCode` / `ODSCode + serviceName`), so ids survive nightly refreshes.
- **Stability is a public contract.** Consumers will bookmark `/services/{id}`; if ids derive from mutable text (edited service names), every re-poll churns ids and orphans references. Seed from stable inputs and freeze the convention.
- **Treat `ODSCode` as opaque — never parse it.** Legacy codes vary in shape (3-char orgs like `RBQ`; historically-suffixed site codes like `RBQ07`; `F…` pharmacy, `V…` dental); newer allocations are 5-char **`ANANA`** (alpha-num-alpha-num-alpha, e.g. `A0C1W`, `C4B2A`), a scheme adopted to expand the code space. The historic "site code = parent code + suffix" reading **no longer generalises and must not be used** to infer the org↔site hierarchy — that relationship lives in ODS **relationship** data (ORD `Rels` / FHIR `OrganizationAffiliation`), e.g. `RBQ07` links to `RBQ` via relationship `RE6`. So reconstructing which sites belong to which organisation is a **relationship-graph** join, not a string operation.

### ODS site records are frequently *services*, with a temporal liveness model

A useful wrinkle from ODS (verified — see [`code/NHS-oas/ods-site-overloading-elland-road.csv`](../code/NHS-oas/ods-site-overloading-elland-road.csv)): the **site record class (`orgRecordClass RC2`) is often overloaded to represent a service, not "an organisation at a site."** Searching ODS for postcodes around Elland Road, Leeds (`LS11 0EB` / `LS11 0ES`) returns RC2 records literally named for services — `LCH – ELLAND RD P&R – SAIS COVID VACCINATION SERVICE` (`H8I8M`), `ELLAND ROAD STADIUM – COVID VACCINATION CENTRE` (`C4B2A`) — alongside one genuine RC1 organisation (`X6R3V` Leeds United Foundation). This **cuts against the earlier assumption that DoHS/ODS has no service layer**: an RC2-as-service is a *de facto* ORUK `Service` (or `ServiceAtLocation`), and its ODS record already carries a real address + UPRN. Harvesting ODS RC2 records by area (or by name — searching `physio`, `dental` etc. returns many) is a candidate way to recover *some* structured services that DoHS's bare `Services[]` name-array cannot express — with the heavy caveat that the naming is unstructured free text and the pattern is inconsistent across publishers. **The service *type* of these provider sites is not role-coded** (their primary role is administrative — PCT/CCG/LA Site) and lives only in the name; see §7 and [`code/NHS-oas/ods-roles-and-service-type.md`](../code/NHS-oas/ods-roles-and-service-type.md).

Each ODS entity also carries **`Operational` and `Legal` date periods** (`Start`/`End`) plus an explicit `Status` (Active/Inactive). This is the **authoritative source for `Service.status`** and for filtering dead entries — materially better than DoHS's org-level `OrganisationStatus` (which §3 flags as wrong-granularity). Liveness rule (hardened from the field observation "past start + no end = live"): **`Operational.Start ≤ today AND (Operational.End absent OR Operational.End > today)`**, corroborated by `Status`. Two edge cases the rule must handle: a **future-dated `End`** means still-live-until-then (don't treat "has an end date" as closed), and **`Operational` ≠ `Legal`** (use Operational for service availability — e.g. `H8I8M` operated two quarters past its legal end). Worked example: both Elland Road vaccination sites have *past* operational ends → correctly **not live**; `X6R3V` has a past start and no end → **live**.

### One premises can host many services from many organisations — and UPRN ties them together

This is the flip side that **rehabilitates the `ServiceAtLocation` join** the DoHS search API lacks. ODS records that share a premises share a **UPRN**, so UPRN is the key that reconstructs co-location. Worked example (verified — [`code/NHS-oas/ods-shared-premises-example.md`](../code/NHS-oas/ods-shared-premises-example.md)): at 23 Mill Hey, Haworth (**UPRN `100051944949`**), `FWF76` is the *Day Lewis Pharmacy* dispensing service (role `RO182`, operated by **Day Lewis PLC**) and `C5I3V` is a *COVID Local Vaccination Service* at the same shop (role `RO279`, operated by **Rimmington Pharmacy** — a different org). **One premises → two services → two organisations → one UPRN.** In ORUK that is exactly: **1 `Location`** (deduped on UPRN) ← **2 `ServiceAtLocation`** → **2 `Service`**, each with a **different `organization_id`** resolved from the record's own `RE6` relationship. So the correct model is *not* "1 org → 1 location": it's **dedup Locations on UPRN, but keep Service and Organization identity per ODS record** (merging them would misattribute a service to the wrong provider). The providing org can differ from the premises owner (the vaccination service runs under Rimmington, not Day Lewis), so resolve providers via relationships, not by assuming the co-located pharmacy runs everything.

---

## 3. Field-level crosswalk (core entities)

Relationship legend: **exact** (direct copy) · **partial** (maps, with loss/cardinality issues) · **derived** (needs transform/synthesis) · **semantic-mismatch** (meaning differs) · **nhs-missing** (no NHS source) · **oruk-missing** (no ORUK home).

| ORUK element | NHS source | Relationship | Transform / notes |
|---|---|---|---|
| `Organization.id` | `Organisation.ODSCode` | derived | Use ODSCode directly (or synth UUID + keep ODS in `uri`). Only natural key. |
| `Organization.name` | `OrganisationName` | exact | Direct copy. |
| `Organization.alternate_name` | `OrganisationAliases` (StringArray) | partial | Cardinality loss: array → single string; take first or overflow to attributes. |
| `Organization.parent_organization_id` | `ParentOrganisation.ODSCode` | exact | Direct if `Organization.id=ODSCode`. `ParentOrganisation.OrganisationName` is lost. Dangling-ref risk if parent not in harvest. |
| `Organization.email` / `url` / `website` | `Contacts[]` filtered by `ContactMethodType` → `ContactValue` | derived | **NHS *does* have a Contacts schema** (`ContactType, ContactAvailabilityType, ContactMethodType, ContactValue`). Demultiplex by method type. |
| `Service.*` (whole entity) | `Services` (StringArray of names) | nhs-missing | **THE central gap.** Only `name` populated; `description`, `eligibility`, `fees`, `application_process`, `accreditations`, `minimum_age`/`maximum_age`, plus `Fees`/`WaitTime`/`Licenses` all empty. |
| `Service.id` | — | derived | Synthesise stably: `<ODSCode>#<slug\|index>` / UUIDv5. |
| `Service.name` | `Services[i]` | partial | Under explode strategy, the only populated Service scalar. |
| `Service.organization_id` | `ODSCode` | derived | FK back to Organization.id. |
| `Service.status` (active\|inactive\|defunct\|temporarily closed) | `OrganisationStatus` (DoHS) / **`Status` + `Operational` dates (ODS)** | semantic-mismatch → derived | DoHS is org-level (pushed onto every service; can't express one service closed while another open). **Prefer the ODS record's own `Status`/`Operational` period** (per-RC2-entity, so finer-grained) — see §2 liveness rule. |
| `Service.last_modified` | `LastUpdatedDates` (field→date map) | partial | Collapse to `max(date)`; optionally expand per-field into `OrukMetadata` audit rows. |
| `Location.id` | — (from ODSCode) | derived | `<ODSCode>-loc`. NHS has no location key. |
| `Location.name` | `OrganisationName` | derived | Reuse org name (no distinct site name). |
| `Location.organization_id` | `ODSCode` | derived | Direct FK. |
| `Location.latitude` / `longitude` (double) | `Latitude`/`Longitude` (**string**) **or** `Geocode.coordinates[]` (DoHS only) | derived | **Type convert** string→double. **The ODS ORD/FHIR APIs return NO coordinates — only UPRN** (see §6 geocoding note); coordinates come either from DoHS (subset only, precision unspecified) or by resolving the UPRN via OS Open UPRN / OS Places. |
| `Location.uprn` (**ORUK-required**) | — (DoHS) / `GeoLoc.Location.UPRN` (**ODS ORD API**) | derived | **Not in the DoHS search API, but the sibling ODS ORD API supplies UPRN directly, keyed by the same ODSCode** (verified live — see §6). Partial coverage; fallback needed for nulls. |
| `Location.usrn` (optional) | — | nhs-missing | No source; safe to omit. |
| `Location.external_identifiers[]` (`identifier`, `identifier_scheme`, `identifier_type`) | `ODSCode` | derived | Best structured home for raw ODSCode: `identifier=ODSCode, identifier_scheme="ODS"`. |
| `Location.location_type` | — | nhs-missing | Default `"physical"`. |
| `Address.address_1` | `Address1` | exact | Direct. |
| `Address.address_2` | `Address2` (+ `Address3`) | partial | **NHS has 3 lines, ORUK 2.** Merge Address2+Address3, or route `Address3` into `Address.attention`. |
| `Address.city` | `City` | exact | Direct. |
| `Address.region` / `state_province` | `County` | partial | County→region (loose fit). Could also carry home-nation here. |
| `Address.postal_code` | `Postcode` | exact | Direct; also the key for UPRN/coordinate/ONS enrichment. |
| `Address.country` (ISO 3166-1 alpha-2) | `Country` (free text) | semantic-mismatch | "England"/"United Kingdom" → `"GB"`; home-nation distinction lost (preserve in `region`). |
| `Address.address_type` | — | nhs-missing | Default `"physical"`. |
| `Phone.number` | `Contacts[].ContactValue` where method = phone/fax | derived | Demultiplex Contacts by `ContactMethodType`. |
| `Phone.type` (voice\|fax\|textphone…) | `Contacts[].ContactMethodType` | derived | Map NHS vocabulary → RFC6350 phone types. |
| `Phone.description` | `ContactType` + `ContactAvailabilityType` | partial | No exact home; concat as lossy free text. |
| `Contact.name`/`title`/`department` | — | nhs-missing | **Concept mismatch:** ORUK Contact = person/role; NHS Contact = a single channel value. Person fields stay empty. |
| `Schedule.opens_at` (HH:mm) | `OpeningTimes_inner.OffsetOpeningTime` (minutes past midnight, e.g. 480) | derived | **Unit convert** 480→"08:00", 1110→"18:30" (div/mod 60, zero-pad). |
| `Schedule.closes_at` | `OffsetClosingTime` | derived | Same; watch values >1440 (past-midnight). |
| `Schedule.byday` + `freq` | `Weekday` (Monday..Sunday) | derived | Recode full name → MO..SU; **synthesise** `freq="WEEKLY"`. |
| `Schedule.valid_from`/`dtstart` | `AdditionalOpeningDate` + `OpeningTimeType` | partial | Exception days → one-off schedules; heuristic. |
| Schedule closed-period | `IsOpen` (bool) | semantic-mismatch | `IsOpen=false` has no lossless ORUK representation. |
| `Schedule.timezone` | — | nhs-missing | Default `"Europe/London"`. |
| `Accessibility.description`/`details` | `Facilities[]` (`Id,Name,Value,FacilityGroupName`) | partial | Nearest analogue; `FacilityGroupName` has no home. Amenity vs accessibility is ambiguous — document the binding. |
| `ServiceAtLocation.*` (`id`,`service_id`,`location_id`) | — | nhs-missing | Fully synthesised: one SAL per (Service × the org's single Location). |
| `ServiceArea.*` (ONS LAD/LSOA/MSOA) | — | nhs-missing | No catchment concept in NHS. |
| `Language.*`, `CostOption.*`, `Eligibility.*`, `RequiredDocument.*`, `Funding.*`, `Program.*` | — | nhs-missing | Entire per-service richness unsourceable. |

*Geo caveat (verifier):* the OAS `Geocode` schema defines `coordinates` only as a 2-number array with **no declared axis order**, and `Geocode.type` is an unconstrained string (not asserted GeoJSON `Point`). The `[longitude, latitude]` ordering is a well-founded **RFC 7946 assumption to reconcile**, not a spec fact — treat coordinate order as a validation risk, not a certainty.

---

## 4. API & query surface

**Shape of the work:** 6+ ORUK routes **fan in** to a single NHS `GET /` (or `POST /`). The bulk of engineering is envelope rewriting, pagination arithmetic and synthetic identity — not per-field value mapping.

| ORUK request | NHS mapping | Relationship | Notes |
|---|---|---|---|
| `GET /services` (list) | `GET /` with `search='*'` + `$filter`/`$select` | partial | NHS returns Organisations; synthesise Service view. |
| `GET /services/{id}` | — (no service identity) | nhs-missing | **Make-or-break.** Mint reversible synthetic ids that round-trip to `$filter=ODSCode eq '…'` + service selection. |
| `GET /organizations`, `/{id}` | `GET /`, or `$filter=ODSCode eq '{id}'` / `search.ismatch('{id}','ODSCode')` | derived | Cleanest projection — Organisation is native, ODSCode stable. |
| `GET /service_at_locations` | — (no join) | nhs-missing | Fabricated 1:1 from the org's own address. |
| `GET /taxonomy_terms` | — (no taxonomy) | nhs-missing | Returns empty (but native ORUK feeds also ship empty — issue #81). |
| `page` (1-based) | `$skip=(page-1)*per_page` | derived | Beware Azure `$skip` ceiling (~100k) + `@odata.next` continuation beyond it. |
| `per_page` | `$top` | exact | Clamp to NHS limits (default 50; ORUK `MaxPageSize=100`). |
| `total_items` | `@odata.count` (needs `$count=true`) | partial | **`@odata.count` is documented as approximate** → `total_pages`/`last_page` inherit inexactness. |
| `total_pages`/`first_page`/`last_page`/`empty` | computed | derived | `ceil(count/per_page)` etc. |
| `text` (keyword) | `search` (+ `searchFields`/`searchMode`/`queryType`) | partial | NHS text control is far richer; facade fixes those knobs internally. |
| proximity — nearest-first | `$orderBy=geo.distance(Geocode, geography'POINT(lon lat)')` | exact | **Strongest, headline capability.** First-class in the OAS. WKT is **lon before lat**. |
| proximity — radius filter | `$filter=geo.distance(...) le radiusKm` **or** `search.ismatch('<postcode>','Postcode')` | derived | **Verifier:** `geo.distance` is documented only for `$orderBy`, not `$filter` — radius-via-`$filter` is inferred Azure behavior. NHS *does* document postcode matching via `search.ismatch(...,'Postcode')` — a **no-geocode** proximity path. |
| `minimum_age`/`maximum_age`, cost/free-only, language, delivery-type filters | — | nhs-missing | **Unservable.** No NHS source fields at all. |
| `updated_since` | `$filter` over a `LastUpdatedDates.<field>` | partial | Per-field, not per-record → approximate. |
| (no request-side projection) | `$select` | oruk-missing | ORUK always returns full nested records. |
| (unranked lists) | `@search.score` | oruk-missing | Native relevance dropped (or stashed in an extension). |
| (fixed typed filters) | arbitrary `$filter` OData boolean | oruk-missing | Available internally to the facade, not to ORUK consumers. |
| (open feeds) | required `api-version` + OAuth + Online Connection Agreement | oruk-missing | **Auth inverted:** facade holds NHS credentials server-side, presents an open ORUK surface. |

**Unsupported-filter policy is a required decision:** for `taxonomy_term`, age, cost, language, delivery-type — either **reject with an explicit unsupported-parameter error** or best-effort map — but **do not silently ignore** (that returns unfiltered results the client believes were filtered). Document the choice in the conformance statement.

---

## 5. What NHS cannot supply / what NHS supplies that ORUK loses

### ORUK fields left empty (no NHS source)

- The entire structured `Service` payload beyond `name`: `description`, `eligibility_description`, `application_process`, `fees_description`/`cost_options`, `minimum_age`/`maximum_age`, `accreditations`, `assured_date`/`assurer_email`, `alert`, `interpretation_services`, `required_documents`, `funding`, `program`, plus `Fees`/`WaitTime`/`Licenses`.
- `Location.uprn` (**ORUK-required**) and `usrn`.
- `ServiceArea` (ONS LAD/LSOA/MSOA geography).
- `Language` entity; structured `Eligibility`; `CostOption` currency/amount.
- `Contact` as a **person/role** (name/title/department).
- Schedule recurrence richness: `timezone`, `interval`, `until`, `count`, `wkst`, `bymonthday`, `valid_from`/`valid_to`, `attending_type`.
- `Organization.description`, `legal_status`, `logo`, `year_incorporated`, `uri`.
- Populated `Taxonomy`/`TaxonomyTerm`/`Attribute`.

### NHS data with no ORUK home (push to attributes / metadata / vendor extension, or drop)

- **`Metric` block** — 18 props (`MetricID`, `MetricName`, `Description`, `Value`/`Value2`/`Value3`, `BandingClassification`, `BandingName`, `MetricDisplayTypeName`, `HospitalSectorType`, `LinkUrl`/`LinkText`, `IsMetaMetric`, …). NHS's richest differentiator; **no ORUK quality/performance concept at all.**
- `AcceptingPatients` (map of `Acceptance{Id,Name,AcceptingPatients:bool}`) — GP list intake status.
- `IsEpsEnabled` (Electronic Prescription Service flag — **string-typed in the OAS**, not bool).
- `GSD` — opaque undocumented string.
- `RelatedIAPTCCGs` and `Trusts` (StringArrays) — commissioning/trust affiliations; ORUK has only a single `parent_organization_id`.
- `OrganisationType`/`OrganisationTypeId`/`OrganisationSubType` (partially rehomeable via taxonomy — §7).
- `Geocode.crs` (CoordinateReferenceSystem) — dropped; safe only if EPSG:4326.
- `@search.score`; `@odata.context`/`@odata.next` envelope metadata.
- Third address line (`Address3`); multi-valued `OrganisationAliases` beyond the first.

> Reserve a **declared vendor extension namespace** on `Service`/`Location` so this content is retained — but note ORUK validators/consumers will ignore it, and taxonomy/attributes are empty across all live feeds (issue #81), so consumers likely won't read it either. Decide deliberately; don't drop silently.

---

## 6. UK localisation & conformance

- **UPRN — the conformance pivot, now largely unblocked by a sibling NHS API.** ORUK marks `Location.uprn` as required for Location records; the DoHS *search* API returns **zero UPRNs**. **But the correct source is the NHS Organisation Data Service, not third-party geocoding.** ODS has migrated to addressing software that records UPRN against organisation/site records, and it is exposed by the **ORD API** (`GET https://directory.spineservices.nhs.uk/ORD/2-0-0/organisations/{ODSCode}`) at `GeoLoc.Location.UPRN`. This means a **direct, authoritative `ODSCode → UPRN` join on the same key DoHS already returns** — no fuzzy postcode→UPRN match, and OS/AddressBase licensing is absorbed upstream by NHS.
  - **Verified live (2026-07-07):** `RBQ` → `UPRN 38150603`; `A81001` (GP practice) → `100110780253`; `RX2` → `200004018207`; `RBQ07` (a **site**, `orgRecordClass RC2`) → `40074525`. The ORD API is **open-access — no auth, no Online Connection Agreement** — with a UAT sandbox at `uat.directory.spineservices.nhs.uk`.
  - **Coverage is partial — plan a null-fallback.** UPRN is recorded only for organisations **created or address-changed since** the ODS addressing-software migration; older untouched records return no UPRN (verified: `Q69` "Thames Valley Area Team" has **no** `UPRN`). It is also absent from legacy CSV products (XML/API only). So UPRN raises the Bronze ceiling to *reachable*, but a fallback (OS Places postcode match, or emit a UPRN-less Location) is still needed for the unmatched tail.
  - **Which NHS API — both work, confirmed live.** The ORD API (`2-0-0`, bespoke JSON/XML) is proven and open-access but under deprecation review. The strategic surface is the newer **R4 Organisation Data Terminology – FHIR API** (positioned as the ODS "single source of truth") — and it **does emit UPRN**, via the UK Core `address.extension` `AddressKey` (`AddressKeyType` code `UPRN`). Verified: `RBQ → 38150603` on the FHIR API, **identical** to the ORD API value. (The older STU3 "ODS – FHIR API" is **retired in production** and doesn't populate UPRN — don't target it.) Recommend building against the R4 Terminology API; keep ORD as a fallback while it lives.
  - **DoHS code classes resolve — confirmed.** The DoHS *sandbox* `V…`/`OP_…` codes are synthetic fixture data (they 404 in live ODS), but **real** dentist (`RO110`, e.g. `V00003`), pharmacy (`RO182`, e.g. `FA002`) and optician (`RO167`, e.g. `A0C1W`) codes resolve **with** UPRN — 19 of 21 probed codes carried one (the misses were a defunct area team and one optician). Sites appear as `orgRecordClass RC2` records with their own UPRN (`RBQ07 → 40074525`).
  - *Standard-wording note:* the ORUK standard doc defines Bronze only as "minimal required fields populated" and does not *explicitly* name UPRN as the Bronze gate; the C# `OrukLocation.Uprn` is a nullable string. "Bronze hinges on UPRN" is a **strong, defensible inference**, not a spec-stated rule — but shipping UPRN-less Locations still risks rejection by strict validators.
- **USRN** — optional; **not** in the ODS Address entity (which carries only address lines/town/county/country/postcode/uprn); derive from OS AddressBase or omit.
- **Coordinates (`Location.latitude`/`longitude`) — the ODS APIs give none; UPRN is a pointer, not a point.** Verified ([`code/NHS-oas/ods-geocoding-precision.md`](../code/NHS-oas/ods-geocoding-precision.md)): neither the ODS ORD API nor the R4 ODS FHIR API returns any coordinate (no `position`/lat-long/easting-northing; no FHIR `Location` resource) — only address + UPRN. So a precise coordinate needs a **UPRN→coordinate resolution step**, but because the UPRN is authoritative this is an **exact, property-level** lookup, not fuzzy geocoding. Cheapest form: **OS Open UPRN** (free, OGL) held as a **local join table** — an offline lookup, not a runtime third API call; use OS Places API (keyed, PSGA-free for public sector) only for address→UPRN on the unmatched tail. DoHS *does* return `Latitude`/`Longitude`/`Geocode`, but only for its "services near you" subset (the ODS-only RC2 service records won't be there) and at unspecified precision. A postcode centroid (postcodes.io/ONSPD) is the last-resort fallback — tens–hundreds of metres off premises.
- **ONS `ServiceArea`** — NHS has **no catchment concept**. You *could* derive the containing LAD/LSOA/MSOA from postcode via ONSPD/NSPL, but containing area ≠ served area — a semantic trap. **Recommend: out of scope; do not promise ONS geography to consumers.**
- **ODSCode placement** — ORUK `Organization` has **no `external_identifiers` collection** (only `Location` does). So the clean homes for ODSCode are: `Organization.id` (recommended), optionally `Organization.uri` (resolvable ODS link), and/or a `Location.external_identifiers` row (`scheme="ODS"`).
- **Country / region** — normalise free-text `Country` → ISO alpha-2 `"GB"`; preserve England/Scotland/Wales/NI in `region` to avoid losing the home-nation.

**Conformance ladder (NHS-only source):**

| Tier | Reachable from NHS? | Blocker |
|---|---|---|
| **Bronze** | **Yes** for ODS-matched records (UPRN via the ODS ORD API) + defaulted required Service fields; needs a fallback for the unmatched tail | `Location.uprn` absent from DoHS *search* API, but sourceable from ODS ORD API (partial coverage) |
| **Silver** | Only via a *synthesised* org-type taxonomy (no resolvable `term_uri`) | No ESD/ASCS/SNOMED coding in NHS |
| **Gold** | **No** | Needs schedules + eligibility + cost + regular assurance reviews, none in NHS |

---

## 7. The taxonomy angle

**Issue #81 turned on its head.** Native ORUK publishers currently ship **empty** taxonomy. An NHS-sourced feed could actually **out-populate** them on organisation classification — because NHS *does* carry coded org typing:

- `OrganisationTypeId` (controlled ODS code — the `$filter` example uses `'PHA'` and `'DEN'`; note **`'GP'` appears only in prose, not the filter example**) + `OrganisationType` (label) → synthesise one `TaxonomyTerm{code, name}` per distinct type.
- `OrganisationSubType` (e.g. "Community") → child `TaxonomyTerm` with `parent_id` → the type term (reproduces ORUK's hierarchy).
- `Facilities[]` (grouped by `FacilityGroupName`) → facility/accessibility `Attribute` rows.
- You must also **fabricate a parent `Taxonomy`** record (e.g. `name="NHS Organisation Type"`, synthetic `uri`, `version`) and **manufacture all `Attribute` join rows** (NHS has no join layer).

**Better still — ODS *roles* are a coded service/profession type.** The strongest categorisation source isn't DoHS `OrganisationType` at all; it's the **ODS role set** (`RO…`, 204 roles) on each org/site record — see [`code/NHS-oas/ods-roles-and-service-type.md`](../code/NHS-oas/ods-roles-and-service-type.md). For **high-street contractor classes the role *is* the service** ("an Optical Site is effectively an optician/optometrist"): `RO167` Optical Site, `RO182` Pharmacy, `RO110` General Dental Practice, `RO65` Private Dental Practice, `RO76` GP Practice. These give a clean, coded ORUK `Service`-type / `TaxonomyTerm` per record. **Three caveats that bound its usefulness:**

  - **Read the whole role set, not the primary flag.** The primary role is often *not* the service — a GP surgery's primary role is `RO177 Prescribing Cost Centre` (financial), with `RO76 GP Practice` as *non-primary*; a trust's is `RO197 NHS Trust` with `RO57 Foundation Trust` non-primary. Derivation rule: pick the most service-specific role across the set, then fall back to name text.
  - **NHS provider *sites* aren't coded by service.** Every physio/dental provider site probed carried only an **administrative** primary role (`RO180` PCT Site, `RO99` CCG Site, `RO222` LA Site) — there is **no "physiotherapy" role in ODS at all**. For exactly the RC2-as-service records from §2, the service lives **only in the free-text name**, uncoded.
  - **It's an ODS role vocabulary, not ESD/ASCS/SNOMED** — so `term_uri` still stays null (keywords-level), same limit as below.

**The hard limits — position honestly, not as ESD/SNOMED conformance:**

- The vocabulary would be an **invented NHS/ODS org-type scheme**, *not* ESD Standards / ASCS / SNOMED. `term_uri` stays **null** → keywords-level fidelity only, no resolvable URIs.
- NHS explicitly **does not support FHIR** and carries no SNOMED, so the `HealthcareService.category`→SNOMED backbone that the UK profile / `terminology.md` anticipates is absent; any clinical coding needs a **separate crosswalk step** (e.g. NHS Ontoserver `$translate`).
- `Services` is a bare name array — **per-service** categorisation (ORUK's heart) is unsourceable; all real classification sits on the organisation and risks being mislabelled onto synthesised Service records.
- **Risk:** a feed that looks *richer* at the taxonomy layer than ESD-aligned publishers, but lacks resolvable URIs, can give a false impression of interoperability and undermine ecosystem trust.

---

## 8. Architecture options

### Option A — Thin real-time OData→REST facade
A stateless service that translates each ORUK request into a live NHS `GET`/`POST` call and rewrites the envelope on the fly.

- **Pros:** Always fresh; low storage/ops; fastest to stand up; no data-republishing footprint (lighter licensing surface if data isn't persisted).
- **Cons:** UPRN now *can* be fetched live from the open ODS ORD API, but that means a **second upstream call per record** (latency/rate-limit cost; a per-ODSCode cache largely mitigates it); no place to hold *other* enrichment (taxonomy crosswalk, merged Service data); every request pays NHS auth/latency; deep pagination fights Azure `$skip` ceiling; `@odata.count` inexactness surfaces directly to consumers; unservable filters must be rejected live.
- **Choose when:** proof-of-concept / demo, or a lean read surface — Bronze is now *technically* reachable via the extra ODS call, but freshness-over-richness is the honest positioning.

### Option B — Batch ETL into an ORUK datastore + enrichment
Scheduled harvest of NHS → transform (fan-out, id synthesis, unit/type conversions) → **enrich (UPRN via the ODS ORD API keyed on ODSCode; OS Places fallback for the unmatched tail; optional taxonomy crosswalk)** → persist as native ORUK entities → serve standard ORUK REST from the store.

- **Pros:** The **cleanest path to Bronze** (UPRN join + fallback have somewhere to live and cache); exact pagination/counts; clean per-entity routes; stable synthesised ids managed centrally; deterministic, testable converters; extension namespace for NHS-only richness.
- **Cons:** Staleness between refreshes; storage + pipeline ops; republishing persisted NHS data squarely engages the **DoHS Online Connection Agreement** (the ODS ORD API itself is open-access) and any residual OS redistribution terms on UPRN; enrichment cost/maintenance.
- **Choose when:** the goal is a **genuinely conformant, queryable ORUK API** — the realistic target for this initiative.

### Option C — Hybrid (recommended)
NHS DoHS supplies the **Organisation/Location/geolocation skeleton** via batch ETL (Option B) with UPRN enrichment; **richer Service payload** (description, eligibility, cost, languages, real schedules) is sourced from **other ORUK feeds / local-authority data** and merged on the same entities. Proximity/nearest-first can optionally still delegate live to NHS `$orderBy=geo.distance` where freshness matters.

- **Pros:** Overcomes NHS's fatal weakness (empty Services) by design; keeps NHS's strengths (org coverage, coordinates, nearest-first search); can climb toward Silver/Gold as other sources fill Service fields.
- **Cons:** Most complex; needs entity-matching/merge logic and provenance tracking across sources; governance across multiple licences.
- **Choose when:** you want a **useful, not just structurally valid**, ORUK API — NHS alone yields a semantically hollow feed.

### Recommendation
**Adopt Option B as the foundation and plan for Option C.** A thin facade (A) cannot reach Bronze because it has nowhere to host the mandatory UPRN, and NHS Services are too sparse to be useful without additional sources. Build the batch-ETL-into-ORUK-store pipeline with UPRN enrichment as a **first-class component**, design the store to accept supplementary ORUK/LA sources, and expose NHS's native strength (`geo.distance` nearest-first) prominently. Treat any real-time facade as an optional accelerator for proximity queries, not the primary architecture.

---

## 9. Key decisions & open questions for the planning session

1. **Conformance target** — Ship sub-Bronze (honest "NHS-as-ORUK") or commit to Bronze (requires UPRN enrichment)? This gates everything below.
2. **UPRN sourcing** — **primary source is the ODS API** (`ODSCode → UPRN`, open-access, authoritative). Both surfaces are confirmed to emit UPRN, so the decision is **ORD API** (proven today, deprecation-review) vs the strategic **R4 Terminology FHIR API** (recommended; UK Core `AddressKey` extension) — not *whether* it works. Still to decide: refresh cadence for the UPRN join, and the **fallback for the unmatched tail** (defunct/older records ODS hasn't UPRN-stamped) — OS Places postcode match, or emit a UPRN-less (non-conformant) Location? Confirm onward-redistribution terms for ODS-sourced UPRN in an open ORUK API.
3. **Service granularity & the RC2-as-service question** — `1 org → 1 Service` (honest to NHS granularity) vs **explode `Services[]` into N Services** (more ORUK-shaped, but each carries only `name`) vs **harvest ODS `RC2` site records as services** (they carry real address + UPRN, but names are free text and the overloading is inconsistent — see §2)? This cascades into `ServiceAtLocation` cardinality and every consumer query.
   - **Liveness/status rule** — adopt the ODS `Operational`-period test (`Start ≤ today AND (End absent OR End > today)`, corroborated by `Status`) as the source for `Service.status` and for excluding dead entries, in preference to DoHS org-level `OrganisationStatus`. Decide the refresh cadence that keeps status current.
4. **Identity strategy** — Deterministic id convention (UUIDv5 over `ODSCode` / `ODSCode+serviceName`) frozen as a **public contract**; how ids survive NHS refreshes and service-name edits.
   - **`Location` keyed on UPRN, with co-location dedup** — multiple ODS records can share one premises/UPRN (e.g. `FWF76` + `C5I3V` at UPRN `100051944949`). Dedup those to a **single ORUK `Location`**, but keep a **`Service`/`ServiceAtLocation` per ODS record** and resolve each service's `organization_id` from that record's `RE6` relationship (the provider can differ from the premises owner). Decide the UPRN-collision policy and how Locations are keyed when UPRN is absent (the unmatched tail).
5. **Enrichment sources** — For a useful feed (Option C), which ORUK/LA feeds fill `description`/`eligibility`/`cost`/`languages`/`schedules`? Entity-matching and provenance approach.
6. **Taxonomy policy** — Synthesise the NHS org-type taxonomy (populates where #81 leaves blank, but no `term_uri`) vs invest in an ESD/ASCS crosswalk vs SNOMED via Ontoserver? Register whichever scheme is chosen.
7. **Unsupported-filter behavior** — Reject with explicit error vs best-effort map for age/cost/language/delivery-type/taxonomy. Document in the conformance statement.
8. **NHS-only data disposition** — Drop, or park `Metric`/`AcceptingPatients`/`IsEpsEnabled`/`Trusts`/`RelatedIAPTCCGs` in a declared extension namespace? (Validators ignore extensions.)
9. **Licensing / governance** — Does the DoHS **Online Connection Agreement** permit open re-publication as ORUK? Confirm before building. Credential/quota management for server-side NHS auth.
10. **Refresh cadence & sync semantics** — Batch interval; how to expose `updated_since` given NHS's per-field `LastUpdatedDates` (not per-record).
11. **Sub-object attachment level** — Canonical home for org-level Contacts / OpeningTimes→Schedule / Facilities→Accessibility (recommend Organization for contacts, ServiceAtLocation for schedules, Location for accessibility) — document it.
12. **Country/region normalisation** — Confirm `Country`→`"GB"` with home-nation preserved in `region`; `Address3` → `address_2` merge vs `attention`.

---

## 10. Risks & unknowns

- **Bronze reachable but not free.** The original "no UPRN in NHS" blocker is **resolved** — ODS holds UPRN, joinable by ODSCode via the open ORD API. Residual risk is **partial coverage** (ODS UPRN-stamps only new/changed records; some return null) and the DoHS-code-class question, so an unenriched or unmatched record still fails strict validation. Per-service required fields remain synthetic.
- **Fabricated entities imply precision NHS never had.** N `Service` records per org, all sharing one location and one org-level schedule/status, suggest per-service hours/eligibility/address the source never asserted (false precision). Deriving `Service.status` from org-level `OrganisationStatus` (wrong granularity, different value set) can mislabel every service and mis-signal open/closed to referral tools.
- **Silently hollow feed.** ORUK clients expect populated eligibility/cost/languages/schedules; an NHS-backed API returns them empty, degrading the very query use-cases (accessibility, by-language, schedule) ORUK consumers rely on — and empty Services may read as *data-quality failures* rather than a source limitation.
- **Silent loss of NHS's differentiator.** The `Metric` quality block, `AcceptingPatients`, EPS flag are dropped by ORUK consumers unless carried as ignored extensions.
- **Coordinate & conversion traps.** GeoJSON axis order is **undocumented in the OAS** (`[lon,lat]` is an assumption); two coordinate sources (string pair vs `Geocode` array) can disagree; WKT for `geo.distance` is **lon-before-lat** — a swap yields plausible-but-wrong points with no error. `OffsetOpeningTime` is minutes-past-midnight (480→"08:00"), not HHmm; `>1440` and `IsOpen=false` have no clean representation.
- **UPRN mismatch = referral-safety risk.** Address-match errors assign the wrong authoritative property id; enrichment coverage will be imperfect.
- **Pagination/count inexactness.** `@odata.count` is documented approximate; Azure `$skip` ceiling + `@odata.next` misalign with ORUK page numbers → wrong counts, incomplete or duplicated deep results.
- **Taxonomy trust risk.** A feed richer at the taxonomy layer than ESD-aligned publishers but lacking resolvable `term_uri` can create a false impression of interoperability.
- **Licensing unknowns.** Whether the **DoHS** Online Connection Agreement permits open ORUK re-publication (the **ODS ORD API is open-access**, so the UPRN source itself is unencumbered), and whether onward redistribution of ODS-sourced UPRN carries residual OS terms, are **open governance questions** that can block the whole effort.
- **Bespoke extension effort may be wasted.** Non-standard `Attribute`/extension link-types for `IsEpsEnabled`/`AcceptingPatients`/`Trusts` fall outside ORUK codelists and may be ignored or flagged by validators.