# Geocoding from the ODS APIs — precision and the third-link question

Question: does the ODS FHIR API give precise geocoding, or do we need a third link to
the GeoPlace/OS APIs? **Answer: the ODS APIs give no coordinates at all — only a UPRN.**
The UPRN is a *pointer* to a precise property-level coordinate, so a third link (or a local
UPRN→coordinate table) is required to obtain the point. Verified live 2026-07-07.

## What each NHS surface actually returns

| API | Address | UPRN | Coordinates (lat/long) |
|-----|---------|------|------------------------|
| **ODS Terminology – FHIR API (R4)** | Yes (`address.line/city/postalCode/country`) | Yes (UK Core `AddressKey` extension) | **No** — no `position`, no lat/long; there is no `/Location` resource and FHIR `Organization` has no `position` element (confirmed in sample **and** OAS `581286`) |
| **ODS ORD API** | Yes (`GeoLoc.Location.AddrLn1…PostCode`) | Yes (`GeoLoc.Location.UPRN`) | **No** — `GeoLoc.Location` has no easting/northing or lat/long |
| **DoHS search API** | Yes | **No** | **Yes** — `Latitude`/`Longitude` (strings) + `Geocode` (GeoJSON Point + CRS); precision/provenance **unspecified** |

So the ODS side (the authoritative source we use for UPRN) is **not** a geocoder. Coordinates
must come from one of:

## Options to get a coordinate

1. **Resolve UPRN → coordinate via OS/GeoPlace (recommended).** Because we already hold the
   authoritative **UPRN**, this is an **exact, property-level** lookup — not fuzzy address/
   postcode matching.
   - **OS Open UPRN** — free, **Open Government Licence**, bulk dataset of *every* UPRN with
     British National Grid easting/northing **and** WGS84 lat/long. Can be held as a **local
     join table**, so the "third link" is an **offline lookup, not a runtime API dependency**
     (no per-record latency/cost/rate-limit). It contains UPRN+coordinate only (no address).
   - **OS Places API** — live REST; accepts a UPRN and returns address + coordinate in a
     chosen CRS (EPSG:4326/27700/…). Needs an OS Data Hub key (free for public sector under
     PSGA). Use it if you also need address↔UPRN matching for the unmatched tail.
2. **Take coordinates from the DoHS search API** (keyed by the same `ODSCode`) — avoids OS,
   **but**: (a) DoHS covers only the "services near you" subset, so the ODS-only records that
   matter most here (RC2-as-service, physio/dental sites) are **unlikely to be present**, and
   (b) DoHS coordinate precision is **undocumented** (it carries a CRS, so it's real geocoding,
   but premises- vs postcode-level is unstated — verify against a known UPRN coordinate).

## Why UPRN-based geocoding is the precise path

- A **UPRN coordinate is property-level** (metres) — the definitive point for that specific
  address, assigned by the local authority and coordinated by GeoPlace.
- A **postcode centroid** (e.g. postcodes.io/ONSPD) is the fuzzy fallback: a postcode can span
  many premises, so the centroid can sit tens–hundreds of metres from any given property.
  (Example: `L14 3PE` centroid is `53.411167, -2.897989` — one point for the whole postcode,
  not RBQ's building.) Only worth using when no UPRN is available.

## Recommendation

Geocode from the **UPRN we already source from ODS**, using **OS Open UPRN as a local
lookup table** (free, exact, no runtime third call). Reserve the OS Places API for the
unmatched tail (address→UPRN). Treat DoHS `Latitude`/`Longitude` as an opportunistic
supplement for the DoHS-listed subset only, after confirming its precision. Net: a precise
coordinate is achievable, but **not from the ODS FHIR API directly — it needs the UPRN→OS
resolution step.**
