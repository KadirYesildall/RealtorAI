# T160 verification: CRMLS rule profile and RESO lookup seed

> **DRAFT. Waiting for product-owner approval.** This is a research record only. Nothing here
> has changed the database, the RESO seed, or any `verified` flag or `verified_at` column.
> Every value marked CONFIRMED or DIFFERS is a *proposal*. It takes effect only through a
> reviewed T160 migration and a reviewed seed edit, made after approval.

- **Task:** T160 (tasks.md:324). Research R2 (CRMLS profile), R3 and R35 (RESO seed).
- **Researched:** 2026-11-16
- **Repo state:** branch `002-ai-listing-launch` at `4b9bb36`
- **Code read:** `supabase/migrations/0010_mls_rule_profiles.sql`,
  `Libs.Compliance/MlsRuleEngine.cs`, `Libs.Compliance/PhotoIntegrityChecker.cs`,
  `Api/Compliance/ComplianceService.cs` (`ToRules`), `Libs.Reso/ResoLookups.cs`,
  `Libs.Reso/ResoNames.cs`, `Libs.Reso/Lookups/dd-2.0-slice-a.json`. `Libs.Mls` holds only an
  empty `Class1` and asks nothing of CRMLS.

**Source rule.** Only documents published by CRMLS (crmls.org and its subdomains: `go.`, `kb.`,
`blog.`, `devdocs.`) or by RESO (`dd.reso.org`, `reso.org`) are cited. Some copies of the
CRMLS rulebook are hosted by member associations. They were used only to learn that a
2026 edition exists, and none is cited.

**Status meanings.** **CONFIRMED**: the repo value matches the official one. **DIFFERS**: an
official value was found and the repo value is different. This includes repo values that are
NULL or empty, where the cell gives the value that would fill them. **NOT FOUND**: no single
current official value was found (the row says where I looked). **NOT PUBLIC**: the value sits
behind a CRMLS login or API account.

---

## 1. Summary

### 1a. CRMLS rule profile (`mls_rule_profiles` row `CRMLS`, plus the CRMLS rules the code applies)

| # | Value | Repo today | Official finding | Status |
|---|---|---|---|---|
| C1 | `public_remarks_max_chars` | NULL | Update API rule 176 rejects at 1,500 or more (so 1,499 max). Snapshot dated 2019. A 2021 CRMLS report says Paragon "will remain at 4,000". | **NOT FOUND** (dated sources disagree) |
| C2 | `private_remarks_max_chars` | NULL | Update API rule 173 rejects at 1,500 or more (2019). Same 2021 Paragon note of 4,000. | **NOT FOUND** (dated sources disagree) |
| C3 | `photo_min_width` | NULL | No minimum pixel width published | **NOT FOUND** |
| C4 | `photo_min_height` | NULL | No minimum pixel height published | **NOT FOUND** |
| C5 | `photo_min_count` | NULL | At least 1 photo, showing a substantial part of the exterior, within 2 calendar days (Rule 11.5.1) | **DIFFERS** (NULL vs 1) |
| C6 | `photo_max_count` | NULL | 75 per listing (CRMLS KB 2025; Photo FAQ 2026). A 2020 Flexmls FAQ said there was no limit. | **DIFFERS** (NULL vs 75) |
| C7 | forbidden `phone_number` | blocking | Prohibited in public remarks | **CONFIRMED** |
| C8 | forbidden `email_address` | blocking | Prohibited | **CONFIRMED** |
| C9 | forbidden `url` | blocking | Prohibited ("links to external websites of any kind") | **CONFIRMED** |
| C10 | forbidden `agent_name` | blocking | Prohibited | **CONFIRMED** |
| C11 | forbidden `brokerage_name` | blocking | Prohibited | **CONFIRMED** |
| C12 | Other prohibited public-remarks content | not modelled | Showing instructions, open houses, compensation, gate/lockbox/security codes, occupancy, FSBO, agent-directed text, non-English, and more | **DIFFERS** (categories missing) |
| C13 | `required_fields` | `[]` | About 180 *conditional* SET_REQUIRED rules for Property in the Update API (2019 snapshot) | **DIFFERS** |
| C14 | `allowed_alteration_types` | `["virtual_staging"]` | Rule 11.5.2 allows furniture and appliance alterations with disclosure. It also allows real-property alterations when the property will be improved to match by close. | **DIFFERS** (repo is narrower) |
| C15 | `disclosure_labels` | `[]` | Rule 11.5.2 lists "Photo Modified", "Virtually Staged", "Digitally Altered", "AI Altered", "or some accurate substantially similar term" | **DIFFERS** (also needs T161) |
| C16 | Where the label goes | per-photo `DisclosureLabel` | In "the text field section for the altered photo". No watermark needed. | **CONFIRMED** |
| C17 | Original placed next to the altered photo | order difference of exactly 1 | "immediately before or after the digitally altered image" | **CONFIRMED** |
| C18 | Rule citation "11.5.x" (R2) | unverified | Rule **11.5.2**, effective 1 Jan 2026. The old 11.5.2 is now 11.5.3. | **CONFIRMED** |
| C19 | Severity of branding in photos | warning (R36) | Rule 11.5(e): photo removed, citation with immediate fine, no warning notice | **DIFFERS** |
| C20 | `enumerated_field_lookups` | `{}` (no restriction) | CRMLS add/edit uses local short codes, and 29 of the 108 seed values have no CRMLS counterpart | **DIFFERS** |
| C21 | `rulebook_version` / full rulebook text | NULL | go.crmls.org rules pages could not be retrieved (bot protection or script rendering) | **NOT FOUND** |
| C22 | Live listing-input metadata (current lookups and rules) | — | Needs a CRMLS API account and its Welcome Package | **NOT PUBLIC** |

**CRMLS counts:** CONFIRMED 8 · DIFFERS 8 · NOT FOUND 5 · NOT PUBLIC 1. Total 22.

### 1b. RESO Data Dictionary seed (`Libs.Reso/Lookups/dd-2.0-slice-a.json`)

| Item | Count | Status |
|---|---|---|
| `dataDictionaryVersion: "2.0"`: DD 2.0 exists, approved 23 Oct 2023 | 1 | **CONFIRMED** |
| Field-to-lookup bindings for the 6 enumerated fields | 6 | **CONFIRMED** |
| Seed lookup values matching a DD 2.0 Legacy OData Value exactly (all Active) | 101 of 108 | **CONFIRMED** |
| Seed lookup values with no exact DD 2.0 match | 7 of 108 | **DIFFERS** |
| `PublicRemarks` and `PrivateRemarks` exist in DD 2.0 (R3) | 2 | **CONFIRMED** |
| `ResoPropertySubType.Residential` (`ResoNames.cs`, outside the seed but the same spelling) | 10 of 10 | **CONFIRMED** |

**The seven DIFFERS values:**

- **InteriorFeatures**
  - `BuiltinFeatures` should be `BuiltInFeatures` (capital I).
  - `WalkInShower` is not a DD 2.0 value. It is not in DD 2.1 either.
- **PoolFeatures:** `Outdoor` should be `OutdoorPool`.
- **View**
  - `Creek` should be `CreekStream`.
  - `Park` should be `ParkGreenbelt`.
  - `Trees` and `Woods` are one DD value, `TreesWoods`.

### 1c. Most important findings

1. **Turning on `verified_at` today would let required fields pass silently.**
   `MlsRuleEngine.Incomplete` checks only the six numeric columns. It does not treat an empty
   `required_fields` as missing, and 0010 seeds `[]`. With `verified_at` set, the per-field
   loop runs over nothing, so "required-field presence" (FR-019) passes for every listing. Set
   `verified_at` only after one of two things happens: `required_fields` is populated (C13),
   or the engine treats `[]` as incomplete. The second is a code change, outside this
   research task.
2. **The 1,500 figure is an exclusive bound.** If approved, store **1,499**. The CRMLS rule
   is `STRLEN(.ENTRY.) < 1500`, and the engine raises its finding when `text.Length > max`.
   It is also not established that CRMLS's `STRLEN` counts what .NET `string.Length` counts:
   UTF-16 code units, with CRLF as 2.
3. **Rule 11.5.2 names the labels.** CRMLS's own rule gives four label terms and says where
   they go. That is enough for the *CRMLS* side of `disclosure_labels`. T161 still has to
   cover the AB 723 statute (Bus. & Prof. Code), which this research did not review, so C15
   should be approved together with T161, not under T160 alone.
4. **The CRMLS lookup restriction is large.** CRMLS's add/edit vocabulary is a set of local
   codes (`SFR`, `GD`, `PK`...), not RESO legacy names. 29 of the 108 seed values have no
   CRMLS counterpart, for example `Hardwood`, `Dryer`, `Washer`, `KitchenIsland`,
   `WalkInClosets`, `VaultedCeilings` and `Beach`. With `enumerated_field_lookups = {}`,
   Story 3 will offer these values as permitted for CRMLS.
5. **Branding in photos is a no-warning citation at CRMLS.** The code raises it as a warning
   (R36, Story 2 scenarios 4 and 5).

---

## 2. Sources

| Ref | Document | Publisher | Version / date | URL |
|---|---|---|---|---|
| S1 | Digitally Altered Images (New Rule 11.5.2) FAQs, which contain the full rule text | CRMLS | "Updated February 13, 2026" | <https://go.crmls.org/wp-content/uploads/2026/02/2026_Digitally_Altered_Images_FAQs_v1.pdf> |
| S2 | CRMLS Compliance FAQs | CRMLS | "Updated May 19, 2026" | <https://kb.crmls.org/wp-content/uploads/2021/09/2026_Compliance_FAQs_May.pdf> |
| S3 | Photographs and Media FAQs | CRMLS | "Updated May 18, 2026" | <https://kb.crmls.org/wp-content/uploads/2019/04/2026_Photographs_and_Media_FAQs.pdf> |
| S4 | For Photographers Only: How to Input Photos Onto CRMLS Matrix Listings | CRMLS KB | "Updated on April 15, 2025" | <https://kb.crmls.org/knowledgebase/for-photographers-only-how-to-input-photos-onto-crmls-matrix-listings/> |
| S5 | flexMLS Compliance FAQs: Photographs and Media | CRMLS | uploaded 2020-01 (no date printed) | <https://go.crmls.org/wp-content/uploads/2020/01/flexMLS_Compliance-FAQs-PhotographsAndMedia.pdf> |
| S6 | CRMLS Web API developer docs: Update > Rules (rule definitions, field definitions, lookups) | CRMLS | no version printed. HTTP `Last-Modified: Tue, 26 Nov 2019` on every page fetched | <https://devdocs.crmls.org/update/rules> |
| S7 | CRMLS Broker Report: April 2021 | CRMLS blog | 30 Apr 2021 | <https://blog.crmls.org/?p=1456> |
| S8 | December 2025 Broker Report | CRMLS blog | 30 Dec 2025 | <https://blog.crmls.org/brokers/december-2025-broker-report/> |
| S9 | RESO Data Dictionary 2.0 (lookup and field pages) | RESO | "DD 2.0 was approved on October 23, 2023". Pages state per-item revision dates. | <https://dd.reso.org/DD2.0/> |

S6 is CRMLS's official documentation for listing input through its RESO Web API (Add/Edit), the
path Slice B will submit through. It is also **seven years old**. CRMLS says the current rules
"can be downloaded" by API account holders (C22).

---

## 3. CRMLS values in detail

### C1 `public_remarks_max_chars`: NOT FOUND (dated sources disagree)

- **Repo:** NULL (0010). Used by `MlsRuleEngine.CheckRemarks` and by content generation (R35).
- **S6**, rule 176, `property-rules/public-remarks/accept`
  (<https://devdocs.crmls.org/update/rules/rule-definitions/property-rules/public-remarks/accept>):
  > "PublicRemarks must be less than 1500 characters." … `(STRLEN(.ENTRY.) < 1500)`
- **S6**, field definitions (`/update/rules/field-definitions/Property`):
  `PublicRemarks — Data Type: Character Maximum Length: 4000`. The column holds 4,000. The
  rule caps input below 1,500.
- **S7** (Paragon section, 2021): the proposal "Limiting Public Remarks, Private Remarks, and
  Syndication Remarks to 1,500 characters" is marked CLOSED.
  > "Due to this limitation, character count will remain at 4,000 to mitigate further user impact."
- **S9** (RESO): `PublicRemarks` has "Max Length Suggested 4000". That is a RESO suggestion,
  not a CRMLS limit.
- **Searched with no result:** S2 Q126–129 ("Property Descriptions and Public Remarks") and
  S3, which give no length. The kb.crmls.org "Public Remarks FAQs" article (updated
  2025-05-20 per search index) could not be read in full.
- **Why not CONFIRMED:** the only rule-level figure (1,500 exclusive) is from 2019. A later
  CRMLS publication says 4,000 for Paragon, and nothing current covers Matrix or Flexmls.
  CRMLS needs to confirm which figure applies today to the add/edit path RealtorAI will use.
  If 1,500 is confirmed, store **1,499** (finding 2).

### C2 `private_remarks_max_chars`: NOT FOUND (dated sources disagree)

- **Repo:** NULL.
- **S6**, rule 173, `property-rules/private-remarks/accept`:
  > "PrivateRemarks must be under 1500 characters." … `(STRLEN(.ENTRY.) < 1500)`

  The rule grandfathers unchanged values on listings that are already active. Field
  definition: `PrivateRemarks … Maximum Length: 4000`.
- **S7:** the same Paragon decision as C1 ("will remain at 4,000").
- **Why not CONFIRMED:** the same reasons as C1.
- **Related:** S6 rule 201 sets `SyndicationRemarks` below 1,500 as well. RealtorAI does not
  draft that field.

### C3 `photo_min_width` / C4 `photo_min_height`: NOT FOUND

- **Repo:** NULL. Used by `MlsRuleEngine.CheckPhoto`.
- **Searched with no result:** S2 (whole document, "Photographs & Media" Q24–38) and S3
  (all 15 questions). S4 gives the upload count but no size. S5 has nothing either. In S6,
  the Media field definitions have `ImageWidth`/`ImageHeight` as `Int`, length 8, with no
  rule, and there are no media rule definitions at all (the 530 rule links cover
  property/member/office/open-house only). Web searches for a CRMLS pixel minimum found only
  third-party guides, which are not sources.
- **Note:** FR-019 requires a minimum-resolution check. CRMLS does not appear to publish
  one. The product owner may ask CRMLS directly, or decide that CRMLS has no minimum. That
  decision needs a design choice, because the engine currently fails closed on NULL.

### C5 `photo_min_count`: DIFFERS (NULL vs 1)

- **S2**, p. 14, Q96:
  > "Per Rule 11.5.1, you must upload an exterior photograph that displays a substantial portion of the exterior structure of the property for all listings except the following: Business Opportunity, Registered, and agreements cancelled within two (2) days of entry."
- **S3**, p. 1, Q4:
  > "While at least one (1) photograph must show a substantial portion of the property's exterior, it does not need to be the primary photo."
- **S3**, Q11: Coming Soon listings need one such photo "within two (2) calendar days from entry".
- **S6**, rule 165, `property-rules/photos-count/warning`:
  > "A warning will appear when the PhotosCount is 0."
- **Proposed value:** `photo_min_count = 1`.
- **Gap:** the rule is "one *exterior* photo within 2 days". The profile column holds only a
  count. Room classification could feed an exterior check, but that would be new logic
  outside T160.

### C6 `photo_max_count`: DIFFERS (NULL vs 75)

- **S4** (Matrix, 2025-04-15):
  > "Our system allows for a maximum of 75 images to be uploaded to each listing."
- **S3**, p. 3, Q12 (2026): "up to seventy-five (75) photographs can be added to a registered listing."
- **Conflicting, older:** **S5** (Flexmls, 2020): "There is no limit to the number of
  photographs that can be uploaded to one listing."
- **S6:** `PhotosCount … Maximum Length: 2` (at most 99).
- **Proposed value:** `photo_max_count = 75`, recorded as a system limit, not a rule.
  CRMLS should confirm it applies to API submissions, because S5 shows the limit has varied
  by platform.

### C7–C11 forbidden content in public remarks: CONFIRMED (each)

- **S2**, p. 21, Q126 and Q127 (the same wording in both):
  > "The property description field and any other public remarks field may not include information regarding showing instructions, open houses, agent or brokerage names, phone numbers, website addresses, email addresses, or compensation."
- **S2**, p. 21, continued:
  > "Links to external websites of any kind, including but not limited to agent or broker sites, video tours, virtual showing or open house tools, vendor sites, or offer submissions/applications/auctions are prohibited."
- **S9:** the RESO `PublicRemarks` definition agrees: "any information pertaining to entry
  to the property, the seller and/or tenant, or listing member contact information is
  excluded."
- **Severity:** the repo blocks. CRMLS's API only *warns* (S6 rule 177, "WARNING, 'public
  remarks' field must not contain any phone numbers…"), but the content is a rule violation
  and gets cited. Blocking is stricter than CRMLS and still consistent with it, so no change
  is proposed.
- **Detector patterns:** S6 rule 177 publishes CRMLS's own regexes for phone, email and URL.
  Two of the nine (LogicBlk 4 and 6) are cut off on the page. The repo's patterns are its
  own, and this research did not compare them pattern by pattern.

### C12 other prohibited public-remarks content: DIFFERS (not modelled)

- **S2**, p. 22, Q128:
  > "You may not include information that may compromise the seller's or the property's security. This includes but is not limited to gate codes, lockbox information or combinations, security codes, occupancy status, For Sale by Owner information, or open house details. Branded content will be considered a violation, as well as information directed toward agents or brokers."
- **S2**, Q127: "You may not solicit or invite the public to contact the listing agent,
  broker, or any affiliated-third parties."
- **S2**, Q129: "non-English remarks are not allowed in the Public Remarks field or any other text field."
- **S6** rule 177 also warns on: "child or elderly information", "vacancy related
  information", "occupant schedule", "occupant personal information", and "open house
  information".
- **Repo:** the five detectors only (FR-019's list). Adding categories means new detectors,
  which is code, and new rows in `forbidden_in_public_remarks`. It belongs in a separate task.
  It is recorded here so that approving T160 does not imply full CRMLS remarks coverage.

### C13 `required_fields`: DIFFERS

- **Repo:** `[]`.
- **S6:** 180 `property-rules/*/set-required` rules. Each is conditional on PropertyType,
  PropertySubType, StandardStatus and the user's role. Examples:
  - `list-price/set-required`: "ListPrice is required."
  - `living-area/set-required`: "LivingArea is required for residential and residential lease."
  - `bedrooms-total/set-required`: "BedroomsTotal is required for residential, residential lease and manufactured in park."
  - `view/set-required`: "When property type is residential, residential lease and land, View is required."
  - `pool-features/set-required`: "PoolFeatures is required for residential, residential lease, residential income and manufactured in park."
- **Gaps:**
  - The column is a flat list, so the residential-sale subset would have to be derived rule by
    rule from a 2019 snapshot.
  - The current set is NOT PUBLIC (C22).
  - Some S6 rules use CRMLS-specific fields (`PhotosCount`, `LivingAreaSource`,
    `LotSizeSource`) that are not among the fields Slice A collects.
- See finding 1: `[]` is not treated as missing.

### C14 `allowed_alteration_types`: DIFFERS (repo is narrower)

- **Repo:** `["virtual_staging"]`. The 0010 comment says "CRMLS permits only virtual staging
  to be presented as an alteration". The enum is `virtual_staging | other`, and `other`
  always blocks.
- **S1**, p. 2, Rule 11.5.2 (full text):
  > "'Digitally Altered Image' means an image or photo … that has been altered through the use of photo editing software or artificial intelligence to add, remove, or change elements in the image related to furniture or appliances."

  > "… Subscribers may not add to a listing record any image or photo that has been altered to add, remove, or modify any (i) real property elements including but not limited to flooring, walls, windows, structural elements, cabinetry, paint color, hardscape, landscape, façade, fixtures, or floor plans unless the listed property will be improved to match the altered photo at time of close as part of the listed price, or (ii) elements outside of, or visible from, the property …"
- **S1**, p. 1: "AI-generated landscaping images are not permitted in the MLS".
- **Comparison:**
  - The permitted category is broader than "virtual staging". It covers *removing* or
    *changing* furniture and appliances, not only adding them.
  - There is also a second permitted case: real-property changes that the price includes
    and that will be built by close. The repo blocks that case.
  - Blocking more than CRMLS does is safe, but it blocks lawful listings.
  - The product owner should decide one of two things:
    - `virtual_staging` is read as the whole furniture/appliance category, in which case
      only the 0010 comment and UI wording change.
    - Or a new alteration type is added, which is a schema and enum change outside T160.
- **Unchanged:** RealtorAI itself never alters pixels (Principle VIII.6). These rules cover
  only photos the agent declares were altered elsewhere.

### C15 `disclosure_labels`: DIFFERS (CRMLS terms found; AB 723 review still T161)

- **Repo:** `[]`. Every altered photo is blocked with `photo.label_not_approved`.
- **S1**, p. 2, Rule 11.5.2 (iii):
  > "ensure that the digitally altered image is labeled as "Photo Modified", "Virtually Staged", "Digitally Altered," or "AI Altered," or some accurate substantially similar term in the text field section for the altered photo in the add/edit module."
- **Proposed CRMLS label list:** `["Photo Modified", "Virtually Staged", "Digitally Altered", "AI Altered"]`.
- **Why approval goes through T161:** the rule permits any "substantially similar term", so
  the closed list is a product choice. The statute behind it (AB 723) has its own wording
  and placement requirements, which counsel must confirm (R4). This document reviews only
  CRMLS's rule.

### C16 label placement: CONFIRMED

- **S1**, p. 1:
  > "Does my photographer need to add a digitally altered watermark? No. The image needs to have the term "digitally altered" or one similar in the image description."
- **S4:** "Photos that are not an accurate depiction of the property, such as staging, or
  any HOA amenities must be captioned as such."
- **Repo:** the label is a per-photo text value, not drawn on the image. That matches.
- **Slice B note:** S6 Media has `ShortDescription` (max 50) and `LongDescription`
  (max 1024).

### C17 original placed next to the altered photo: CONFIRMED

- **S1**, Rule 11.5.2 (ii):
  > "ensure that the original, unaltered version is displayed in the listing immediately before or after the digitally altered image"
- **Repo:** `PhotoIntegrityChecker` requires `|original.OrderIndex − photo.OrderIndex| == 1`,
  and the original must not itself be altered. That matches.

### C18 rule citation: CONFIRMED (Rule 11.5.2)

- **S1**, p. 1:
  > "In adherence to CA CB 723, CRMLS has drafted the new Rule 11.5.2 … The previous Rule 11.5.2 has been renamed Rule 11.5.3."

  "CB" is CRMLS's typo for AB.
- **S8:** "effective January 1, 2026".
- **Fines:** S1 says "As of now, there is no fine for violating this rule, however the CRMLS
  Rules Committee will revisit the matter of fines in 2026." Whether the committee has
  changed this was not found.

### C19 branding in photos: DIFFERS (severity)

- **Repo:** photo content flags (people, documents, house numbers, plates, branding) are
  **warnings** (R36).
- **S3**, p. 1, Q2:
  > "Because a branding advertisement occurs concurrently with the media's publication, this violation does not receive a warning notice and CRMLS will issue a citation with an immediate fine."
- **S3**, Q3: branding covers "signs, contact information, identifiable persons, logos,
  slogans, catchphrases, external links/references, team names". Q2: a "For Sale" sign
  "qualifies as 'branding'".
- **Note:** this is a severity in code, not a profile column. It is out of scope for the
  T160 migration and is recorded for the product owner.

### C20 `enumerated_field_lookups`: DIFFERS

- **Repo:** `{}`, meaning "no CRMLS restriction on the RESO lookups".
- **S6**, `/update/rules/Lookups` and `/update/rules/field-definitions/Property`:
  - CRMLS add/edit uses local codes with long values. For example,
    `PropertySubType … SFR, CONDO, COOP, TWNHS …`, and in View, `PK = Park/Greenbelt`.
  - The S6 sample deep-insert payload carries `"PropertyType": "Resi"` and
    `"WindowFeatures": "DPW,TIN,CWC"`.
- **Comparison:** matching each seed value's DD 2.0 Standard Value to a CRMLS long value
  leaves **29 of 108** with no CRMLS counterpart:
  - **InteriorFeatures (11):** `Bookcases`, `BreakfastBar`, `DoubleVanity`, `EatInKitchen`,
    `EntranceFoyer`, `KitchenIsland`, `SmartHome`, `SoakingTub`, `VaultedCeilings`,
    `WalkInClosets`, `WalkInShower`
  - **Appliances (4):** `Dryer`, `StainlessSteelAppliances`, `Washer`, `WineRefrigerator`
  - **Flooring (5):** `Cork`, `Hardwood`, `Linoleum`, `Marble`, `Slate`
  - **ParkingFeatures (5):** `Attached`, `Detached`, `OnStreet`, `Tandem`, `Underground`.
    CRMLS has `Tandem Covered`/`Tandem Garage`/`Tandem Uncovered` and
    `Attached Carport`/`Detached Carport`.
  - **PoolFeatures (1):** `Outdoor`
  - **View (3):** `Beach`, `City`, `Garden`
- **Caveats:**
  - This is a 2019 snapshot, and the current CRMLS lists are NOT PUBLIC (C22).
  - The code-to-RESO translation is Slice B's adapter work.
  - CRMLS also has local values the seed lacks, for example View `Catalina`, `Coastline`,
    `Peek-A-Boo`, and Pool `Association`/`Community`/`Private`. S6 `pool-features/accept`
    requires one of the last three, or `None`.
- **Proposed approach:** populate `{field: {only: [...]}}` from the confirmed overlap only
  after CRMLS confirms its current lists. Until then the Story 3 behaviour should be
  described as "RESO-valid", not "CRMLS-valid".

### C21 rulebook version / full text: NOT FOUND

- CRMLS's rules index (<https://go.crmls.org/rules-and-policies/>) links to "CRMLS Rules and
  Policies" (<https://go.crmls.org/crmls-rules-and-policies/>). Neither that page nor
  `/crmls-rules-and-regulations/` or `/crmls-rules-policy-change-summary/` returned content:
  - Command-line retrieval got a Cloudflare block page.
  - Rendered retrieval got only the page title.
- kb.crmls.org's "CRMLS Rules & Policy Change Summary" (updated 2026-07-09) lists article
  titles but has no rule text.
- The rule numbers cited above (11.5(c), 11.5(d), 11.5(e), 11.5.1, 11.5.2, 11.8, 12.10) come
  from S1–S3, which quote or cite the rulebook.
- **To close:** the product owner downloads the current rulebook PDF from crmls.org in a
  browser. Its version and effective date then go into `rulebook_version`.

### C22 live listing-input metadata: NOT PUBLIC

- S6 `/start`: "please contact … api@crmls.org so that we can create an API account for you.
  You will be sent a 'Welcome Package'". The staging Swagger UI is described as "dependent
  on metadata for different clients (i.e., ClientClass claim value)".
- The current required-field rules, lookups and any media limits for API submission come
  from that account. Slice B's vendor approval (spec, "Vendor approval is a schedule
  dependency") is the natural moment to get them.

---

## 4. RESO seed in detail

**How it was verified:**

1. For each of the six DD 2.0 lookups, I fetched the lookup page on dd.reso.org and then every
   value page linked from it: 312 value pages in all, plus 31 for `PropertySubType`.
2. Each value page gives "Standard Value" and "Legacy OData Value". For example, the City
   Lights page reads "Standard Value City Lights Legacy OData Value CityLights … Status Active".
3. The seed is documented as using legacy OData spellings (R35), so each seed value was
   compared against the Legacy OData Value, case-sensitively. `ResoLookups.Permitted` uses
   ordinal comparison.

### Version and fields: CONFIRMED

- **DD 2.0:** <https://dd.reso.org/DD2.0/about/>, "DD 2.0 was approved on October 23, 2023."
  DD 2.1 is also published on the same site. Moving to it is not proposed here. For the
  seven DIFFERS values, DD 2.1 has the same standard values as DD 2.0.
- **Field-to-lookup bindings:** every field page reads "Simple Data Type String List, Multi
  · Max Length Suggested 1024 · Lookup Status Open with Enumerations".

  | Seed field | DD 2.0 lookup | Field page | Field revised |
  |---|---|---|---|
  | `InteriorFeatures` | `InteriorOrRoomFeatures` | <https://dd.reso.org/DD2.0/Property/InteriorFeatures/> | 6/17/2021 |
  | `Appliances` | `Appliances` | <https://dd.reso.org/DD2.0/Property/Appliances/> | 6/17/2021 |
  | `Flooring` | `Flooring` | <https://dd.reso.org/DD2.0/Property/Flooring/> | 6/17/2021 |
  | `ParkingFeatures` | `ParkingFeatures` | <https://dd.reso.org/DD2.0/Property/ParkingFeatures/> | 6/17/2021 |
  | `PoolFeatures` | `PoolFeatures` | <https://dd.reso.org/DD2.0/Property/PoolFeatures/> | 6/17/2021 |
  | `View` | `View` | <https://dd.reso.org/DD2.0/Property/View/> | 6/17/2021 |

  "Open with Enumerations" means an MLS may add local values. That is what the profile's
  `extra` is for.
- **R3's text fields:**
  - `PublicRemarks`: <https://dd.reso.org/DD2.0/Property/PublicRemarks/>, "Simple Data Type
    String Max Length Suggested 4000", revised 8/25/2023.
  - `PrivateRemarks`: <https://dd.reso.org/DD2.0/Property/PrivateRemarks/>, the same, revised 6/17/2021.
- **`ResoPropertySubType.Residential`:** <https://dd.reso.org/DD2.0/lookups/PropertySubType/>
  (31 values). All 10 match:
  `SingleFamilyResidence, Condominium, Townhouse, Duplex, Triplex, Quadruplex, ManufacturedHome, ManufacturedOnLand, StockCooperative, Cabin`.

### Lookup values, one row per seed value

The last column shows the CRMLS add/edit code with the same long value, from the 2019 S6
snapshot (see C20). It is shown for context, not as a seed check.

#### InteriorFeatures (DD 2.0 lookup `InteriorOrRoomFeatures`: 53 values, all Active) — <https://dd.reso.org/DD2.0/lookups/InteriorOrRoomFeatures/>

| Seed value | DD 2.0 Standard Value | DD 2.0 Legacy OData Value | Status | CRMLS Update API code (2019 snapshot) |
|---|---|---|---|---|
| `BeamedCeilings` | Beamed Ceilings | `BeamedCeilings` | CONFIRMED | `BC` Beamed Ceilings |
| `Bookcases` | Bookcases | `Bookcases` | CONFIRMED | none |
| `BreakfastBar` | Breakfast Bar | `BreakfastBar` | CONFIRMED | none |
| `BuiltinFeatures` | Built-in Features | `BuiltInFeatures` | DIFFERS | `BI` Built-in Features |
| `CathedralCeilings` | Cathedral Ceiling(s) | `CathedralCeilings` | CONFIRMED | `CC` Cathedral Ceiling(s) |
| `CeilingFans` | Ceiling Fan(s) | `CeilingFans` | CONFIRMED | `CF` Ceiling Fan(s) |
| `CrownMolding` | Crown Molding | `CrownMolding` | CONFIRMED | `CRWN` Crown Molding |
| `DoubleVanity` | Double Vanity | `DoubleVanity` | CONFIRMED | none |
| `DryBar` | Dry Bar | `DryBar` | CONFIRMED | `DBAR` Dry Bar |
| `EatInKitchen` | Eat-in Kitchen | `EatInKitchen` | CONFIRMED | none |
| `EntranceFoyer` | Entrance Foyer | `EntranceFoyer` | CONFIRMED | none |
| `HighCeilings` | High Ceilings | `HighCeilings` | CONFIRMED | `HCEL` High Ceilings |
| `KitchenIsland` | Kitchen Island | `KitchenIsland` | CONFIRMED | none |
| `OpenFloorplan` | Open Floorplan | `OpenFloorplan` | CONFIRMED | `OFP` Open Floorplan |
| `Pantry` | Pantry | `Pantry` | CONFIRMED | `PTRY` Pantry |
| `RecessedLighting` | Recessed Lighting | `RecessedLighting` | CONFIRMED | `RECL` Recessed Lighting |
| `SmartHome` | Smart Home | `SmartHome` | CONFIRMED | none |
| `SoakingTub` | Soaking Tub | `SoakingTub` | CONFIRMED | none |
| `StoneCounters` | Stone Counters | `StoneCounters` | CONFIRMED | `STNC` Stone Counters |
| `TrayCeilings` | Tray Ceiling(s) | `TrayCeilings` | CONFIRMED | `TREY` Tray Ceiling(s) |
| `VaultedCeilings` | Vaulted Ceiling(s) | `VaultedCeilings` | CONFIRMED | none |
| `WalkInClosets` | Walk-In Closet(s) | `WalkInClosets` | CONFIRMED | none |
| `WalkInShower` | — | — | DIFFERS (no DD 2.0 value) | none |
| `WetBar` | Wet Bar | `WetBar` | CONFIRMED | `WB` Wet Bar |
| `WiredForSound` | Wired for Sound | `WiredForSound` | CONFIRMED | `SUND` Wired for Sound |

#### Appliances (DD 2.0 lookup `Appliances`: 75 values, all Active) — <https://dd.reso.org/DD2.0/lookups/Appliances/>

| Seed value | DD 2.0 Standard Value | DD 2.0 Legacy OData Value | Status | CRMLS Update API code (2019 snapshot) |
|---|---|---|---|---|
| `Cooktop` | Cooktop | `Cooktop` | CONFIRMED | `ST` Cooktop |
| `Dishwasher` | Dishwasher | `Dishwasher` | CONFIRMED | `DW` Dishwasher |
| `Disposal` | Disposal | `Disposal` | CONFIRMED | `GD` Disposal |
| `DoubleOven` | Double Oven | `DoubleOven` | CONFIRMED | `DO` Double Oven |
| `Dryer` | Dryer | `Dryer` | CONFIRMED | none |
| `Freezer` | Freezer | `Freezer` | CONFIRMED | `FZ` Freezer |
| `IceMaker` | Ice Maker | `IceMaker` | CONFIRMED | `IM` Ice Maker |
| `Microwave` | Microwave | `Microwave` | CONFIRMED | `MW` Microwave |
| `Oven` | Oven | `Oven` | CONFIRMED | `OV` Oven |
| `Range` | Range | `Range` | CONFIRMED | `RA` Range |
| `RangeHood` | Range Hood | `RangeHood` | CONFIRMED | `HOD` Range Hood |
| `Refrigerator` | Refrigerator | `Refrigerator` | CONFIRMED | `RF` Refrigerator |
| `StainlessSteelAppliances` | Stainless Steel Appliance(s) | `StainlessSteelAppliances` | CONFIRMED | none |
| `TanklessWaterHeater` | Tankless Water Heater | `TanklessWaterHeater` | CONFIRMED | `TW` Tankless Water Heater |
| `TrashCompactor` | Trash Compactor | `TrashCompactor` | CONFIRMED | `TC` Trash Compactor |
| `Washer` | Washer | `Washer` | CONFIRMED | none |
| `WaterHeater` | Water Heater | `WaterHeater` | CONFIRMED | `WHU` Water Heater |
| `WineRefrigerator` | Wine Refrigerator | `WineRefrigerator` | CONFIRMED | none |

#### Flooring (DD 2.0 lookup `Flooring`: 39 values, all Active) — <https://dd.reso.org/DD2.0/lookups/Flooring/>

| Seed value | DD 2.0 Standard Value | DD 2.0 Legacy OData Value | Status | CRMLS Update API code (2019 snapshot) |
|---|---|---|---|---|
| `Bamboo` | Bamboo | `Bamboo` | CONFIRMED | `BMB` Bamboo |
| `Brick` | Brick | `Brick` | CONFIRMED | `BRK` Brick |
| `Carpet` | Carpet | `Carpet` | CONFIRMED | `CARP` Carpet |
| `Concrete` | Concrete | `Concrete` | CONFIRMED | `CONC` Concrete |
| `Cork` | Cork | `Cork` | CONFIRMED | none |
| `Hardwood` | Hardwood | `Hardwood` | CONFIRMED | none |
| `Laminate` | Laminate | `Laminate` | CONFIRMED | `LAM` Laminate |
| `Linoleum` | Linoleum | `Linoleum` | CONFIRMED | none |
| `Marble` | Marble | `Marble` | CONFIRMED | none |
| `Slate` | Slate | `Slate` | CONFIRMED | none |
| `Stone` | Stone | `Stone` | CONFIRMED | `STON` Stone |
| `Tile` | Tile | `Tile` | CONFIRMED | `TILE` Tile |
| `Vinyl` | Vinyl | `Vinyl` | CONFIRMED | `VINY` Vinyl |
| `Wood` | Wood | `Wood` | CONFIRMED | `WOOD` Wood |

#### ParkingFeatures (DD 2.0 lookup `ParkingFeatures`: 72 values, all Active) — <https://dd.reso.org/DD2.0/lookups/ParkingFeatures/>

| Seed value | DD 2.0 Standard Value | DD 2.0 Legacy OData Value | Status | CRMLS Update API code (2019 snapshot) |
|---|---|---|---|---|
| `Assigned` | Assigned | `Assigned` | CONFIRMED | `AS` Assigned |
| `Attached` | Attached | `Attached` | CONFIRMED | none |
| `Carport` | Carport | `Carport` | CONFIRMED | `CP` Carport |
| `CircularDriveway` | Circular Driveway | `CircularDriveway` | CONFIRMED | `CIRC` Circular Driveway |
| `Covered` | Covered | `Covered` | CONFIRMED | `COVP` Covered |
| `Detached` | Detached | `Detached` | CONFIRMED | none |
| `Driveway` | Driveway | `Driveway` | CONFIRMED | `DY` Driveway |
| `Garage` | Garage | `Garage` | CONFIRMED | `GAR` Garage |
| `GarageDoorOpener` | Garage Door Opener | `GarageDoorOpener` | CONFIRMED | `GDO` Garage Door Opener |
| `Guest` | Guest | `Guest` | CONFIRMED | `GU` Guest |
| `OffStreet` | Off Street | `OffStreet` | CONFIRMED | `OFFS` Off Street |
| `OnStreet` | On Street | `OnStreet` | CONFIRMED | none |
| `Oversized` | Oversized | `Oversized` | CONFIRMED | `OVS` Oversized |
| `Tandem` | Tandem | `Tandem` | CONFIRMED | none |
| `Underground` | Underground | `Underground` | CONFIRMED | none |

#### PoolFeatures (DD 2.0 lookup `PoolFeatures`: 35 values, all Active) — <https://dd.reso.org/DD2.0/lookups/PoolFeatures/>

| Seed value | DD 2.0 Standard Value | DD 2.0 Legacy OData Value | Status | CRMLS Update API code (2019 snapshot) |
|---|---|---|---|---|
| `AboveGround` | Above Ground | `AboveGround` | CONFIRMED | `AG` Above Ground |
| `Fenced` | Fenced | `Fenced` | CONFIRMED | `FE` Fenced |
| `Fiberglass` | Fiberglass | `Fiberglass` | CONFIRMED | `FB` Fiberglass |
| `Gunite` | Gunite | `Gunite` | CONFIRMED | `GN` Gunite |
| `Heated` | Heated | `Heated` | CONFIRMED | `HT` Heated |
| `InGround` | In Ground | `InGround` | CONFIRMED | `IG` In Ground |
| `Indoor` | Indoor | `Indoor` | CONFIRMED | `IN` Indoor |
| `Infinity` | Infinity | `Infinity` | CONFIRMED | `INF` Infinity |
| `Lap` | Lap | `Lap` | CONFIRMED | `LAP` Lap |
| `Outdoor` | Outdoor Pool | `OutdoorPool` | DIFFERS | none |
| `SaltWater` | Salt Water | `SaltWater` | CONFIRMED | `SLT` Salt Water |
| `SolarHeat` | Solar Heat | `SolarHeat` | CONFIRMED | `SH` Solar Heat |
| `Vinyl` | Vinyl | `Vinyl` | CONFIRMED | `VNL` Vinyl |
| `Waterfall` | Waterfall | `Waterfall` | CONFIRMED | `WFL` Waterfall |

#### View (DD 2.0 lookup `View`: 38 values, all Active) — <https://dd.reso.org/DD2.0/lookups/View/>

| Seed value | DD 2.0 Standard Value | DD 2.0 Legacy OData Value | Status | CRMLS Update API code (2019 snapshot) |
|---|---|---|---|---|
| `Bay` | Bay | `Bay` | CONFIRMED | `BA` Bay |
| `Beach` | Beach | `Beach` | CONFIRMED | none |
| `Canal` | Canal | `Canal` | CONFIRMED | `CL` Canal |
| `City` | City | `City` | CONFIRMED | none |
| `CityLights` | City Lights | `CityLights` | CONFIRMED | `CI` City Lights |
| `Creek` | Creek/Stream | `CreekStream` | DIFFERS | `STM` Creek/Stream |
| `Garden` | Garden | `Garden` | CONFIRMED | none |
| `GolfCourse` | Golf Course | `GolfCourse` | CONFIRMED | `GC` Golf Course |
| `Hills` | Hills | `Hills` | CONFIRMED | `HI` Hills |
| `Lake` | Lake | `Lake` | CONFIRMED | `LA` Lake |
| `Mountains` | Mountain(s) | `Mountains` | CONFIRMED | `MT` Mountain(s) |
| `Neighborhood` | Neighborhood | `Neighborhood` | CONFIRMED | `NE` Neighborhood |
| `None` | None | `None` | CONFIRMED | `NO` None |
| `Ocean` | Ocean | `Ocean` | CONFIRMED | `OC` Ocean |
| `Panoramic` | Panoramic | `Panoramic` | CONFIRMED | `PA` Panoramic |
| `Park` | Park/Greenbelt | `ParkGreenbelt` | DIFFERS | `PK` Park/Greenbelt |
| `Pool` | Pool | `Pool` | CONFIRMED | `PL` Pool |
| `River` | River | `River` | CONFIRMED | `RV` River |
| `Trees` | Trees/Woods | `TreesWoods` | DIFFERS | `TW` Trees/Woods |
| `Valley` | Valley | `Valley` | CONFIRMED | `VA` Valley |
| `Water` | Water | `Water` | CONFIRMED | `WA` Water |
| `Woods` | Trees/Woods | `TreesWoods` | DIFFERS | `TW` Trees/Woods |

---

## 5. What approval would change

Nothing below has been done. Each item names the exact row or entry, and what an approval
would allow a later reviewed change to write.

### 5a. `public.mls_rule_profiles` row `id = 'CRMLS'` (seeded by `0010_mls_rule_profiles.sql`)

A new migration (the next free number after `0019_retention_purge.sql`) would `UPDATE` this one
row. Each column below carries the finding it depends on.

| Column | Today | Value approval would allow | Depends on |
|---|---|---|---|
| `photo_min_count` | NULL | `1` | C5 |
| `photo_max_count` | NULL | `75` | C6 (ideally confirmed by CRMLS for API submission) |
| `public_remarks_max_chars` | NULL | stays NULL until CRMLS confirms. If 1,500 is confirmed, store **`1499`** | C1 |
| `private_remarks_max_chars` | NULL | as above | C2 |
| `photo_min_width` / `photo_min_height` | NULL | stays NULL. No published value | C3 / C4 |
| `forbidden_in_public_remarks` | five detectors, all `blocking` | unchanged (CONFIRMED) | C7–C11 |
| `required_fields` | `[]` | the residential-sale subset of the S6 SET_REQUIRED rules, limited to RESO keys Slice A collects, once the product owner accepts the 2019 snapshot or CRMLS supplies the current set | C13 |
| `allowed_alteration_types` | `["virtual_staging"]` | unchanged value. Only the 0010 comment wording, if `virtual_staging` is read as the whole furniture/appliance category | C14 |
| `disclosure_labels` | `[]` | `["Photo Modified", "Virtually Staged", "Digitally Altered", "AI Altered"]`, **only together with T161's counsel sign-off** | C15 |
| `enumerated_field_lookups` | `{}` | `{field: {only: [...]}}` from the CRMLS overlap in section 4, once CRMLS confirms current lists | C20, C22 |
| `rulebook_version` | NULL | a string naming the sources actually relied on, for example `"CRMLS Rules & Policies <edition, from C21>; Rule 11.5.2 per FAQ 2026-02-13; Compliance FAQs 2026-05-19"` | C21 |
| `verified_at` | NULL | `now()` at migration time, **only after** `required_fields` is populated, or the engine treats `[]` as incomplete | finding 1 |

**Effect of a partial approval.** Suppose `verified_at` is set while C1–C4 stay NULL:

- `MlsRuleEngine.Incomplete` still raises one blocking `mls.profile_incomplete`, now naming
  only `public_remarks_max_chars, private_remarks_max_chars, photo_min_width, photo_min_height`.
- Content generation stays refused (`409 mls_rule_profile_incomplete`, R35), because the
  remarks limits are missing.

Approving C5/C6 alone therefore does not open the gates. It only narrows the
"missing" message.

### 5b. `Libs.Reso/Lookups/dd-2.0-slice-a.json`

| Entry | Today | Change approval would allow |
|---|---|---|
| `fields.InteriorFeatures` | `"BuiltinFeatures"` | rename to `"BuiltInFeatures"` |
| `fields.InteriorFeatures` | `"WalkInShower"` | remove (not a DD 2.0 value) |
| `fields.PoolFeatures` | `"Outdoor"` | rename to `"OutdoorPool"` |
| `fields.View` | `"Creek"` | rename to `"CreekStream"` |
| `fields.View` | `"Park"` | rename to `"ParkGreenbelt"` |
| `fields.View` | `"Trees"`, `"Woods"` | replace both with one `"TreesWoods"` |
| the other 101 values | as is | unchanged (CONFIRMED) |
| `dataDictionaryVersion` | `"2.0"` | unchanged (CONFIRMED) |
| `note` | "UNVERIFIED …" | reword to cite this verification (dd.reso.org, DD 2.0, checked 2026-11-16) |
| `verified` | `false` | `true`, **only after** the seven corrections above are applied in the same reviewed change |

**What `verified: true` would mean.** Only that the seed is valid RESO DD 2.0. It does not
mean CRMLS accepts these values (C20). R35's warning that "no MLS submission (Slice B) may
rely on" the seed should then be reworded to point at the CRMLS mapping rather than at the
seed. That edit to research.md is not part of this draft.

### 5c. Decisions needed from the product owner

1. **C1/C2:** ask CRMLS (api@crmls.org or Customer Care) for the current public and private
   remarks limits on the add/edit path. Is it 1,500 exclusive or 4,000?
2. **C3/C4:** ask CRMLS whether a minimum photo resolution exists. If it does not, decide how
   the profile represents "no minimum" without breaking fail-closed.
3. **C13 / finding 1:** choose between populating `required_fields` from the 2019 snapshot
   now, or making the engine treat `[]` as incomplete until CRMLS supplies the current set.
4. **C14:** decide whether `virtual_staging` covers furniture and appliance removal, and
   whether the improvement-by-close exception should be supported.
5. **C15:** forward the Rule 11.5.2 label list to whoever owns T161.
6. **C19:** decide whether branding in photos should become blocking, which would be a
   separate task.
7. **C21:** download the current CRMLS Rules & Policies PDF in a browser so that its edition
   can be recorded.
