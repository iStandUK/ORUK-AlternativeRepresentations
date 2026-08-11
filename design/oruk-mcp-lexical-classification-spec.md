# Specification: lexical ESD classification as an ORUK MCP server capability

**Version:** 1.0 · **Date:** 21 July 2026 · **Author:** prepared for Nicholas Oughtibridge
**Audience:** Claude Code, implementing against the existing ORUK MCP server
**Companion artifact:** `esd-lexical-rules-v2.json` (the versioned rule pack this spec defines)

---

## 1. Purpose

Add a deterministic, vocabulary-grounded classification capability to the ORUK MCP server: given the free text of an Open Referral UK service record (`name`, `description`, `organization`), return one or more **ESD service type** allocations and a **dominant ESD function**, each carrying a confidence score and full provenance. This is the "tier 1" of the hybrid architecture proven in the July 2026 proof-of-concept work: cheap, fast, explainable lexical rules handle the bulk of records; low-confidence residue is flagged so a calling agent (Claude) can apply richer reasoning only where it is needed.

The capability turns the throwaway classifier used to produce the North Lincolnshire full-feed document into a supported, versioned server feature that any MCP client can call.

## 2. Evidence base

The design was derived from and validated against complete enumerations of the three ORUK v3 feeds configured on this server, captured 21 July 2026 via `enumerate_services`:

| Feed | Records | v1 fallback (NL-tuned rules) | v2 fallback (this spec's pack) |
|---|---:|---:|---:|
| Bristol | 874 | 19.8% | **14.1%** |
| Shropshire | 5,305 | 23.1% | **15.2%** |
| North Lincolnshire | 848 | 10.6% | **8.1%** |
| **Total** | **7,027** | — | **14.2%** |

Findings that shaped the design:

1. **Feeds have distinct linguistic registers.** North Lincolnshire is activity/venue-led ("coffee morning", "toddler group"); Bristol is provider-led ("home care", "independent living", condition charities); Shropshire is dominated by civic and national-charity entries (~170 Neighbourhood Watch schemes, schools, parish councils, condition-specific foundations). A rule pack tuned on one feed overfits: the v1→v2 gap above is the measured cost. Rules must therefore be maintained as **versioned data, not code**, so new feeds can contribute rules without redeployment.
2. **A first-match priority list is sufficient for tier 1.** Distinct-offer bundling ("wellbeing group, older people group, food bank") is real but is tier-2 work; forcing multi-label into the lexical tier produced noise in trials.
3. **The residue is informative.** The ~14% fallback is not noise — it is precisely the queue for LLM classification, and its composition (generic hubs, umbrella charities, sparse descriptions) is itself a data-quality signal worth reporting.
4. **Feed hygiene issues recur** (placeholder `example.com` URLs, duplicate names, near-empty descriptions) and should be surfaced by the same pass, cheaply.

## 3. New MCP tools

Three tools are added to the ORUK server's existing surface (`list_feeds`, `search_services`, `enumerate_services`, `list_taxonomy_terms`, `resolve_taxonomy_label`, `get_service_detail`, …). Names follow the server's existing snake_case convention.

### 3.1 `classify_service_text`

Classify a single service record supplied inline. Stateless; no feed access.

```json
{
  "name": "classify_service_text",
  "description": "Classify a service description against the ESD Service and Function vocabularies using the server's lexical rule pack. Returns allocations with confidence, rule provenance, and a review flag. Use for single records or when the caller already holds the text.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "name":         { "type": "string", "description": "Service name (required)." },
      "description":  { "type": ["string","null"], "description": "Service description free text." },
      "organization": { "type": ["string","null"], "description": "Owning organisation name." },
      "matchMode":    { "enum": ["first-match","all-match"], "default": "first-match",
                        "description": "first-match: highest-priority rule wins (tier-1 behaviour). all-match: return every matching rule, deduplicated by ESD service code, for multi-label exploration." }
    },
    "required": ["name"]
  }
}
```

**Response** (`content` JSON):

```json
{
  "allocations": [
    {
      "esd_service":  { "system": "http://id.esd.org.uk/service",  "code": "1818", "display": "Food banks" },
      "esd_function": { "system": "http://id.esd.org.uk/function", "code": "4",    "display": "Community support" },
      "confidence": 0.9,
      "rule_id": "r001",
      "rule_label": "Food banks",
      "method": "lexical"
    }
  ],
  "review": false,
  "classifier": { "rule_pack": "oruk-esd-lexical-rules", "version": "2.0.0",
                  "service_codesystem_version": "2026-07-08", "function_codesystem_version": "2026-07-08" }
}
```

`review` is `true` when the best allocation's confidence is below the review threshold (§5.4) or only the fallback rule matched.

### 3.2 `classify_feed`

Enumerate-and-classify one feed page. Composes the existing `enumerate_services` pagination contract exactly (same `offset` / `pageSize` / `has_more` / `next_offset` semantics and the same ~10,000-record safety cap), adding classification to each record and an aggregate summary.

```json
{
  "name": "classify_feed",
  "description": "Enumerate one page of a feed (as enumerate_services) and classify every service against the ESD vocabularies. Returns per-record allocations plus a page summary by dominant function. Set summaryOnly to true to receive counts without per-record payloads — recommended for a first pass over large feeds.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "feedUrl":     { "type": "string", "description": "Feed URL, name, or alias from list_feeds. Required." },
      "offset":      { "type": "integer", "minimum": 0, "default": 0 },
      "pageSize":    { "type": "integer", "minimum": 1, "maximum": 500, "default": 100 },
      "matchMode":   { "enum": ["first-match","all-match"], "default": "first-match" },
      "summaryOnly": { "type": "boolean", "default": false,
                       "description": "Return only the aggregate summary and data-quality block; omit per-record results." }
    },
    "required": ["feedUrl"]
  }
}
```

**Response**: the `enumerate_services` envelope, with each service gaining an `allocations` array and `review` flag (as §3.1), plus:

```json
{
  "summary": {
    "classified": 500,
    "review_count": 41,
    "by_function": [ { "code": "80", "display": "Sports and sporting venues", "count": 123 } ],
    "by_theme":    [ { "code": "72", "display": "Leisure and culture", "count": 353 } ]
  },
  "data_quality": {
    "placeholder_urls": 30,
    "duplicate_names": 20,
    "empty_or_short_descriptions": 12,
    "notes": "Counts within this page only; aggregate across pages client-side."
  },
  "classifier": { "rule_pack": "oruk-esd-lexical-rules", "version": "2.0.0" }
}
```

Response-size discipline: a 500-record classified page must stay within the transport budget the server already meets for `enumerate_services`; the added fields are bounded (≤3 allocations per record in first-match mode — see §5.3). `summaryOnly` exists so whole-feed profiling costs one small response per page.

### 3.3 `get_classifier_info`

Introspection, so any client can establish provenance before trusting results.

```json
{
  "name": "get_classifier_info",
  "description": "Return the loaded lexical rule pack's metadata: name, version, rule count, target CodeSystem URLs/versions, evidence feeds, review threshold, and per-rule summaries (id, label, ESD codes, confidence, origin). Never returns raw regex patterns unless includePatterns is true.",
  "inputSchema": {
    "type": "object",
    "properties": { "includePatterns": { "type": "boolean", "default": false } }
  }
}
```

## 4. Data artifacts

### 4.1 The rule pack (`esd-lexical-rules-v2.json`)

The rule pack is a standalone JSON document loaded by the server at startup (path configurable; hot-reload optional). The companion file delivered with this spec is the authoritative v2.0.0 instance, containing **63 rules** (37 v1 `origin:"v1-northlincs"` + 26 v2 `origin:"v2-bristol-shropshire"`) plus the fallback. Schema:

```json
{
  "name": "oruk-esd-lexical-rules",
  "version": "2.0.0",                       // semver; bump minor for rule additions, major for semantics changes
  "generated": "2026-07-21",
  "target_codesystems": {
    "service":  { "url": "http://id.esd.org.uk/service",  "version": "2026-07-08" },
    "function": { "url": "http://id.esd.org.uk/function", "version": "2026-07-08" }
  },
  "evidence_feeds": ["bristol", "shropshire", "northlincs"],
  "match_mode_default": "first-match",
  "fallback": { "id": "r-fallback", "esd_service": "297", "esd_function": "4",
                "confidence": 0.4, "label": "Community support (fallback)" },
  "rules": [
    {
      "id": "r001",                          // stable; never reuse after deletion
      "pattern": "food ?bank|food parcel|…", // ECMA-compatible regex, applied case-insensitively
      "esd_service": "1818",
      "esd_function": "4",
      "confidence": 0.9,
      "label": "Food banks",
      "origin": "v1-northlincs",
      "notes": "optional rationale / evidence"
    }
  ]
}
```

Ordering in the `rules` array **is** priority. Confidence values are hand-assigned priors reflecting rule specificity (0.9+ near-unambiguous phrases like "food bank"; 0.6–0.7 broad cultural/venue language), not calibrated probabilities — §8 covers calibration as future work.

### 4.2 ESD vocabulary source

Displays and code validation come from the FHIR-ESD artifacts already in the ecosystem: `CodeSystem-service.json` (210 concepts) and `CodeSystem-function.json` (176 concepts), both `version 2026-07-08`, generated by the iStandUK/FHIR-ESD converter under OGL v3.0. The server loads both at startup and **refuses to load a rule pack referencing a code absent from the loaded CodeSystems** (fail fast, at startup, not per-request). Function theme roll-up (§5.5) is computed from the CodeSystems' `parent` properties.

## 5. Classification algorithm

### 5.1 Text assembly and normalisation

```
text = join(" . ", [name, description ?? "", organization ?? ""])
text = replace typographic apostrophes (’ → '), collapse whitespace
matching is case-insensitive; no stemming, no tokenisation
```

Boilerplate stripping (e.g. the recurring "During bank holidays, opening times may vary…" preamble seen in openplace feeds) is applied before matching, from a small configurable strip-list carried in the rule pack (optional `strip_patterns` array; absent in v2.0.0 — matching proved robust without it, but the hook is reserved).

### 5.2 Matching

**first-match** (default): walk `rules` in order; the first rule whose pattern matches yields the sole allocation. If none match, emit the `fallback` allocation. This is O(rules × text) with all patterns precompiled at load; the 7,027-record corpus classifies in well under a second on commodity hardware.

**all-match**: collect every matching rule; deduplicate by `esd_service` code keeping the highest-priority instance; cap at 5 allocations; sort by rule priority. Fallback is emitted only when nothing matches.

### 5.3 Allocation shape

Every allocation carries the full FHIR-style triple (`system`, `code`, `display`) for both service and function — displays resolved from the loaded CodeSystems, never stored in the rule pack (single source of truth; a vocabulary version bump updates displays automatically).

### 5.4 Review flag

`review = (best confidence < 0.5) or (only fallback matched)`. The 0.5 threshold is carried in server config, not hard-coded. Callers should treat `review: true` records as the tier-2 queue: re-classify with an LLM (constrained to the ESD ValueSet, per the PoC pattern), or route to a human.

### 5.5 Theme roll-up

`by_theme` in summaries is computed by walking each allocated function's `parent` chain in `CodeSystem-function.json` to its root (13 top-level themes). Cycles are guarded (seen-set); a function with no parent is its own theme.

## 6. Provenance, licensing, versioning

Every response embeds the `classifier` block (rule pack name + semver, both CodeSystem versions). This makes any stored result reproducible and auditable — the same triple (pack version, service CS version, function CS version) always yields the same output for the same input.

Attribution: responses that include ESD displays must be accompanied (in tool description or server docs) by the OGL attribution the vocabularies require: "Contains public sector information licensed under the Open Government Licence v3.0, attributed to the LG Inform Plus programme." The rule pack itself is original work and may carry the server's own licence.

Compatibility rule: a rule pack targeting CodeSystem version X loads against CodeSystem version Y only if every referenced code resolves in Y; otherwise startup fails with a list of orphaned rule ids.

## 7. Acceptance tests

Fixtures are real records from the evidence corpus (match on the given strings; ids shown for traceability).

| # | Input (name / org) | Expect service | Expect function | review |
|---|---|---|---|---|
| 1 | "North Bristol and South Glos Foodbank" / Trussell Trust | 1818 Food banks | 4 | false |
| 2 | "Carers Support Group South" / Alzheimer's Society | 298 Carers support groups | 61 | false |
| 3 | "Neighbourhood Watch Scheme (Longden Village)" | 870 Community safety | 21 | false |
| 4 | "My Homecare Bristol" | 242 Care at home | 148 | false |
| 5 | "St Martins Day Centre" | 296 Day centres | 59 | false |
| 6 | "Rail Travel: 16 to 25 Railcard" | 221 Rail cards | 105 | false |
| 7 | "Oswestry Fencing Club" | 641 Sports clubs and groups | 80 | false |
| 8 | "Terrence Higgins Trust - Bristol" | 202 HIV/AIDS support | 65 | false |
| 9 | "#TalkSuicide" / NE Lincs Mind | 1284 Mental health support | 65 | false |
| 10 | "Birth Registration Appointment" / NL Family | 1716 Registration | 54 | false |
| 11 | "Bonby Village Hall Coffee Mornings" | 1806 Social clubs and groups | 75 | false |
| 12 | "Elijah's Hope CIC" (generic description) | 297 fallback | 4 | **true** |

Corpus-level gates (run against the three stored enumerations, or live feeds accepting drift):

- fallback rate ≤ 16% on each of the three evidence feeds with pack v2.0.0;
- zero allocations whose codes fail CodeSystem resolution;
- determinism: two runs over the same corpus byte-identical;
- `classify_feed` page of 500 including per-record results stays within the transport size budget already met by `enumerate_services`.

## 8. Explicit non-goals and tier-2 hand-off

Not in scope for this capability: multi-intent splitting of bundled descriptions; Need derivation (deterministic via the Service→Need ConceptMap — a natural *separate* tool, `derive_needs(service_codes[])`, once ConceptMap loading lands); Circumstance/cohort tagging (requires retrieval over the 4,069-concept list — tier-2 by construction); confidence calibration against a human-labelled gold set; non-v3 feed normalisation; and HSDS `service_taxonomy` write-back.

The intended operating pattern is: **`classify_feed` (summaryOnly) → `classify_feed` pages → collect `review:true` records → Claude classifies the residue** using `resolve_taxonomy_label`/vocabulary context — exactly the hybrid pipeline of the July 2026 design proposal, with the cheap 85% now server-side.

## 9. Maintenance loop

New feed onboarding: run `classify_feed` in `summaryOnly` mode; if fallback rate exceeds the gate, mine the review residue's vocabulary (the same frequency analysis that produced v2), draft rules, bump the pack's minor version, re-run the corpus gates. Rule authorship remains a human-plus-LLM task; the server only ever executes the versioned pack.

---

*Evidence, thresholds and fixtures in this specification derive from full enumerations of the Bristol (874), Shropshire (5,305) and North Lincolnshire (848) ORUK v3 feeds on 21 July 2026, classified against ESD Service (210 concepts) and Function (176 concepts) CodeSystems v2026-07-08 as converted to FHIR by the iStandUK FHIR-ESD project. ESD vocabularies © LG Inform Plus (Local Government Association), OGL v3.0.*
