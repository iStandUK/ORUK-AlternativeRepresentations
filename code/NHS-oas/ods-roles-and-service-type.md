# ODS roles as a service-type signal (and its limits)

Follow-up exploring whether ODS **roles** can supply the service/profession type an
ORUK feed needs. Verified live 2026-07-07 via the open ORD API. Evidence:
[`ods-site-roles-physio-dental.csv`](ods-site-roles-physio-dental.csv),
[`ods-site-overloading-elland-road.csv`](ods-site-overloading-elland-road.csv).

## How ODS roles work

- The ORD roles reference (`/ORD/2-0-0/roles`) lists **204 roles** (`RO…` codes). Each is
  flagged `primaryRole` **eligible** or not — **97 can be a primary role, 107 are
  non-primary only** (e.g. the various `… PRESCRIBING COST CENTRE` roles).
- Every organisation/site carries a set of roles; **exactly one is its primary role**,
  the rest are non-primary. **You must read the whole set — the primary role is not
  always the most service-meaningful one.**

## The key finding: two populations

**(1) High-street contractor classes — the role *is* the service.** Here the user's
observation holds cleanly ("an Optical Site is effectively an optician/optometrist"):

| Role | Meaning (≈ ORUK service type) |
|------|-------------------------------|
| `RO167` Optical Site | optician / optometry |
| `RO166` Optical Headquarters | optical chain HQ |
| `RO182` Pharmacy · `RO280` Pharmacy Site · `RO181` Pharmacy Headquarter | community pharmacy |
| `RO110` General Dental Practice · `RO65` Private Dental Practice | NHS / private dentistry |
| `RO76` GP Practice | general practice |

Verified: `A0C1W` M&S Opticians → primary `RO167 Optical Site`; `FA002` Rowlands Pharmacy
→ `RO182 Pharmacy`; `V00003` Crabtree Road Dental Practice → `RO110 General Dental Practice`.

**(2) NHS provider *sites* (RC2) — the role is administrative; the service is only in the
name.** Every physio/dental provider-site probed had an **administrative** primary role and
**no clinical role at all** — the profession lives solely in the free-text `Name`:

| ODSCode | Name (the only place the service appears) | Primary role |
|---------|-------------------------------------------|--------------|
| `5KM11` | PHYSIOTHERAPY SERVICE | `RO180` Primary Care Trust Site |
| `5JEDA` | PHYSIOTHERAPY OUTPATIENTS | `RO180` Primary Care Trust Site |
| `04RAX` | BELPER PHYSIOTHERAPY CLINIC | `RO99` CCG Site |
| `03FAE` | HULL DENTAL ACCESS CENTRE | `RO99` CCG Site |
| `503AY` | NORTH HYKEHAM DENTAL CLINIC | `RO222` Local Authority Site |

There is **no "physiotherapy" role in ODS at all** — physio is never coded, only named.
(All 19 physio/dental sites probed were `RC2`, `primaryRole` ∈ {`RO180`, `RO99`, `RO222`}.)

**(3) Watch the primary/non-primary inversion.** Even where a service role exists, it may be
**non-primary**. A GP surgery's *primary* role is a financial construct, not the service:

- `A81001` The Densham Surgery → **primary** `RO177 Prescribing Cost Centre`, **non-primary**
  `RO76 GP Practice`. The service type is the non-primary role.
- Trusts: `RBQ`/`RX2` → primary `RO197 NHS Trust`, non-primary `RO57 Foundation Trust`.

## Implication for an ORUK feed

- ODS roles give a **clean, coded service/profession type for contractor classes**
  (optical, pharmacy, dental practice, GP) — a real candidate ORUK taxonomy/`Service` type,
  and better than DoHS `OrganisationType`. But the vocabulary is an **ODS role scheme**
  (`RO…`), not ESD/ASCS/SNOMED, so `term_uri` stays unresolved (keywords-level, per §7).
- For **NHS provider sites** (where the physio/dental *overloading* is worst), roles do
  **not** give the service — it must be parsed from unstructured names, or left uncoded.
- Derivation rule: **scan the full role set, prefer the most service-specific role over the
  `primaryRole` flag**; fall back to name text only when no clinical role is present.
