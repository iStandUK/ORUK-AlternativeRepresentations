# NHS Directory of Healthcare Services (DoHS) API vs Open Referral UK — Semantic Comparison & Options for an ORUK API Drawing on NHS Data

*Planning-session brief. Prepared from six verified dimension analyses (entity topology, field crosswalk, API surface, identifiers/geography, taxonomy/metadata, coverage/fidelity). Where a verifier corrected an original claim, the correction is used here.*

---

## 1. Executive summary

**The dominating gap is a paradigm inversion.** NHS DoHS is **organisation-centric** and exposes a **single Azure Cognitive Search / OData endpoint** (`GET /` and a `POST /` body twin): one flat `Organisation` record (29 properties) with address, coordinates, opening times, contacts, facilities and quality metrics all baked inline. ORUK/HSDS is **service-centric and relational**: a four-entity graph — `Organization` 1—N `Service` N—M `Location`, joined by `ServiceAtLocation` — served as **per-entity REST collections** (`/services`, `/services/{id}`, `/organizations`, `/service_at_locations`, `/taxonomy_terms`) with required string `id` primary keys on every entity.

Everything else follows from that inversion. NHS has **no Service entity at all**: `Organisation.Services` is a bare `StringArray` of service *names* (`{"items":{"type":"string"}}`). ORUK's core unit — the structured `Service` (~23 scalars + 12 child collections: description, eligibility, cost, languages, schedules, application process, assurance…) — has essentially no NHS source.

**Feasibility verdict — blunt:**

- **How much of a useful ORUK `Service` record is sourceable from NHS? Well under ~15%.** From NHS you can populate a synthesised `Service.id`, `Service.name` (one array element), `Service.organization_id` (from `ODSCode`), a `status` down-cast from org-level `OrganisationStatus`, and `last_modified` (collapsed from `LastUpdatedDates`). Contact channels (`email`/`url`/phone) are partially sourceable **at organisation level** via the NHS `Contacts` array and can be inherited down. Everything else — `description`, `eligibility`, `cost_options`, `languages`, `application_process`, `required_documents`, `funding`, `program`, `service_areas`, assurance, real recurrence schedules — emits **empty**.
- **Realistic conformance ceiling:** **Bronze, and only with external enrichment.** ORUK marks `Location.uprn` as a required field for Location records, and NHS DoHS carries **no UPRN anywhere** — so Bronze is unreachable from NHS data alone without an Ordnance Survey postcode→UPRN lookup. **Silver** (which leans on populated taxonomy terms) is reachable only via a *synthesised* NHS-org-type taxonomy with no resolvable `term_uri`. **Gold** (schedules + eligibility + cost + regular assurance reviews) is **not achievable** from NHS alone.

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
| `Service.status` (active\|inactive\|defunct\|temporarily closed) | `OrganisationStatus` | semantic-mismatch | Org-level → pushed onto every service; re-map vocabulary to the enum. Cannot express one service closed while another open. |
| `Service.last_modified` | `LastUpdatedDates` (field→date map) | partial | Collapse to `max(date)`; optionally expand per-field into `OrukMetadata` audit rows. |
| `Location.id` | — (from ODSCode) | derived | `<ODSCode>-loc`. NHS has no location key. |
| `Location.name` | `OrganisationName` | derived | Reuse org name (no distinct site name). |
| `Location.organization_id` | `ODSCode` | derived | Direct FK. |
| `Location.latitude` / `longitude` (double) | `Latitude`/`Longitude` (**string**) **or** `Geocode.coordinates[]` | derived | **Type convert** string→double. Two redundant sources to reconcile. |
| `Location.uprn` (**ORUK-required**) | — | nhs-missing | **Hard gap.** No UPRN in NHS. External OS AddressBase / OS Places lookup required. |
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

- **UPRN — the conformance pivot.** ORUK marks `Location.uprn` as required for Location records; NHS DoHS provides **zero UPRNs**. Convenient leverage: the **NHS OAS itself already directs integrators to the Ordnance Survey Places API** for postcode→coordinate lookup — the same OS/AddressBase ecosystem that issues UPRNs. Extend that integration to capture UPRN.
  - *Verifier honesty note:* the standard doc defines Bronze only as "minimal required fields populated" and does not *explicitly* name UPRN as the Bronze gate; the C# `OrukLocation.Uprn` is a nullable string. So "Bronze hinges on UPRN" is a **strong, defensible inference**, not a spec-stated rule. Either way, shipping UPRN-less Locations risks rejection by strict validators.
  - **Licensing reality:** OS OpenUPRN gives only UPRN+coordinate (no address→UPRN match); reliable per-address matching needs **AddressBase Premium or OS Places API**, whose redistribution terms may constrain an open ORUK API.
- **USRN** — optional; derive from OS AddressBase or omit.
- **ONS `ServiceArea`** — NHS has **no catchment concept**. You *could* derive the containing LAD/LSOA/MSOA from postcode via ONSPD/NSPL, but containing area ≠ served area — a semantic trap. **Recommend: out of scope; do not promise ONS geography to consumers.**
- **ODSCode placement** — ORUK `Organization` has **no `external_identifiers` collection** (only `Location` does). So the clean homes for ODSCode are: `Organization.id` (recommended), optionally `Organization.uri` (resolvable ODS link), and/or a `Location.external_identifiers` row (`scheme="ODS"`).
- **Country / region** — normalise free-text `Country` → ISO alpha-2 `"GB"`; preserve England/Scotland/Wales/NI in `region` to avoid losing the home-nation.

**Conformance ladder (NHS-only source):**

| Tier | Reachable from NHS? | Blocker |
|---|---|---|
| **Bronze** | Only **with** external UPRN enrichment + defaulted required Service fields | `Location.uprn` absent from NHS |
| **Silver** | Only via a *synthesised* org-type taxonomy (no resolvable `term_uri`) | No ESD/ASCS/SNOMED coding in NHS |
| **Gold** | **No** | Needs schedules + eligibility + cost + regular assurance reviews, none in NHS |

---

## 7. The taxonomy angle

**Issue #81 turned on its head.** Native ORUK publishers currently ship **empty** taxonomy. An NHS-sourced feed could actually **out-populate** them on organisation classification — because NHS *does* carry coded org typing:

- `OrganisationTypeId` (controlled ODS code — the `$filter` example uses `'PHA'` and `'DEN'`; note **`'GP'` appears only in prose, not the filter example**) + `OrganisationType` (label) → synthesise one `TaxonomyTerm{code, name}` per distinct type.
- `OrganisationSubType` (e.g. "Community") → child `TaxonomyTerm` with `parent_id` → the type term (reproduces ORUK's hierarchy).
- `Facilities[]` (grouped by `FacilityGroupName`) → facility/accessibility `Attribute` rows.
- You must also **fabricate a parent `Taxonomy`** record (e.g. `name="NHS Organisation Type"`, synthetic `uri`, `version`) and **manufacture all `Attribute` join rows** (NHS has no join layer).

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
- **Cons:** No place to hold enrichment → **cannot supply UPRN** → cannot reach Bronze; every request pays NHS auth/latency/rate limits; deep pagination fights Azure `$skip` ceiling; `@odata.count` inexactness surfaces directly to consumers; unservable filters must be rejected live.
- **Choose when:** proof-of-concept / demo, or an explicit **sub-Bronze** "NHS-shaped-as-ORUK" read surface where freshness beats conformance.

### Option B — Batch ETL into an ORUK datastore + enrichment
Scheduled harvest of NHS → transform (fan-out, id synthesis, unit/type conversions) → **enrich (UPRN via OS, optional taxonomy crosswalk)** → persist as native ORUK entities → serve standard ORUK REST from the store.

- **Pros:** The **only path to Bronze** (UPRN enrichment has somewhere to live); exact pagination/counts; clean per-entity routes; stable synthesised ids managed centrally; deterministic, testable converters; extension namespace for NHS-only richness.
- **Cons:** Staleness between refreshes; storage + pipeline ops; republishing persisted NHS data squarely engages the **Online Connection Agreement** and OS redistribution terms; enrichment cost/maintenance.
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
2. **UPRN sourcing** — OS Places API vs AddressBase Premium? Budget, licence, and **redistribution terms** for an open ORUK API. Define the **fallback policy for unresolved UPRNs** (multi-UPRN postcodes, PO boxes): omit the Location, or emit a UPRN-less (non-conformant) Location?
3. **Service granularity** — `1 org → 1 Service` (honest to NHS granularity) vs **explode `Services[]` into N Services** (more ORUK-shaped, but each carries only `name`)? This cascades into `ServiceAtLocation` cardinality and every consumer query.
4. **Identity strategy** — Deterministic id convention (UUIDv5 over `ODSCode` / `ODSCode+serviceName`) frozen as a **public contract**; how ids survive NHS refreshes and service-name edits.
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

- **Bronze unreachable from NHS alone.** Mandatory `Location.uprn` is absent; per-service required fields are synthetic. An unenriched feed fails strict validation.
- **Fabricated entities imply precision NHS never had.** N `Service` records per org, all sharing one location and one org-level schedule/status, suggest per-service hours/eligibility/address the source never asserted (false precision). Deriving `Service.status` from org-level `OrganisationStatus` (wrong granularity, different value set) can mislabel every service and mis-signal open/closed to referral tools.
- **Silently hollow feed.** ORUK clients expect populated eligibility/cost/languages/schedules; an NHS-backed API returns them empty, degrading the very query use-cases (accessibility, by-language, schedule) ORUK consumers rely on — and empty Services may read as *data-quality failures* rather than a source limitation.
- **Silent loss of NHS's differentiator.** The `Metric` quality block, `AcceptingPatients`, EPS flag are dropped by ORUK consumers unless carried as ignored extensions.
- **Coordinate & conversion traps.** GeoJSON axis order is **undocumented in the OAS** (`[lon,lat]` is an assumption); two coordinate sources (string pair vs `Geocode` array) can disagree; WKT for `geo.distance` is **lon-before-lat** — a swap yields plausible-but-wrong points with no error. `OffsetOpeningTime` is minutes-past-midnight (480→"08:00"), not HHmm; `>1440` and `IsOpen=false` have no clean representation.
- **UPRN mismatch = referral-safety risk.** Address-match errors assign the wrong authoritative property id; enrichment coverage will be imperfect.
- **Pagination/count inexactness.** `@odata.count` is documented approximate; Azure `$skip` ceiling + `@odata.next` misalign with ORUK page numbers → wrong counts, incomplete or duplicated deep results.
- **Taxonomy trust risk.** A feed richer at the taxonomy layer than ESD-aligned publishers but lacking resolvable `term_uri` can create a false impression of interoperability.
- **Licensing unknowns.** Whether the Online Connection Agreement permits open ORUK re-publication, and whether OS UPRN redistribution terms allow it, are **open governance questions** that can block the whole effort.
- **Bespoke extension effort may be wasted.** Non-standard `Attribute`/extension link-types for `IsEpsEnabled`/`AcceptingPatients`/`Trusts` fall outside ORUK codelists and may be ignored or flagged by validators.