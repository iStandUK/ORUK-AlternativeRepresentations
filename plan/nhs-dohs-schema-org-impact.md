# Impact on the ORUK→Schema.org conversion

Sourcing ORUK data from NHS ODS/DoHS **inverts the completeness profile** of our JSON-LD output rather than uniformly enriching or degrading it. A handful of Place/identity fields that native ORUK feeds ship empty flip to reliably populated — `Place.identifier` (authoritative UPRN), `Place.geo` (via a new UPRN→OS Open UPRN resolution step), and a coded `additionalType`/`serviceType` derived from ODS organisation roles (native feeds currently emit **no** taxonomy at all, issue #81). At the same time the entire Service-payload surface — `audience` (eligibility), `offers` (cost), `availableLanguage`, `openingHoursSpecification`, `application_process`, assurance — stays `Missing`, because DoHS supplies little more than a service name. The switch also forces several concrete transformer changes: re-seeding `Place.@id`/dedup on **UPRN** (with a lockstep change to the Service→Place reference), a new `ods-role` entry in the hard-coded vocabulary registry, a UPRN→coordinate geocoding hook on `TransformationOptions`, an optional field-merge in `JsonLdMerger`, and a new liveness gate. None of this is free: the UPRN identifier path, the geo resolver, role selection and the date/liveness rule all require code or upstream-harvester work, and two latent defects (the never-emitted `location.uprn` scalar and the non-standard `{type,id}` location reference) surface under the new source and should be fixed alongside.

---

## 1. Field-by-field impact

| Schema.org target | NHS/ODS source | Change | Verdict |
|---|---|---|---|
| `Place.identifier` (PropertyValue `propertyID=UPRN`) | ODS ORD `GeoLoc.Location.UPRN` / R4 FHIR UKCore `AddressKey`, keyed by ODSCode | UPRN goes from sporadic free-text to authoritative and definitive; reliably present (null-fallback still needed for defunct records lacking a UPRN) | **now-populatable** |
| `Place.@id` | ODS UPRN (shared across co-located records, e.g. `100051944949`) | Re-seed node identity on UPRN so co-located records converge to one Place; fall back to location-id when UPRN absent | **new-work** |
| `Place.geo` (`GeoCoordinates`) | ODS UPRN → OS Open UPRN local table (WGS84 lat/long) | ODS returns **no** coordinates, so `MapGeoCoordinates(null,null)` returns `Missing` and geo is omitted until a resolver populates it | **newly-conditional** |
| `Place.geo.latitude/.longitude` | OS Open UPRN WGS84 columns | When resolved, a property-level LA-assigned point — more precise and provenance-defined than an arbitrary feed coordinate | **enhanced** |
| `GovernmentService.additionalType` (`DefinedTerm`) | ODS role `RO182` pharmacy / `RO167` optical / `RO110` dental / `RO76` GP | Coded `DefinedTerm` where native feeds emit nothing (empty taxonomy, #81) — NHS out-populates native | **now-populatable** |
| `DefinedTerm.termCode` + `inDefinedTermSet` + `@id` + `name` | ODS role code + display + OrganizationRole CodeSystem URI | Existing `SchemaOrgDefinedTerm` shape already carries all four fields — no model change | **now-populatable** |
| `Service.serviceType` (free text) | ODS role display, e.g. `"PHARMACY"` | Never emitted today (`SchemaOrgService.ServiceType` is set nowhere in `src/`); `MapService` must be changed to set it | **new-work** |
| `Thing.keywords` | ODS role display | Role display appended to keywords like any term name — for contractor classes only | **enhanced** |
| `GovernmentService.additionalProperty[orukStatus]` | ODS `Status` (Active/Inactive) | ODS always carries status, so `orukStatus` flips `Missing`→`Valid`; `active`/`inactive` already classify `Valid` via `ValidOrukStatuses` | **enhanced** |
| `GovernmentService` (whole-node emission) | ODS `Operational.Start/End` + `Status` | Defunct/inactive records are currently emitted in full; NHS sourcing wants them skipped — no liveness gate exists | **new-work** |
| `Place.identifier` (ODSCode scheme) | ODSCode (`external_identifier` scheme ≠ UPRN) | `MapExternalIdentifiers` recognises **only** scheme `UPRN` and drops everything else, so ODSCode is neither emitted nor scored | **new-work** |
| `SchemaOrgThing.identifier` cardinality | ODS UPRN + ODSCode (both authoritative) | `Identifier` is a single `SchemaOrgPropertyValue?`, so a Place cannot carry both UPRN and ODSCode — make it a list or route ODSCode to `additionalProperty` | **newly-conditional** |
| `GovernmentService.additionalType` for **provider sites (RC2)** | RC2 sites carry only admin roles (`RO180`/`RO99`/`RO222`), no clinical role | No coded `additionalType`; service label trapped in the site `Name` (which maps only to `name`, not `keywords`) — degrades to Type C | **degraded** |
| `audience` / `offers` / `availableLanguage` / `openingHoursSpecification` | DoHS bare service-name array | eligibility, age bounds, cost, languages, interpretation, schedules, application all emit `Missing` per field — `Valid%` collapses (source limitation, not a defect) | **degraded** |

---

## 2. The `DefinedTerm` opportunity (ODS roles → `additionalType`)

Native ORUK feeds ship an empty taxonomy (issue #81), so for contractor classes an ODS **role** is the coded service-type signal we have never had. A role carries a code (`RO182`), a display (`"PHARMACY"`), and belongs to the ODS OrganizationRole CodeSystem — which maps cleanly onto the existing `SchemaOrgDefinedTerm` shape (`@id` / `inDefinedTermSet` / `termCode` / `name`) already emitted by `MapAttributes`. The prize is threefold: a coded `additionalType`, a free-text `Service.serviceType`, and enriched `keywords`.

Three things must happen to make roles resolve **above keyword level**:

- **Registry entry.** `KnownTaxonomyPrefixes` is currently `{esdstandards, esd standards, snomed, snomed ct, loinc, icd-10}` (six entries incl. aliases) with no ODS key, and `ConstructTaxonomyUri`'s switch has no ODS case — so any role term falls to Priority-3 (keywords-only). Add `ods-role` (aliases `ods`, `odsrole`) to both places, in lockstep. Adding it to only one silently disables the Priority-2 path.
- **`inDefinedTermSet` override.** For ODS the branch must set `inDefinedTermSet` to the **full** OrganizationRole CodeSystem URI, not `ExtractBaseUri(...)`, which would wrongly reduce it to a host root (e.g. `https://fhir.nhs.uk/`). Introduce a per-scheme `inDefinedTermSet` lookup alongside the URI template. *(Note: the exact CodeSystem URI/host is not yet confirmed — the ODS ORD FHIR endpoint is hosted under `directory.spineservices.nhs.uk`, so treat any `fhir.nhs.uk/CodeSystem/ODSAPI-OrganizationRole-1` string as illustrative and pin it from the live ODS metadata before shipping. The `DefinedTerm.@id` for a role is an **opaque, non-dereferenceable** identifier — there is no per-concept HTML page, unlike ESD/SNOMED term URIs.)*
- **Role selection (upstream).** The derivation rule is **"scan the full role set and prefer the most service-specific role over the `primaryRole` flag"** — *not* "the non-primary role". For most contractor classes the service role **is** the primary role (M&S Opticians primary `RO167`; Rowlands primary `RO182`; dental primary `RO110`); only **GP Practice `RO76`** is typically non-primary. Selection, plus skipping admin roles (`RO180`/`RO99`/`RO222`/`RO177` Prescribing Cost Centre), belongs in the ODS→ORUK **ingestion adapter** that synthesises `OrukTaxonomyTerm{ Taxonomy="ods-role", Code, Name, TermUri=null }`. Leave `TermUri` null so the Priority-2 construction path runs; do **not** fabricate an ESD/SNOMED URI. Keep a defensive skip-list in `MapAttributes` as backstop.

**Hard routing constraint:** `MapAttributes` is invoked **only** from `MapService` (line ~296) — never from `MapOrganization`/`MapLocation`. Since roles are organisation/site attributes, the adapter must attach the synthesised `OrukAttribute` to the **`OrukService`**, or it is silently dropped.

**Where it degrades:** RC2 provider sites carry only admin roles, so there is no `OrukTaxonomyTerm` at all → `additionalType`, `serviceType` **and** `keywords` all stay empty (the service label lives only in the site `Name`, which `MapService` maps to `name` alone). Extracting it needs name parsing/NLP — out of scope. `serviceType` is also single-valued, so a record with several meaningful roles keeps one in `serviceType`; the rest must go to `additionalType`/`keywords`.

---

## 3. Node identity & the `@graph`

Today `Transform` runs per-`OrukService` and emits a self-contained sub-graph (one Service, one Organization from `service.Organization`, one Place per `ServiceAtLocations[].Location`); `JsonLdMerger.Merge` then concatenates every `@graph` and dedups purely on `@id` (Ordinal `HashSet`, **first-occurrence-wins, silent skip, no field union**). The many-services→one-Place collapse already works mechanically — but only because both `Place.@id` (`MapLocation`: `options.LocationUri(location.Id)`) and the Service→Place reference (`MapService` line 263: `options.LocationUri(sal.Location.Id)`) derive from the ORUK `location.Id`.

Under ODS sourcing, two co-located records (FWF76 Day Lewis dispensing, C5I3V COVID service, shared UPRN `100051944949`) arrive as distinct ODS records that must dedup to **one Place on UPRN**. Required changes:

- **Re-key `Place.@id` on UPRN.** Add a canonical helper, e.g. `CanonicalLocationKey(loc) => string.IsNullOrWhiteSpace(loc.Uprn) ? loc.Id : $"uprn-{loc.Uprn.Trim()}"`, and seed the Place `@id` from it (`{BaseUrl}/locations/{key}`), falling back to `location.Id` when UPRN is absent. **Which UPRN source is canonical must be decided:** the `OrukLocation.Uprn` scalar is currently **never emitted** — only `external_identifiers[scheme=UPRN]` reaches `Place.identifier` (line 548 via `MapExternalIdentifiers`), while `location.uprn` is only *recorded to VODIM* (lines 485-490) and never output. The dedup key and the emitted identifier must be reconciled to the same source.
- **Move the reference side in lockstep.** `MapService` line 263 must build the Service→Place ref from the identical `CanonicalLocationKey`, or `Service.location` dangles after the Place is deduped. Ship both together. **Pre-existing defect to fix while here:** that reference is emitted as `new { type = "@id", id = ... }`, serialising to `{"type":"@id","id":"..."}` — **not** a standard JSON-LD `{"@id":"..."}` node reference, so it may not resolve as a proper `@id` even today. Correct the envelope, not just the URI.
- **Per-service provider.** `provider` and `serviceOperator` (currently forced equal) must reference the **operating** org resolved from that record's ODS **RE6** relationship (Rimmington Pharmacy `FX417` for C5I3V; Day Lewis PLC `P05F` for FWF76). The transformer already emits `provider = OrganisationUri(service.Organization.Id)` per service and the merger keeps both Organization nodes distinct — correct **as long as ingestion sets `service.Organization` to the RE6 operator, not the premises owner** (`MapLocation` ignores `OrukLocation.OrganizationId`, so the owner is never emitted — an accepted lossy point).
- **`JsonLdMerger` upgrade (recommended).** With first-wins/no-union, when two co-located Place variants share a UPRN `@id` but differ (one resolved OS coordinates, the other did not; slightly different names), the richer variant is silently dropped and which one survives is **feed-order-dependent**. Add a Place-specific field-union path (union non-null `geo`, `telephone`, `identifier`, `amenityFeature`); at minimum warn instead of discarding. Do **not** merge Service or Organization nodes.

**Guards:** UPRN dedup must be paired with a **site-overloading** guard so genuinely distinct sub-premises sharing one UPRN are not collapsed into one Place; `@id` embeds the per-deployment `BaseUrl`, so two feeds produce different `@id`s for the same UPRN — true cross-feed convergence needs a BaseUrl-independent namespace or dedup on `identifier.value`.

---

## 4. Geo

Geo stops being a direct field copy and becomes **conditional on UPRN resolution**. ODS returns no coordinates, so an NHS feed arrives with `latitude`/`longitude` null and `Place.geo` is omitted for essentially every record until a resolver runs. The good news is that the conservative contract — *omit `geo` when unresolved, but still emit address + UPRN identifier* — already falls out of the code: `SchemaOrgPlace.Geo` is `[JsonIgnore(WhenWritingNull)]` and the identifier is emitted independently, so omission is clean with no change.

The work is adding the resolver:

- Add `public Func<string,(double Lat,double Lng)?>? ResolveUprnCoordinate { get; init; }` to `TransformationOptions.cs`, backed by the **OS Open UPRN** local join table (free OGL, offline). Default null preserves today's behaviour exactly (opt-in).
- In `MapLocation` (around the `MapGeoCoordinates` call, line ~479): keep native feed coordinates as priority 1; when both are null **and** a UPRN is present **and** the resolver is set, call it and feed the result into the existing `MapGeoCoordinates` so all range validation (`−90..90` / `−180..180`) is reused. **No CRS juggling and no string parsing** on this path: OS Open UPRN already holds WGS84 decimal lat/long and `OrukLocation.Latitude/Longitude` are already `double?` → cast to the `required decimal` `SchemaOrgGeoCoordinates` fields unchanged.
- **Column hazard:** OS Open UPRN also carries British National Grid easting/northing. Feeding BNG values is silently rejected by the range guard (`Invalid` → geo omitted), collapsing coverage — the resolver must select the WGS84 columns. Do **not** add DoHS string-coordinate/CRS handling on this path; if DoHS coords are later used as a supplement, add a separate guarded branch.

Pick **one** owner of the OS join (upstream harvester **or** the options hook, not both) to avoid double-geocoding. Co-located services at one UPRN correctly share one coordinate — `geo` cannot disambiguate them; consumers rely on `name`/operator.

---

## 5. VODIM / quality

The `TransformationReport` becomes the honest instrument that reveals an NHS feed's sparsity — but it will read as a mass data-quality failure unless three awarenesses are added:

- **Skipped-defunct tally.** The liveness gate (below) removes defunct records *before* emission, dropping their `FieldMappingRecord`s, so survivors' `Valid%` inflates by survivorship. Thread a `skipped (defunct)` counter through `TransformationResult`/`RunCommand` into `VodimReporter.BuildSummaryText`.
- **Geocode-precision provenance.** `MapGeoCoordinates` scores presence + range only, so a postcode-centroid fallback scores `Valid` identically to a UPRN rooftop point. Carry a precision marker (e.g. `additionalProperty[geoPrecision]`) and record resolved coordinates as `Default`/`Other` with a note ("resolved from UPRN via OS Open UPRN; not in ODS source") so the report reflects provenance honestly.
- **Source-profile annotation.** Structurally-unsourceable `Missing` (NHS-limited: eligibility/cost/languages/schedules) should be annotated distinctly from publisher-omission `Missing`, so the sparse feed does not read as a publisher failure.

**Reporting bug to fix regardless of ODS:** VODIM records `location.uprn → Place.identifier[UPRN]` as `Valid` whenever the scalar is present (line 488), even though that scalar is never emitted — only `external_identifiers[UPRN]` reaches output. There are also **two** rows aimed at the identifier target (`location.uprn` line 488 and `location.external_identifiers` line 512); the "emit from `location.uprn` when the external one is null" fix must reconcile them to avoid double-counting UPRN coverage. Separately, `mapping.md` §10 line 224 specifies `location.uprn → additionalProperty[uprn]`, but `BuildLocationAdditionalProperties` emits only `usrn` and `locationType` — a standing doc/code gap.

Net scoring signal for an NHS feed: a small set of Place/identity fields (`identifier[UPRN]`, `geo`, `orukStatus`, role keyword) flip `Missing`→`Valid`, while the Service payload drives `Missing%` sharply up. That is the source limitation, correctly surfaced — not a transformer regression.

---

## 6. Transformer change punch-list (ordered)

1. **`TransformationOptions.cs`** — add a UPRN-aware canonical location key (`CanonicalLocationKey` / UPRN-seeded `LocationUri` overload, keep the string overload for fallback); add `ResolveUprnCoordinate` geocoding hook (default null).
2. **`OrukToSchemaOrgTransformer.cs` — `MapLocation`** — seed `Place.@id` from the canonical (UPRN-first) key; wire the direct `OrukLocation.Uprn` scalar into `Place.identifier` (prefer `external_identifiers[UPRN]`, else `location.Uprn`, trimmed); call `ResolveUprnCoordinate` when native coords are null and feed the result through the existing `MapGeoCoordinates`.
3. **`OrukToSchemaOrgTransformer.cs` — `MapService` (lines ~261-264)** — build the Service→Place reference from the **same** canonical key (lockstep) **and** fix the envelope from `{type,id}` to a proper `{"@id":…}` node reference; set `SchemaOrgService.ServiceType` from the selected ODS role display (extend `MapAttributes`' return to yield the label).
4. **Vocabulary registry (`MapAttributes` — `KnownTaxonomyPrefixes` lines 68-72 + `ConstructTaxonomyUri` switch line ~1320)** — add `ods-role` (aliases `ods`, `odsrole`) to **both**; set `inDefinedTermSet` to the full OrganizationRole CodeSystem URI (per-scheme lookup, not `ExtractBaseUri`); add an admin-role skip-set `{RO180, RO99, RO222, RO177}`.
5. **`OrukToSchemaOrgTransformer.cs` — `MapExternalIdentifiers`** — generalise beyond scheme `UPRN` so **ODSCode** is emitted and scored; if both UPRN and ODSCode must coexist, make `SchemaOrgThing.Identifier` a list (`SchemaOrgThing.cs` line 99) or route ODSCode to `additionalProperty`.
6. **`JsonLdMerger.cs` — `Merge`** — replace first-wins/silent-skip for same-`@id` **Place** nodes with a non-null field union (`geo`, `telephone`, `identifier`, `amenityFeature`); leave Service/Organization dedup untouched; at minimum emit a warning.
7. **Liveness gate** — add `TransformationOptions.SkipInactiveStatuses` (default `{defunct, inactive, temporarily closed}`) and an early return in `Transform` (or filter in `RunCommand.ExecuteAsync`); document that the **date** rule (`Operational.Start ≤ today AND (End absent OR End > today)` — **Operational, not Legal**) runs in the NHS→ORUK harvester and arrives as `service.status`, since `OrukService` has no Operational/Legal date fields.
8. **`Vodim/*`** — surface the skipped-defunct counter in `VodimReporter.BuildSummaryText`; add geocode-precision classification; reconcile the duplicate `location.uprn` identifier rows; add a source-profile annotation for structurally-unsourceable `Missing`.
9. **Ingestion adapter (upstream, not the transformer)** — attach synthesised `OrukTaxonomyTerm{Taxonomy="ods-role", Code, Name, TermUri=null}` to the **`OrukService`**; set `service.Organization` to the **RE6 operator**; apply role selection and the Operational-date liveness rule.
10. **Tests** — extend `JsonLdMergerTests` (currently covers org dedup lines 99-114, service first-wins 66-84, distinct-service and null-id) with a shared-premises case: two services at UPRN `100051944949` → **one** Place, **two** Services, **two** Organizations, each `Service.location[].@id` resolving to the single Place.

---

## 7. What does NOT change / carried-over limits

- **No CRS or string→number code** on the OS Open UPRN geo path — coords are already WGS84 `double?` casting to `required decimal`; `SchemaOrgGeoCoordinates.cs` is untouched. The `−90..90`/`−180..180` guard stays as the BNG safety net.
- **`SchemaOrgDefinedTerm` needs no model change** — it already carries `@id`/`name`/`inDefinedTermSet`/`termCode`.
- **Service and Organization nodes must stay distinct** under UPRN dedup — only the **Place** `@id` scheme changes. One UPRN → many Services → possibly different operating orgs.
- **Premises owner stays unrepresented** — Schema.org `Place` has no owner slot; only the per-service RE6 operator is emitted, so owner-vs-operator collapses to operator-only (accepted).
- **Provider-site (RC2) service type stays trapped in free-text `Name`** — no coded `additionalType`, no automatic `keywords`; name-parsing/NLP is out of scope.
- **The Service payload remains sparse** — eligibility, cost, languages, schedules, application and assurance stay `Missing`; that is a DoHS source limit, not a transformer fix.
- **Role `@id`s are non-dereferenceable**, and ODS role codes are **not SNOMED** — future FHIR `HealthcareService.type` output is blocked to `CodeableConcept.text` without an ODS→SNOMED `ConceptMap`; Schema.org output is unaffected.
- **UPRN coverage is partial** — defunct/older ODS records lack a UPRN, so the graph mixes UPRN-seeded and location-id-seeded `Place.@id`s; the fallback must be deterministic for stable re-runs, and the unmatched tail keeps `Place.identifier[UPRN]` `Missing` (a Bronze-conformance risk set).

---

*Produced by a grounded multi-agent analysis (5 impact dimensions, each adversarially verified against the actual transformer source, Schema.org models, and mapping docs; verifier corrections folded in). Companion to [nhs-dohs-comparison.md](nhs-dohs-comparison.md).*
