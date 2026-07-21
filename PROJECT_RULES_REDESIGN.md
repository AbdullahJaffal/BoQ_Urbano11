# Project Rules Redesign — «Proje Kurulumu (DWG)» → per-network calc rule source

Status: **LOCKED design, implementation in progress.** Replaces the old named-`ProjectTemplate`
system on the "Proje Kurulumu (DWG)" tab with a per-network, project-scoped rule source that
becomes the input for the BoQ calc when the new mode is active.

## Governing principle

Type resolution in the calc gains a **project-level exclusive mode switch** (`HESAP_MODU`, stored
in the DWG):

- `TYPE_MAPPING` (current behavior — unchanged): `TypeMappingStore` links + `MasterPipeRules`
  from the catalog. This is the default for any DWG that has no mode flag.
- `RULES` (new): `TypeMappingStore` is fully ignored; types resolve from a **project-scoped,
  per-network, editable rule set** plus a CAD-selection exception layer.

No new geometric/math formulas. The existing calc engines (`ManholeAIService`, trench/overlap
logic) are unchanged — only the **source of their inputs** changes.

## Locked decisions

1. **Page structure:** full replacement. The page shows the DWG's real networks (`SystemName`);
   each network has its own rules. No named templates.
2. **Mode toggle:** a single project-wide exclusive switch (RULES ⇄ TYPE_MAPPING). The inactive
   page is visually disabled.
3. **Exception conflict:** detected **within one dimension only**. Pipe-family exception on a pipe
   that already has a pipe-family exception → warning (replace vs keep). A pipe-family exception +
   a pipe-excavation exception on the same pipe → no conflict.
4. **Rule source direction:** catalogs (`MasterPipeRules` in Akıllı Montaj, PipeTrench,
   ManholeExcavation) become **import-only** when RULES mode is active; the real editable copy
   lives in the DWG project. TYPE_MAPPING mode still reads catalogs directly.
5. **Piece exclusions:** specific piece heights/lengths (e.g. Gövde 200/250/500) via a multi-select
   list per role.
6. **Excavation rule granularity:** one excavation rule per diameter range, as defined in the
   excavation rules.
7. **Persistence & portability:** every rule is saved inside the DWG (NOD). Everything **except**
   the AG_GUID-specific per-pipe/per-manhole exceptions can be exported/imported as an XML file
   (portable project-rules template).

## Data model (project-scoped, in NOD under `URBANO_BOQ`)

New subkey `PROJE_KURALLARI`, plus project-level `HESAP_MODU`.

```
ProjectRuleSet                       // whole DWG
  CalcMode : RULES | TYPE_MAPPING    // the exclusive switch

  NetworkRules : List<NetworkRule>   // one per active SystemName

NetworkRule
  SystemName
  ── pipes (network default) ──
  PipeFamilyId, PipeSinif            // koruge + SN8 → every DN resolves to PipeDefinition(Family,Sinif,DN)
  ── manholes (network default) ──
  ManholeFamilyId                    // e.g. precast
  ConnectionRules : List<PipeRangeRule>   // project copy of "baca seçim kuralları"
  PieceExclusions : {Role -> allowed heights/lengths}   // e.g. Gövde {200,250,500}
  ── excavation ──
  PipeTrenchRule   : project copy of "boru hendek kuralları" (one rule per diameter range)
  ManholeExcavRule : project copy of "baca kazı kuralları"

ProjectExceptions                    // keyed by AG_GUID, separated by dimension (NOT XML-exported)
  PipeFamilyEx    : AG_GUID -> {PipeFamilyId, Sinif}
  ManholeFamilyEx : AG_GUID -> {ManholeFamilyId}
  PipeExcavEx     : AG_GUID -> {trench override}
  ManholeExcavEx  : AG_GUID -> {manhole excav override}
```

Reuses existing `PipeRangeRule` / `DepthTierRule` / `ComponentConstraints` (deep-copied on
import). Exception dimensions are independent (matches decision 3). Storage follows the
`ManholeAssignStore` Xrecord-under-subdict pattern, with a schema version for forward-compat.

## UI (inside `ProjectSettingsWindow`, replaces the "Proje Kurulumu (DWG)" tab)

- **Top bar:** active-mode indicator + exclusive `RULES ⇄ TYPE_MAPPING` switch. Also shown on the
  Tür Eşleştirme tab; the inactive tab is visually disabled. XML export/import buttons.
- **Left:** list of real networks (`SystemName`) read from the active DWG (same source as
  "Ağ Seçimi"). No named templates.
- **Right (per selected network):** four sub-sections:
  1. **Boru** — Aile combo → Sınıf combo (mirrors the existing Tür Eşleştirme Aile→Sınıf→Boru chain).
  2. **Baca** — family combo + `ConnectionRules` grid (import/delete/edit ranges & heights) +
     `PieceExclusions` multi-select panel.
  3. **Kazı** — import + edit `PipeTrenchRule` and `ManholeExcavRule` for the project.
  4. **İstisnalar** — the exception table with add/remove.

Each section has an **"import from catalog"** button (deep-copy from the current catalogs).

## Exceptions (reuses `UT_MANHOLE_ASSIGN` selection logic)

- "+ İstisna" button → temporarily hide the Proje Ayarları window → pick entities in the drawing
  using the same `ManholeAssignCommand` logic (extract `AG_GUID`/`AG_LAB_ENTITY`, dedupe) →
  restore the window → dialog to choose the override value.
- **Conflict check (decision 3):** for each selected AG_GUID, if a prior exception exists in the
  **same dimension**, warn: "this element already has an exception (X). Replace with the new one,
  or keep the old and ignore the current pick?". Different dimensions on the same element are
  accepted silently.
- Storage: `Merge`-style (last assignment replaces — here gated by the user's answer to the warning).

## Calc integration

One branch at the start of Parse, on `CalcMode`:

- **Pipes** (`BoQParserService` ~ line 888): instead of `TypeMappingStore.FindPipeLink(pprGuid)`,
  RULES mode resolves PozNo/Sinif/Aciklama from the `NetworkRule` (Family+Sinif+DN →
  `PipeDefinition`), applying `PipeFamilyEx` first if present.
- **Manholes** (`ManholeAIService`): instead of `catalog.MasterPipeRules`, pass the network's
  project-copy `ConnectionRules` + chosen family, with `PieceExclusions` constraining the candidate
  pools, applying `ManholeFamilyEx`.
- **Excavation:** feed the project-copy `PipeTrenchRule`/`ManholeExcavRule` with the excavation
  exceptions.
- **TYPE_MAPPING:** bypasses all of the above; calls the current path verbatim.

## Compatibility & migration

- DWG with no `HESAP_MODU` → defaults to `TYPE_MAPPING` (today's behavior, zero breakage).
- Old `ProjectTemplate` / `ProjectTemplateNodManager` structures: read-only or removed after
  confirmation (no templates in the new design).
- Linking-completeness warnings (`[WARN] … Tür Eşleştirme'de bağlı değil`) are replaced in RULES
  mode by "network has no family/class selected" / "pipe has no matching rule" warnings.

## Implementation phases (each AutoCAD-testable milestone flagged for the user)

1. Data models + NOD store + XML import/export + mode enum. (build-only, no AutoCAD test)
2. New page UI: network list + mode toggle + per-network pipe family/sinif + manhole family, wired
   to the store. **(AutoCAD test: persistence round-trip)**
3. Calc integration for RULES mode (pipes + manholes) behind the switch. **(AutoCAD test: compare
   RULES vs TYPE_MAPPING output)**
4. Exceptions (selection reuse + per-dimension conflict check). **(AutoCAD test)**
5. Excavation rules + PieceExclusions. **(AutoCAD test)**
6. Migration/cleanup: remove the template system.
```
