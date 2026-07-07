# One premises, many services, many organisations (shared UPRN)

Worked ODS example showing that a single premises can host multiple services from
multiple organisations — each a distinct ODS record sharing one address and **UPRN**.
Verified live 2026-07-07 via the open ORD API.

## The example

**Premises:** 23 Mill Hey, Haworth, Keighley, BD22 8NQ — **UPRN `100051944949`** (identical on both records).

| ODSCode | Class | Name | Roles | Operated by (via `RE6`) | Operational |
|---------|-------|------|-------|-------------------------|-------------|
| `FWF76` | RC1 | DAY LEWIS PHARMACY | `RO182` **Pharmacy** (primary) | **Day Lewis PLC** (`P05F`); commissioned by NHS West Yorkshire ICB (`QWO`, `RE4`) | 2002-03-19 → (open) |
| `C5I3V` | RC2 | DAY LEWIS PHARMACY HAWORTH – LITTLELANDS – **COVID LOCAL VACCINATION SERVICE** | `RO280` Pharmacy Site (primary) **+ `RO279` COVID Vaccination Centre (non-primary)** | **Rimmington Pharmacy** (`FX417`); previously Cottingley Pharmacy (`FP078`, now inactive) | 2022-04-01 → 2026-09-30 |

So **one premises → two services (dispensing + COVID vaccination) → two different operating
organisations (Day Lewis PLC vs Rimmington Pharmacy)**, two ODS codes, one shared UPRN.

## Why this matters for an ORUK feed

This is precisely the many-to-many that ORUK's `Service`/`ServiceAtLocation`/`Location`
graph exists to express, and that the DoHS *search* API (flat, org-centric) cannot. Mapped:

- **1 `Location`** — keyed on UPRN `100051944949`. **UPRN is the dedup key**: co-located ODS
  records collapse to a single ORUK Location.
- **2 `Service` + 2 `ServiceAtLocation`** — one per ODS record, both pointing at that Location.
- **≥2 `Organization`** — a **different `organization_id` per service**, resolved from each
  record's own relationships (`RE6` → operating org), not assumed one-org-per-premises.

### Consequences / cautions for the ETL

- **Do dedup Locations on UPRN, but never merge the Services or Organizations.** The two
  records are genuinely different services under different orgs; collapsing them would lose
  a real distinction (and misattribute a service to the wrong provider).
- **Resolve the provider per record via relationships** (`RE6` = operated by / part of;
  `RE4` = commissioned by), because the premises "owner" and the service operator can differ
  (the vaccination service here runs under Rimmington, not Day Lewis).
- **Read non-primary roles for the service type** — the COVID vaccination service is the
  *non-primary* role `RO279`; the primary `RO280 Pharmacy Site` is administrative. (Same rule
  as [`ods-roles-and-service-type.md`](ods-roles-and-service-type.md).)
- **Combine with the liveness rule** — `C5I3V` has a future-dated operational end
  (2026-09-30), so it is live now but time-bounded; don't treat "has an end date" as closed.
