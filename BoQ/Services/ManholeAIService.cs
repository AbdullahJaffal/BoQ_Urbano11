using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Services;
using UrbanoMetraj.BoQ.TypeMapping.Services;
using UrbanoMetraj.BoQ.TypeMapping.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Phase 2 — Manhole AI: Smart Topology, Drop-Pipe Filtering, Dynamic Pre-cast Stacking.
    ///
    /// Catalog schema (row-per-part)
    /// --------------------------------
    ///  Nominal_Diameter | Part_Name | Height_m | Is_Mandatory | Is_Variable_Ring
    ///
    ///  Mandatory parts  (Is_Mandatory = Yes) → 1 unit added per manhole regardless of depth.
    ///  Variable rings   (Is_Variable_Ring = Yes) → filled greedily to cover the remaining depth.
    ///
    /// Stacking algorithm (per manhole, pre-cast only)
    /// ------------------------------------------------
    ///  1. Collect mandatory parts → add 1 each → Fixed_Stack_Height = sum of their heights.
    ///  2. Remaining = Depth − Fixed_Stack_Height.
    ///  3. Greedy largest-first fill with variable rings until Remaining ≤ 0.
    ///  4. If Remaining > 0.05 m after all rings, add 1 more of the smallest ring.
    ///
    /// BOM output (system-isolated)
    /// --------------------------------
    ///  BuildBomBySystem() returns one SystemBomGroup per network system,
    ///  preserving the same system order as BoQReport.Systems.
    ///
    /// Drop-Pipe Rule (pre-cast only)
    /// --------------------------------
    ///  An inlet at a manhole is a "drop-pipe" (Selale) when:
    ///      InvertAtManhole  >  (Lowest_Invert_At_Manhole + sum of mandatory heights)
    ///  Cast-in-place manholes never have drop-pipes.
    ///
    /// ASCII-safe strings used throughout (project coding convention):
    ///   O  = diameter symbol (Ø),  Giris / Cikis = Giriş / Çıkış,  Selale = Şelale
    /// </summary>
    public static class ManholeAIService
    {
        /// <summary>
        /// Remaining-height tolerance in metres (user-set 2026-07-04: 3 cm — roughly
        /// half the shortest available Boyun Bileziği height, so greedy largest-first
        /// Gövde filling plus at most one Boyun piece always lands within this margin;
        /// no combinatorial/exact-sum search needed). If the greedy algorithm leaves
        /// a gap smaller than this, no extra ring is added.
        /// </summary>
        private const double LeftoverTolerance = 0.03;

        // =====================================================================
        // 1. Catalog reader  (new row-per-part schema)
        // =====================================================================

        /// <summary>
        /// Reads the Manhole_Catalog.xlsx produced by ManholeConfigService.
        /// Returns a dictionary keyed by NominalDiameter (mm).
        ///
        /// If the path is empty, missing, or unreadable, returns an empty
        /// dictionary — processing continues without stacking data.
        /// </summary>
        public static Dictionary<int, CatalogEntry> ReadCatalog(string path)
        {
            var result = new Dictionary<int, CatalogEntry>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

            try
            {
                using (var pkg = new ExcelPackage(new FileInfo(path)))
                {
                    // Prefer "Manhole_Catalog"; fall back to the first sheet.
                    ExcelWorksheet ws = pkg.Workbook.Worksheets["Manhole_Catalog"]
                                     ?? (pkg.Workbook.Worksheets.Count > 0
                                         ? pkg.Workbook.Worksheets[1] : null);

                    if (ws == null || ws.Dimension == null || ws.Dimension.Rows < 2)
                        return result;

                    // ── Discover column positions from header row ─────────────
                    int colDiam = 1, colName = 2, colHeight = 3,
                        colMand = 4, colVar  = 5;

                    int totalCols = ws.Dimension.Columns;
                    for (int c = 1; c <= totalCols; c++)
                    {
                        string hdr = (ws.Cells[1, c].Text ?? "").Trim();
                        if      (hdr.Equals("Nominal_Diameter",  StringComparison.OrdinalIgnoreCase)) colDiam   = c;
                        else if (hdr.Equals("Part_Name",         StringComparison.OrdinalIgnoreCase)) colName   = c;
                        else if (hdr.Equals("Height_m",          StringComparison.OrdinalIgnoreCase)) colHeight = c;
                        else if (hdr.Equals("Is_Mandatory",      StringComparison.OrdinalIgnoreCase)) colMand   = c;
                        else if (hdr.Equals("Is_Variable_Ring",  StringComparison.OrdinalIgnoreCase)) colVar    = c;
                    }

                    // ── Read data rows ────────────────────────────────────────
                    int totalRows = ws.Dimension.Rows;
                    for (int r = 2; r <= totalRows; r++)
                    {
                        int diam = ParseCellInt(ws.Cells[r, colDiam].Value);
                        if (diam <= 0) continue;  // skip blank / header repetition rows

                        string partName = (ws.Cells[r, colName].Text ?? "Part").Trim();
                        double heightM  = ParseCellDouble(ws.Cells[r, colHeight].Value);
                        bool   isMand   = ParseBool(ws.Cells[r, colMand].Text);
                        bool   isVar    = ParseBool(ws.Cells[r, colVar].Text);

                        if (!result.ContainsKey(diam))
                            result[diam] = new CatalogEntry { NominalDiameter = diam };

                        result[diam].Parts.Add(new CatalogPart
                        {
                            PartName       = partName,
                            HeightM        = heightM,
                            IsMandatory    = isMand,
                            IsVariableRing = isVar
                        });
                    }
                }
            }
            catch
            {
                // Non-fatal — return whatever was collected before the error.
            }

            return result;
        }

        // =====================================================================
        // 2. Main processing pass
        // =====================================================================

        /// <summary>
        /// Runs the full Phase 2 analysis on every ManholeItem in the report.
        /// Mutates ManholeItem in-place (SmartTypeName, Stack, inlet/outlet counts).
        /// Stacking source is our own ComponentFamily catalog (Baca Parça Kataloğu),
        /// resolved per manhole via its Type Mapping link — the old Manhole_Catalog.xlsx
        /// dictionary is no longer consulted (single unified manhole catalog).
        /// </summary>
        public static void Process(
            BoQReport   report,
            BoQSettings settings)
        {
            if (report == null) return;

            int unresolvedCount     = 0;
            int excavUnresolvedCount = 0;
            int steppedIgnoredCount  = 0;
            var constraintViolationNames = new List<string>();
            foreach (var sys in report.Systems)
                foreach (var mh in sys.Manholes)
                {
                    ProcessManhole(mh, report.SectionDebug, ref excavUnresolvedCount, ref steppedIgnoredCount);
                    if (mh.StackPreCast == null) unresolvedCount++;
                    else if (mh.StackPreCast.ConstraintViolated) constraintViolationNames.Add(mh.NodeName);
                }

            if (unresolvedCount > 0)
                report.DiscoveryNotes.Add(
                    $"[WARN] {unresolvedCount} baca için Baca-Boru Bağlantı Kuralı/Taban eşleşmesi bulunamadı — çap ve Prefabrik Malzeme Listesi eksik kalacak.");
            if (excavUnresolvedCount > 0)
                report.DiscoveryNotes.Add(
                    $"[WARN] {excavUnresolvedCount} baca için Baca Kazı Kataloğu'nda uygun kural/derinlik kademesi bulunamadı — kazı hacmi 0 kalacak (sadece ham derinlik gösterilir).");
            if (steppedIgnoredCount > 0)
                report.DiscoveryNotes.Add(
                    $"[WARN] {steppedIgnoredCount} baca için 'Basamaklı Kazı' seçiliydi — bu özellik henüz desteklenmiyor, düz şevli hesap kullanıldı.");
            if (constraintViolationNames.Count > 0)
                report.DiscoveryNotes.Add(
                    $"[WARN] Parça Kısıtları (Min/Maks) nedeniyle hedef derinliğe tam ulaşılamadı veya zorunlu bir parça sayısı sağlanamadı: {string.Join(", ", constraintViolationNames)} — Prefabrik Malzeme Listesi eksik/hatalı olabilir.");
        }

        // =====================================================================
        // 3. System-isolated BOM builder
        // =====================================================================

        /// <summary>
        /// Builds the bill-of-materials list system by system, preserving the
        /// same system order as <see cref="BoQReport.Systems"/>.
        ///
        /// Pre-cast: aggregates discrete part counts (mandatory parts + variable rings).
        /// Cast-in-place: lists manhole count and total concrete depth per diameter.
        /// </summary>
        public static List<SystemBomGroup> BuildBomBySystem(
            BoQReport   report,
            BoQSettings settings)
        {
            var groups    = new List<SystemBomGroup>();
            if (report == null) return groups;
            bool isPreCast = settings.ManholeType == ManholeType.PreCast;

            foreach (var sys in report.Systems)
            {
                var group = new SystemBomGroup { SystemName = sys.SystemName };
                var manholes = sys.Manholes;

                if (isPreCast)
                    BuildPreCastBom(manholes, group.Lines);
                else
                    BuildCipBom(manholes, group.Lines);

                groups.Add(group);
            }

            return groups;
        }

        // =====================================================================
        // Private: per-manhole topology + stacking
        // =====================================================================

        private static void ProcessManhole(
            ManholeItem            mh,
            List<SectionDebugRow>  allSections,
            ref int                excavUnresolvedCount,
            ref int                steppedIgnoredCount)
        {
            // ── Topology ──────────────────────────────────────────────────────
            var inlets  = allSections
                .Where(s => string.Equals(s.EndNodeName,   mh.NodeName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var outlets = allSections
                .Where(s => string.Equals(s.StartNodeName, mh.NodeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var invertsAtNode = inlets .Select(s => s.InvertEnd)
                .Concat(outlets.Select(s => s.InvertStart))
                .ToList();

            double lowestInvert = invertsAtNode.Count > 0
                ? invertsAtNode.Min() : double.NaN;

            // ── Catalog lookup (Baca-Boru Bağlantı Kuralları → ComponentFamily + Taban) ──
            ResolveFamilyAndTaban(mh, inlets, outlets,
                out ComponentFamily family, out BottomElementComponent taban, out DepthTierRule matchedTier);

            // ── Excavation pit geometry (Baca Kazı Kataloğu) ─────────────────
            ResolveExcavation(mh, family, taban, ref excavUnresolvedCount, ref steppedIgnoredCount);

            // ── Drop-pipe rule (pre-cast logic drives the SmartTypeName) ──────
            var dropInlets   = new List<SectionDebugRow>();
            var normalInlets = new List<SectionDebugRow>(inlets);

            if (!double.IsNaN(lowestInvert))
            {
                double mandH = (family != null && taban != null)
                    ? TotalMandatoryHeightM(family, taban) : 0;
                double dropThreshold = lowestInvert + mandH;

                dropInlets   = inlets.Where(s => s.InvertEnd > dropThreshold).ToList();
                normalInlets = inlets.Where(s => s.InvertEnd <= dropThreshold).ToList();
            }

            mh.HasDropPipe      = dropInlets.Count > 0;
            mh.ValidInletCount  = normalInlets.Count;
            mh.ValidOutletCount = outlets.Count;

            // ── Smart type name ───────────────────────────────────────────────
            string diamTag  = mh.DrawnShape != FootprintShape.Circular
                ? mh.DiameterDisplay
                : (mh.Diameter > 0 ? $"O{mh.Diameter}" : "O?");
            string dropNote = mh.HasDropPipe ? $" [+{dropInlets.Count} Selale]" : "";
            mh.SmartTypeName =
                $"Baca {diamTag} - ({mh.ValidInletCount} Giris / {mh.ValidOutletCount} Cikis){dropNote}";

            // ── Stacking — both scenarios cached simultaneously ───────────────
            mh.StackPreCast = (family != null && taban != null)
                ? ComputeFamilyStack(mh.Depth, mh.Diameter, family, taban, matchedTier)
                : null;

            mh.StackCastInPlace = new ManholeStackResult
            {
                NominalDiameter = mh.Diameter,
                IsPreCast       = false,
                ConcreteDepth   = mh.Depth
            };
        }

        // =====================================================================
        // Private: Baca-Boru Bağlantı Kuralları resolution
        // (connected pipe diameter/family + manhole depth → Taban)
        // =====================================================================

        /// <summary>
        /// Urbano only ever tells us the manhole's DIAMETER, never the base
        /// (Taban) piece's height — two Tabans can share a diameter but differ in
        /// height depending on which pipe connects. The drawn diameter
        /// (<see cref="ManholeItem.Diameter"/>) is a HARD constraint that is never
        /// overridden by catalog rules (user directive 2026-07-04): rules whose
        /// resulting Taban has a DIFFERENT diameter are excluded entirely, never
        /// considered even as a "closest" option. A direct Tür Eşleştirme→Taban
        /// link cannot express this at all, so it is not consulted here.
        ///
        /// Algorithm: take the largest-diameter pipe connected to this manhole,
        /// resolve its own Type Mapping link to find its PipeFamily, collect every
        /// (PipeRangeRule, DepthTierRule) pair scoped to that family whose
        /// SelectedBaseId resolves to a circular BottomElementComponent with
        /// Footprint.DiameterMm == mh.Diameter, then pick the pair whose pipe
        /// range is exact-or-closest to the connected pipe's diameter (ties
        /// broken by closest depth range to the manhole's own depth).
        ///
        /// Exception (user directive 2026-07-04): when Urbano gave NO usable
        /// diameter at all (mh.Diameter &lt;= 0 — ExtractManholeNominalDiam found no
        /// parseable Φ in the catalog item name), the diameter filter is dropped
        /// entirely — every circular Taban in the scoped rules is eligible, closest
        /// wins as usual, and mh.Diameter is BACKFILLED from the winning Taban's
        /// own diameter. This is the one case where the drawn diameter is not a
        /// hard constraint, precisely because there isn't one to respect.
        /// </summary>
        private static void ResolveFamilyAndTaban(
            ManholeItem mh, List<SectionDebugRow> inlets, List<SectionDebugRow> outlets,
            out ComponentFamily family, out BottomElementComponent taban, out DepthTierRule tier)
        {
            family = null;
            taban  = null;
            tier   = null;

            var governingPipe = inlets.Concat(outlets)
                .Where(s => s.LinkedPipeFamilyId != Guid.Empty)
                .OrderByDescending(s => s.DiameterMm)
                .FirstOrDefault();
            if (governingPipe == null) return;

            var catalog = SmartAssemblyCatalogStore.Current;
            if (catalog?.MasterPipeRules == null) return;

            var candidateRules = catalog.MasterPipeRules
                .Where(r => r.SelectedPipeFamilyId == governingPipe.LinkedPipeFamilyId)
                .ToList();
            if (candidateRules.Count == 0) return;

            // Additional narrowing filter (user directive 2026-07-05): if this
            // manhole's own Urbano catalog item is linked to a manhole family in
            // Tür Eşleştirme, the resolved Taban must belong to THAT family too —
            // on top of the existing pipe-family scoping above. Surfaces a catalog
            // gap early (family picked but no matching diameter/rule inside it)
            // instead of only failing much later. No link yet → filter is simply
            // skipped, behavior unchanged (graceful degrade).
            var manholeLink = !string.IsNullOrEmpty(mh.MhGuid)
                ? TypeMappingStore.FindManholeLink(mh.MhGuid) : null;
            Guid linkedFamilyId = manholeLink?.LinkedFamilyId ?? Guid.Empty;

            // Çap Modu (user directive 2026-07-06 — now actually wired up, was
            // previously a dead UI field with zero engine consumer): when a
            // manhole's link is explicitly set to ComputeFromCatalog, Urbano's own
            // drawn diameter/dimensions are treated as if they don't exist at all,
            // forcing the SAME resolution path already used when Urbano genuinely
            // gives no diameter — every Taban of the matching shape becomes
            // eligible, and mh.Diameter/DrawnLengthM/DrawnWidthM get backfilled
            // from the winning Taban below. FollowDrawing (default, and every
            // manhole with no link at all) is completely unchanged — Urbano's
            // drawn value stays a hard constraint exactly as before.
            bool forceCatalogDiameter = manholeLink?.DiameterMode == ManholeDiameterMode.ComputeFromCatalog;
            bool diameterKnown   = !forceCatalogDiameter && mh.Diameter > 0;
            bool dimensionsKnown = !forceCatalogDiameter && mh.DrawnLengthM > 0 && mh.DrawnWidthM > 0;

            // Every (rule, tier) pair whose resolved Taban matches the DRAWN
            // manhole diameter exactly (when known) — anything else is excluded
            // outright, never used even as a fallback. When no diameter was ever
            // drawn, every circular Taban is eligible instead (see method doc).
            var diameterMatches = new List<(PipeRangeRule Rule, DepthTierRule Tier, BottomElementComponent Component, ComponentFamily OwningFamily)>();
            foreach (var rule in candidateRules)
            {
                foreach (var depthTier in rule.DepthTiers)
                {
                    if (depthTier.IsCastInSitu || depthTier.SelectedBaseId == Guid.Empty) continue;
                    var component = catalog.FindById(depthTier.SelectedBaseId) as BottomElementComponent;
                    if (component == null) continue;
                    if (!ShapeMatches(mh, component.Footprint, diameterKnown, dimensionsKnown)) continue;

                    var owningFam = catalog.Families.FirstOrDefault(f => f.Components.Contains(component));
                    if (owningFam == null) continue;
                    if (linkedFamilyId != Guid.Empty && owningFam.Id != linkedFamilyId) continue;

                    diameterMatches.Add((rule, depthTier, component, owningFam));
                }
            }
            if (diameterMatches.Count == 0) return;

            // Among the eligible pairs: closest by pipe range, then closest by
            // depth range as a tie-breaker.
            var best = diameterMatches
                .Select(m => new
                {
                    Match    = m,
                    PipeDist = DistanceToRange(governingPipe.DiameterMm, m.Rule.MinPipeMm, m.Rule.MaxPipeMm),
                    DepthDist = DistanceToRange(mh.Depth, m.Tier.MinDepthM, m.Tier.MaxDepthM)
                })
                .OrderBy(x => x.PipeDist)
                .ThenBy(x => x.DepthDist)
                .First();

            family = best.Match.OwningFamily;
            taban  = best.Match.Component;
            tier   = best.Match.Tier;

            if (mh.DrawnShape == FootprintShape.Circular)
            {
                if (!diameterKnown)
                    mh.Diameter = (int)Math.Round(taban.Footprint.DiameterMm);
            }
            else if (!dimensionsKnown)
            {
                mh.DrawnLengthM = (taban.Footprint.Shape == FootprintShape.Square
                    ? taban.Footprint.SideMm : taban.Footprint.LengthMm) / 1000.0;
                mh.DrawnWidthM = (taban.Footprint.Shape == FootprintShape.Square
                    ? taban.Footprint.SideMm : taban.Footprint.WidthMm) / 1000.0;
            }
        }

        /// <summary>
        /// Shape-aware Taban match: a manhole drawn as Circular only matches a
        /// Circular Taban by diameter (unchanged, original behavior); Square only
        /// matches Square by side; Rectangular only matches Rectangular by L/W,
        /// allowing the catalog component's long/short sides to be swapped
        /// relative to the drawn orientation. When the drawn dimension is unknown
        /// (0), every Taban of the matching shape is eligible — mirrors the
        /// original diameter-unknown fallback.
        /// </summary>
        private static bool ShapeMatches(
            ManholeItem mh, Footprint fp, bool diameterKnown, bool dimensionsKnown)
        {
            const double tolM = 0.001;
            switch (mh.DrawnShape)
            {
                case FootprintShape.Circular:
                    return fp.Shape == FootprintShape.Circular &&
                        (!diameterKnown || Math.Abs(fp.DiameterMm - mh.Diameter) <= 1e-6);

                case FootprintShape.Square:
                    return fp.Shape == FootprintShape.Square &&
                        (!dimensionsKnown || Math.Abs(fp.SideMm / 1000.0 - mh.DrawnLengthM) <= tolM);

                default: // Rectangular
                    if (fp.Shape != FootprintShape.Rectangular) return false;
                    if (!dimensionsKnown) return true;
                    double l = mh.DrawnLengthM, w = mh.DrawnWidthM;
                    bool direct  = Math.Abs(fp.LengthMm / 1000.0 - l) <= tolM && Math.Abs(fp.WidthMm / 1000.0 - w) <= tolM;
                    bool swapped = Math.Abs(fp.LengthMm / 1000.0 - w) <= tolM && Math.Abs(fp.WidthMm / 1000.0 - l) <= tolM;
                    return direct || swapped;
            }
        }

        private static double DistanceToRange(double value, double min, double max)
        {
            if (value < min) return min - value;
            if (max > 0 && value > max) return value - max;
            return 0;
        }

        // ── Parça Kısıtları (per-role Min/Max component counts) ───────────────
        // Convention (ComponentConstraintVm, the UI's own source of truth):
        // MaxCount == -1 → unlimited; == 0 → don't use any; N > 0 → at most N.
        // No constraint row defined for a role at all → treated as unlimited
        // (matches every tier that predates this feature — zero behavior change).
        private static int GetMaxCount(DepthTierRule tier, ComponentRole role)
        {
            var c = tier?.ComponentConstraints?.FirstOrDefault(x => x.Role == role);
            return c?.MaxCount ?? -1;
        }

        private static int GetMinCount(DepthTierRule tier, ComponentRole role)
        {
            var c = tier?.ComponentConstraints?.FirstOrDefault(x => x.Role == role);
            return c?.MinCount ?? 0;
        }

        // =====================================================================
        // Private: Baca Kazı Kataloğu resolution (Phase 7 — excavation pit)
        // =====================================================================

        /// <summary>
        /// Resolves the manhole's excavation pit geometry from
        /// ManholeExcavationCatalogStore, once the Taban is already known (needed
        /// for TabanKalinligiMm and the pit's base width). Matching is by RANGE
        /// containment only (Min/Maks Taban Ø, then Min/Maks depth within the
        /// matched rule) — no closest-fallback: a manhole whose diameter/depth
        /// falls outside every defined range is an explicit, reported failure
        /// (user directive 2026-07-05), not a silently-approximated guess.
        ///
        /// On any failure, mh.ExcavationDepth stays at the raw baseline set in
        /// BoQParserService.ComputeManholeDepths (TerrainZ − lowestInvert + MHB,
        /// catalog-independent) and mh.ExcavationVolume stays 0 — never guessed.
        /// </summary>
        private static void ResolveExcavation(
            ManholeItem mh, ComponentFamily family, BottomElementComponent taban,
            ref int excavUnresolvedCount, ref int steppedIgnoredCount)
        {
            if (taban == null) { excavUnresolvedCount++; return; }

            var rules = ManholeExcavationCatalogStore.Current;
            if (rules == null || rules.Count == 0) { excavUnresolvedCount++; return; }

            // Min/Max Taban Ø in the catalog is a shape-agnostic "size" scalar —
            // ManholeExcavationMainVm.RebuildDiameterList already builds its size
            // dropdown as diameter (Circular) / side (Square) / longer side
            // (Rectangular), so the match here must use the exact same
            // convention instead of always comparing mh.Diameter (which stays 0
            // for a non-circular manhole — this was the bug that left every
            // Square/Rectangular manhole's excavation permanently unresolved
            // even after the Taban-matching fix). ResolveFootprintWidthM already
            // implements this convention (reused below for baseWidthM too).
            double baseSizeMm = ResolveFootprintWidthM(taban.Footprint) * 1000.0;

            var rule = rules.FirstOrDefault(r =>
                baseSizeMm >= r.MinBaseDiameterMm &&
                (r.MaxBaseDiameterMm <= 0 || baseSizeMm <= r.MaxBaseDiameterMm) &&
                (r.SelectedFamilyNames == null || r.SelectedFamilyNames.Count == 0 ||
                 (family != null && r.SelectedFamilyNames.Contains(family.Name, StringComparer.OrdinalIgnoreCase))));
            if (rule == null) { excavUnresolvedCount++; return; }

            var tier = rule.DepthTiers.FirstOrDefault(t =>
                mh.Depth >= t.MinDepthM && (t.MaxDepthM <= 0 || mh.Depth <= t.MaxDepthM));
            if (tier == null) { excavUnresolvedCount++; return; }

            // Basamaklı Kazı (stepped/benched) is a different volume shape entirely
            // and is not implemented yet (deferred 2026-07-05) — fall back to a
            // smooth slope using this tier's own SlopeRatio, but flag it so the
            // gap is visible instead of silently under/over-stating the volume.
            if (tier.IsSteppedExcavation) steppedIgnoredCount++;

            double baseWidthM  = ResolveFootprintWidthM(taban.Footprint);
            double tabanThickM = taban.TabanKalinligiMm / 1000.0;
            // mh.ExcavationDepth already holds the raw baseline set in
            // BoQParserService.ComputeManholeDepths (structural depth + the lowest
            // connected pipe's own wall thickness) — add the Taban slab and Alt
            // Temel Katmanları on top of that, not mh.Depth (which is the
            // structural-only depth used for precast ring stacking).
            double finalDepth  = mh.ExcavationDepth + tabanThickM + tier.TotalSubBaseDepthM;
            double baseSideM   = baseWidthM + 2.0 * tier.WorkingClearanceM;

            mh.ExcavationDepth        = finalDepth;
            mh.ExcavWorkingClearanceM = tier.WorkingClearanceM;
            mh.ExcavSlopeRatio        = tier.SlopeRatio;
            mh.ExcavBaseSideM         = baseSideM;
            mh.ExcavationVolume       = ManholeExcavationGeometry.ComputeFrustumVolume(
                baseSideM, finalDepth, tier.SlopeRatio);

            mh.ResolvedBackfillLayers = tier.BackfillLayers?.ToList() ?? new List<ManholeBackfillLayer>();
        }

        /// <summary>Effective plan width (m) of a Taban footprint, used as the
        /// excavation pit's base side before working clearance is added — the pit
        /// itself is always square regardless of the Taban's own footprint shape
        /// (user directive 2026-07-05).</summary>
        private static double ResolveFootprintWidthM(Footprint fp)
        {
            if (fp == null) return 0;
            switch (fp.Shape)
            {
                case FootprintShape.Circular: return fp.DiameterMm / 1000.0;
                case FootprintShape.Square:   return fp.SideMm / 1000.0;
                default:                      return Math.Max(fp.LengthMm, fp.WidthMm) / 1000.0;
            }
        }

        private static double TabanHeightM(BottomElementComponent taban)
            => (taban.EffectiveHeight + taban.TabanKalinligiMm) / 1000.0;

        private static double TotalMandatoryHeightM(ComponentFamily family, BottomElementComponent taban)
        {
            // Approximation used only for the drop-pipe threshold — the exact
            // mandatory height (incl. diameter-matched Konik/Kapak) is computed
            // properly inside ComputeFamilyStack; this just needs to be in the
            // right ballpark, so it stays diameter-agnostic here.
            double h = TabanHeightM(taban);
            h += family.Components
                .Where(c => c.Role != ComponentRole.BottomElement && c.ZorunluParca)
                .Sum(c => c.EffectiveHeight) / 1000.0;
            return h;
        }

        // =====================================================================
        // Private: greedy stacking against our own ComponentFamily catalog
        // =====================================================================

        /// <summary>
        /// Builds a ManholeStackResult for one pre-cast manhole from ComponentFamily
        /// (replaces the old Manhole_Catalog.xlsx-driven ComputePreCastStack).
        ///
        /// A family mixes components for MULTIPLE diameters together (confirmed
        /// against the real catalog — one "Precast" family holds Ø1000/1200/1400/1600
        /// parts side by side), so every role below is filtered by a diameter
        /// COMPATIBILITY CHAIN, not just by Role:
        ///   Taban.TopOpeningDiameterMm (shaft Ø)
        ///     → Gövde.InnerDiameterMm            must equal shaft Ø
        ///     → Konik.BottomInnerDiameterMm      must equal shaft Ø
        ///     → Konik.TopInnerDiameterMm         = neck Ø (narrower)
        ///     → Boyun Bileziği.InnerDiameterMm   must equal neck Ø (NOT shaft Ø)
        ///     → Kapak.ClearOpeningMm             must equal neck Ø
        ///
        /// Steps (user-confirmed 2026-07-04):
        /// 1. Taban (already resolved) + Konik (diameter-matched, exactly one —
        ///    two Konik sharing a diameter is invalid catalog data; first found
        ///    wins defensively) + Kapak (diameter-matched to the neck, exactly
        ///    one) + any other flat-ZorunluParca component.
        /// 2. Greedily fill the remaining depth with diameter-matched Gövde rings
        ///    (largest first).
        /// 3. If a gap &gt; LeftoverTolerance (3 cm) remains, close it with the
        ///    smallest diameter-matched Boyun Bileziği piece. No exact
        ///    combinatorial search — user confirmed simple greedy + one Boyun
        ///    always lands within the 3 cm tolerance in practice.
        /// </summary>
        private static ManholeStackResult ComputeFamilyStack(
            double depth, int diameter, ComponentFamily family, BottomElementComponent taban,
            DepthTierRule tier)
        {
            var stack = new ManholeStackResult
            {
                NominalDiameter = diameter,
                IsPreCast       = true
            };

            double shaftDiam = taban.TopOpeningDiameterMm;

            // "değişken" (user directive 2026-07-06, generalized 2026-07-06):
            // ANY piece in the chain — not just Taban — can be marked
            // IsVariable. Its real installed height is NOT the catalog
            // EffectiveHeight: every FIXED (non-variable) piece is placed first
            // using its normal height, and the remaining depth is then split
            // EVENLY across however many değişken pieces exist (1 piece → gets
            // 100%; 2 pieces → 50/50; 3 → a third each). Once any değişken piece
            // exists anywhere, no further optional piece (Gövde greedy fill /
            // Boyun gap-correction) is added at all.
            var variableParticipants = new List<ManholeComponent>();
            double fixedHeight = 0;

            // ── Step 1a: Taban ─────────────────────────────────────────────────
            if (taban.IsVariable)
                variableParticipants.Add(taban);
            else
            {
                double hTaban = TabanHeightM(taban);
                stack.Parts.Add(NewStackedPart(taban, hTaban, 1, false));
                fixedHeight += hTaban;
            }

            // Parça Kısıtları (user directive 2026-07-06): MaxCount==0 for a role
            // means "don't use any piece of this type" even if the family/tier
            // would otherwise supply one; MinCount>0 with no piece actually
            // added is a genuine catalog/tier gap — both are enforced below and
            // surfaced via stack.ConstraintViolated (aggregated by Process()).
            int konikMax = GetMaxCount(tier, ComponentRole.Reducer);
            int kapakMax = GetMaxCount(tier, ComponentRole.Cover);

            // ── Step 1b: Konik (Reducer) — diameter-matched, exactly one ───────
            var konik = konikMax == 0 ? null : family.Components.OfType<ReducerComponent>()
                .FirstOrDefault(r => Math.Abs(r.BottomInnerDiameterMm - shaftDiam) < 1e-6);
            double neckDiam = shaftDiam; // no Konik found → Boyun/Kapak fall back to matching the shaft directly
            if (konik != null)
            {
                neckDiam = konik.TopInnerDiameterMm;
                if (konik.IsVariable)
                    variableParticipants.Add(konik);
                else
                {
                    double hM = konik.EffectiveHeight / 1000.0;
                    stack.Parts.Add(NewStackedPart(konik, hM, 1, false));
                    fixedHeight += hM;
                }
            }
            if (konik == null && GetMinCount(tier, ComponentRole.Reducer) >= 1)
                stack.ConstraintViolated = true;

            // ── Step 1c: Kapak (Cover) — diameter-matched to the neck, exactly one ──
            var kapak = kapakMax == 0 ? null : family.Components.OfType<CoverComponent>()
                .FirstOrDefault(c => Math.Abs(c.ClearOpeningMm - neckDiam) < 1e-6);
            if (kapak != null)
            {
                if (kapak.IsVariable)
                    variableParticipants.Add(kapak);
                else
                {
                    double hM = kapak.EffectiveHeight / 1000.0;
                    stack.Parts.Add(NewStackedPart(kapak, hM, 1, false));
                    fixedHeight += hM;
                }
            }
            if (kapak == null && GetMinCount(tier, ComponentRole.Cover) >= 1)
                stack.ConstraintViolated = true;

            // ── Step 1d: any other flat-mandatory component (roles above excluded) ──
            foreach (var part in family.Components.Where(c =>
                         c.Role != ComponentRole.BottomElement &&
                         c.Role != ComponentRole.Reducer &&
                         c.Role != ComponentRole.Cover &&
                         c.ZorunluParca))
            {
                if (part.IsVariable)
                    variableParticipants.Add(part);
                else
                {
                    double hM = part.EffectiveHeight / 1000.0;
                    stack.Parts.Add(NewStackedPart(part, hM, 1, false));
                    fixedHeight += hM;
                }
            }

            // ── Gövde/Boyun candidate pools — same diameter matching as the
            // normal greedy-fill/gap-correction steps below, just computed
            // early so a değişken candidate in either pool can be detected. ──
            var govdeCandidates = family.Components.OfType<MiddleElementComponent>()
                .Where(m => Math.Abs(m.InnerDiameterMm - shaftDiam) < 1e-6 && !m.ZorunluParca)
                .ToList();
            var variableGovde = govdeCandidates.FirstOrDefault(g => g.IsVariable);
            if (variableGovde != null) variableParticipants.Add(variableGovde);

            var boyunCandidates = family.Components.OfType<AdjusterComponent>()
                .Where(a => Math.Abs(a.InnerDiameterMm - neckDiam) < 1e-6)
                .ToList();
            var variableBoyun = boyunCandidates.FirstOrDefault(a => a.IsVariable);
            if (variableBoyun != null) variableParticipants.Add(variableBoyun);

            if (variableParticipants.Count > 0)
            {
                double perPieceHeight = Math.Max(0, depth - fixedHeight) / variableParticipants.Count;
                foreach (var vp in variableParticipants)
                    stack.Parts.Add(NewStackedPart(vp, perPieceHeight, 1, false, isDegisken: true));
                stack.ResidualM = 0;
                return stack;
            }

            // ── Step 2: greedy Gövde ring fill — diameter-matched to the shaft ──
            double remaining = depth - fixedHeight;
            var variableRings = govdeCandidates
                .GroupBy(c => c.EffectiveHeight)
                .Select(g => g.First())
                .OrderByDescending(c => c.EffectiveHeight)
                .ToList();

            if (remaining <= 0 || variableRings.Count == 0)
            {
                stack.ResidualM = remaining;
                if (remaining > LeftoverTolerance) stack.ConstraintViolated = true;
                if (GetMinCount(tier, ComponentRole.MiddleElement) >= 1) stack.ConstraintViolated = true;
                return stack;
            }

            int govdeMax = GetMaxCount(tier, ComponentRole.MiddleElement);
            int govdeUsedCount = 0;
            var ringUsage = new Dictionary<double, RingUsageEntry>();
            foreach (var ring in variableRings)
            {
                if (govdeMax >= 0 && govdeUsedCount >= govdeMax) break;
                double hM = ring.EffectiveHeight / 1000.0;
                if (hM <= 1e-9) continue;
                int count = (int)(remaining / hM);
                if (govdeMax >= 0) count = Math.Min(count, govdeMax - govdeUsedCount);
                if (count > 0)
                {
                    ringUsage[hM] = new RingUsageEntry { Component = ring, Count = count };
                    remaining -= count * hM;
                    govdeUsedCount += count;
                }
            }
            if (govdeUsedCount < GetMinCount(tier, ComponentRole.MiddleElement))
                stack.ConstraintViolated = true;

            // ── Step 3: leftover gap correction — Boyun Bileziği, neck-diameter-matched ──
            int boyunMax = GetMaxCount(tier, ComponentRole.Adjuster);
            if (remaining > LeftoverTolerance && boyunMax != 0)
            {
                var boyun = boyunCandidates
                    .OrderBy(a => a.EffectiveHeight)
                    .FirstOrDefault();
                if (boyun != null)
                {
                    double hM = boyun.EffectiveHeight / 1000.0;
                    stack.Parts.Add(NewStackedPart(boyun, hM, 1, true));
                    remaining -= hM;
                }
            }

            stack.ResidualM = Math.Max(0, remaining);
            // Final check: even after every role's Max cap was respected, the
            // target depth couldn't be reached — the exact "used the max
            // allowed count per piece but still short" case the user asked
            // about. Never silently swallowed (see ConstraintViolated doc).
            if (stack.ResidualM > LeftoverTolerance)
                stack.ConstraintViolated = true;

            // Convert usage map to StackedPart list (largest ring first)
            foreach (var kv in ringUsage.OrderByDescending(k => k.Key))
            {
                if (kv.Value.Count > 0)
                    stack.Parts.Add(NewStackedPart(kv.Value.Component, kv.Key, kv.Value.Count, true));
            }

            return stack;
        }

        // Builds a StackedPart carrying the underlying catalog component's PozNo/
        // Aciklama/volumes — proves the stack is actually catalog-driven downstream.
        // isDegisken (user directive 2026-07-06): a "değişken" component's
        // catalog MaterialVolume/ExternalVolume represent the RATE for 1 metre
        // of height, not a fixed total — scale by the actual computed height.
        // Non-değişken components keep the catalog value as-is (a fixed total
        // for that component's own EffectiveHeight), unchanged from before.
        private static StackedPart NewStackedPart(
            ManholeComponent component, double heightM, int count, bool isVariableRing,
            bool isDegisken = false)
            => new StackedPart
            {
                PartName          = component.Name,
                HeightM           = heightM,
                Count             = count,
                IsVariableRing    = isVariableRing,
                PozNo             = component.PozNo,
                Aciklama          = component.Aciklama,
                UnitMaterialVolume = isDegisken ? component.MaterialVolume * heightM : component.MaterialVolume,
                UnitExternalVolume = isDegisken ? component.ExternalVolume * heightM : component.ExternalVolume
            };

        // ── Small helper to avoid tuples in .NET 4.8 ─────────────────────────
        private sealed class RingUsageEntry
        {
            public ManholeComponent Component { get; set; }
            public int              Count     { get; set; }
        }

        // =====================================================================
        // Private: BOM builders
        // =====================================================================

        private static void BuildPreCastBom(
            List<ManholeItem> manholes,
            List<BomLine>     lines)
        {
            // Aggregate: group by (Diameter, PartName, HeightM, IsVariableRing)
            var bomData = manholes
                .Where(m => m.StackPreCast != null)
                .SelectMany(m => m.StackPreCast.Parts.Select(p => new
                {
                    m.Diameter,
                    p.PartName,
                    p.HeightM,
                    p.Count,
                    p.IsVariableRing,
                    p.PozNo,
                    p.Aciklama,
                    p.UnitMaterialVolume,
                    p.UnitExternalVolume
                }))
                .GroupBy(x => new
                {
                    x.Diameter,
                    x.PartName,
                    HeightKey = Math.Round(x.HeightM, 4),
                    x.IsVariableRing
                })
                // Sort: diameter asc, mandatory before variable, variable rings largest first
                .OrderBy(g => g.Key.Diameter)
                .ThenBy(g => g.Key.IsVariableRing ? 1 : 0)
                .ThenByDescending(g => g.Key.HeightKey)
                .ThenBy(g => g.Key.PartName);

            foreach (var grp in bomData)
            {
                int totalCount = grp.Sum(x => x.Count);
                if (totalCount <= 0) continue;

                var first = grp.First();
                lines.Add(new BomLine
                {
                    Description = FormatPartDescription(
                        grp.Key.PartName,
                        grp.Key.HeightKey,
                        grp.Key.Diameter,
                        grp.Key.IsVariableRing),
                    Quantity = totalCount,
                    Unit     = "Adet",
                    PozNo               = first.PozNo,
                    Aciklama            = first.Aciklama,
                    TotalMaterialVolume = grp.Sum(x => x.UnitMaterialVolume * x.Count),
                    TotalExternalVolume = grp.Sum(x => x.UnitExternalVolume * x.Count)
                });
            }

            // Manholes with no catalog match → flag them
            var noCatalog = manholes
                .Where(m => m.StackPreCast == null)
                .GroupBy(m => m.Diameter)
                .OrderBy(g => g.Key);

            foreach (var grp in noCatalog)
            {
                string tag = grp.Key > 0 ? $" O{grp.Key} mm" : " (unknown)";
                lines.Add(new BomLine
                {
                    Description = "(Catalog not found)" + tag,
                    Quantity    = grp.Count(),
                    Unit        = "Adet"
                });
            }
        }

        private static void BuildCipBom(
            List<ManholeItem> manholes,
            List<BomLine>     lines)
        {
            var byDiam = manholes
                .Where(m => m.StackCastInPlace != null)
                .GroupBy(m => m.Diameter)
                .OrderBy(g => g.Key);

            foreach (var grp in byDiam)
            {
                string tag        = grp.Key > 0 ? $" O{grp.Key} mm" : "";
                double totalDepth = grp.Sum(m => m.StackCastInPlace.ConcreteDepth);
                lines.Add(new BomLine
                {
                    Description  = "Beton Baca" + tag,
                    Quantity     = grp.Count(),
                    Unit         = "Adet",
                    TotalDepthM  = totalDepth
                });
            }
        }

        // =====================================================================
        // Private: helpers
        // =====================================================================

        private static string FormatPartDescription(
            string partName, double heightM, int diameter, bool isVariableRing)
        {
            // Variable rings include their height in the label; mandatory parts do not
            // (their height is fixed and defined by the catalog).
            string heightTag = isVariableRing && heightM > 1e-9
                ? $" {heightM:F2}m" : "";
            string diamTag   = diameter > 0 ? $" O{diameter} mm" : "";
            return $"{partName}{heightTag}{diamTag}";
        }

        // ── Cell parsing ──────────────────────────────────────────────────────

        private static int ParseCellInt(object cellValue)
        {
            if (cellValue == null) return 0;
            if (cellValue is double d) return (int)Math.Round(d);
            if (cellValue is int    i) return i;
            return int.TryParse(cellValue.ToString().Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private static double ParseCellDouble(object cellValue)
        {
            if (cellValue == null) return 0;
            if (cellValue is double d) return d;
            if (cellValue is int    i) return i;
            return double.TryParse(cellValue.ToString().Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        /// <summary>
        /// Accepts "Yes", "True", "1", "Evet" (case-insensitive) as true.
        /// Everything else is false.
        /// </summary>
        private static bool ParseBool(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            return s.Equals("yes",  StringComparison.OrdinalIgnoreCase)
                || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s.Equals("1")
                || s.Equals("evet", StringComparison.OrdinalIgnoreCase);
        }
    }

    // =========================================================================
    // BOM output models
    // =========================================================================

    /// <summary>
    /// One aggregated line in the Bill of Materials.
    /// Used by both ManholeAIService (builder) and ExcelExportService (writer).
    /// </summary>
    public class BomLine
    {
        /// <summary>Part description, e.g. "Taban O1000 mm" or "Govde 0.50m O1000 mm".</summary>
        public string Description { get; set; }
        /// <summary>Total number of pieces (pre-cast) or number of manholes (cast-in-place).</summary>
        public int    Quantity    { get; set; }
        /// <summary>Unit string, e.g. "Adet".</summary>
        public string Unit        { get; set; }
        /// <summary>Total concrete depth in metres (cast-in-place lines only; 0 for pre-cast).</summary>
        public double TotalDepthM { get; set; }

        // ── From our ComponentFamily catalog (pre-cast lines only; blank for CIP) ──
        public string PozNo                { get; set; }
        public string Aciklama             { get; set; }
        /// <summary>Sum of MaterialVolume across all Quantity units (m³).</summary>
        public double TotalMaterialVolume  { get; set; }
        /// <summary>Sum of ExternalVolume across all Quantity units (m³).</summary>
        public double TotalExternalVolume  { get; set; }
    }

    /// <summary>
    /// BOM lines for one network system.
    /// Returned by ManholeAIService.BuildBomBySystem() in system order.
    /// </summary>
    public class SystemBomGroup
    {
        public string        SystemName { get; set; }
        public List<BomLine> Lines      { get; set; } = new List<BomLine>();
    }
}
