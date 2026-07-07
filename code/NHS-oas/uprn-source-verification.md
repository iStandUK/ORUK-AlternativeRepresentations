# UPRN source verification (ODS APIs)

Follow-up checks confirming the two open questions from the DoHS↔ORUK comparison
([`plan/nhs-dohs-comparison.md`](../../plan/nhs-dohs-comparison.md)). Verified live 2026-07-07.

## Q1 — Does an NHS FHIR API actually emit UPRN? **Yes.**

There are two ODS FHIR APIs; only the current one carries UPRN:

| API | OAS | Status | UPRN? |
|-----|-----|--------|-------|
| **Organisation Data Terminology – FHIR API** (R4) | `restapi/oas/581286` | **Current / strategic** | **Yes** — address extension `UKCore-AddressKey`, `AddressKeyType` code `UPRN` |
| Organisation Data Service – FHIR API (STU3) | `restapi/oas/577397` | **Retired in production** ("The STU3 api has now been retired") | Extension defined (`Extension-ODSAPI-UPRN-1`) but not populated in the UAT sample; do not use |

The R4 API returns `Organization` / `OrganizationAffiliation` resources. Open-access
sandbox at `https://sandbox.api.service.nhs.uk/organisation-data-terminology-api/fhir`
(dummy bearer token). Live sandbox results:

| ODSCode | Name | UPRN (FHIR R4) |
|---------|------|----------------|
| `RBQ` | Liverpool Heart and Chest Hospital NHSFT | `38150603` |
| `RHM` | University Hospital Southampton NHSFT | `100062509013` |
| `5EX` | Greater Derby PCT (defunct) | *(none)* |

The R4 UPRN for `RBQ` (`38150603`) is **identical** to the value returned by the ORD
API (`GeoLoc.Location.UPRN`), cross-confirming the two surfaces agree.

## Q2 — Do real DoHS-type (dentist / pharmacy / optician) codes resolve in ODS with UPRN? **Yes.**

The DoHS **sandbox** `V…`/`OP_…` codes are synthetic fixture data and 404 in live ODS
(e.g. `V002072`). **Real** codes of the same record classes resolve and carry UPRN.
Pulled from the open ORD API by primary role (`RO110` dental, `RO182` pharmacy,
`RO167` optical) then looked up individually — see
[`ord-uprn-coverage-probe.csv`](ord-uprn-coverage-probe.csv):

- **21 codes probed, 19 with UPRN.** Only 2 lacked one: `Q69` (old area team)
  and `A1L4S` (one optician).
- Real dentists (`V00003`…), pharmacies (`FA002`…) and opticians (`A0C1W`…) all
  returned UPRNs (e.g. `V00003 → 100062475349`, `FA002 → 100012787936`).
- Sites are modelled as `orgRecordClass RC2` records with their **own** UPRN
  (`RBQ07 → 40074525`), linked to their parent org through an ODS **relationship
  record** (`RBQ07` → `RBQ` via `RE6`), **not** through the code string.

## Note on ODS code format (codes are opaque)

ODS codes must be treated as **opaque identifiers**. Legacy codes vary in shape
(3-char organisations like `RBQ`; historically-suffixed site codes like `RBQ07`;
`F…` dispensing/pharmacy codes; `V…` dental codes). Newer allocations are **5-char
`ANANA`** (alpha-numeric-alpha-numeric-alpha, e.g. `A0C1W`, `C4B2A`, `X6R3V`,
`H8I8M`) — a scheme adopted to expand the available code space. **Do not parse a
code to infer type or an organisation↔site hierarchy** (the historic "site = parent
code + suffix" reading no longer generalises); those relationships come from ODS
**relationship** data (ORD `Rels` / FHIR `OrganizationAffiliation`).

## Conclusion

Both blockers are cleared. UPRN is sourceable from ODS keyed by `ODSCode` — via the
open ORD API today, and via the current R4 Terminology FHIR API (strategic target) —
for the primary-care contractor record classes that dominate DoHS. The residual issue
is **partial coverage** (defunct/older un-restamped records return no UPRN), so a
null-fallback is still required.
