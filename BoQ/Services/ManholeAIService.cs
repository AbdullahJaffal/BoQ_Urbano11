using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Services;
using UrbanoMetraj.BoQ.ProjectRules.Models;
using UrbanoMetraj.BoQ.ProjectRules.Services;
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
        /// Remaining-height tolerance in metres (revised 2026-07-06: strictly under
        /// 6 cm — the shortest available Boyun/Gövde piece is 6 cm, so any gap of
        /// 6 cm or more means an actual piece exists that could have closed it and
        /// should be flagged, while a leftover under 6 cm genuinely can't be closed
        /// by any piece we have). 5.99 cm passes; 6.00 cm does not. If the greedy
        /// fill leaves a gap smaller than this, no extra piece is added/flagged.
        /// </summary>
        private const double LeftoverTolerance = 0.0599;

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
        // RULES-mode per-manhole resolution failures, grouped and surfaced by Process (reset each run).
        private static readonly List<(string Node, string Reason)> _rulesResolveFails =
            new List<(string, string)>();
        private static void RulesFail(ManholeItem mh, string reason)
            => _rulesResolveFails.Add((mh.NodeName, reason));

        public static void Process(
            BoQReport   report,
            BoQSettings settings)
        {
            if (report == null) return;
            _rulesResolveFails.Clear();

            int unresolvedCount     = 0;
            int excavUnresolvedCount = 0;
            int steppedIgnoredCount  = 0;
            // Grouped by reason (not just a flat name list) — with many manholes
            // failing at once, a bare name list gives no clue which of several
            // possible causes (missing Konik/Kapak, Min not met, genuine
            // unreachable depth) is actually responsible.
            var constraintViolationsByReason = new Dictionary<string, List<string>>();
            foreach (var sys in report.Systems)
                foreach (var mh in sys.Manholes)
                {
                    ProcessManhole(mh, report.SectionDebug, settings.RingFillMode, settings.BacaAltiParcaEklensin, settings.BacaKaziDisCapKullan, ref excavUnresolvedCount, ref steppedIgnoredCount);
                    if (mh.StackPreCast == null) unresolvedCount++;
                    else if (mh.StackPreCast.ConstraintViolated)
                    {
                        string reason = mh.StackPreCast.ConstraintViolationReason ?? "bilinmeyen neden";
                        // Group by CATEGORY, not the exact string — the residual-
                        // gap reason embeds a per-manhole numeric value (each
                        // manhole would otherwise become its own group of one).
                        string category =
                            reason.Contains("Konik")          ? "Konik bulunamadı" :
                            reason.Contains("Kapak")          ? "Kapak bulunamadı" :
                            reason.Contains("Gövde Halkası")  ? "Gövde Halkası sayısı yetersiz (Min karşılanmadı)" :
                            reason.Contains("hedef derinliğe") ? "Hedef derinliğe ulaşılamadı (Gövde/Boyun kalıntı boşluk)" :
                            reason;
                        if (!constraintViolationsByReason.TryGetValue(category, out var names))
                            constraintViolationsByReason[category] = names = new List<string>();
                        // Include the manhole's own StackPreCast.ResidualM alongside
                        // its name for the residual-gap category — the grouped
                        // category text alone hides the actual number, which is
                        // exactly what's needed to tell "just over tolerance" apart
                        // from "genuinely far short".
                        names.Add(category.Contains("Hedef derinliğe")
                            ? $"{mh.NodeName} ({mh.StackPreCast.ResidualM * 1000.0:0} mm)"
                            : mh.NodeName);
                    }
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
            foreach (var kv in constraintViolationsByReason)
                report.DiscoveryNotes.Add(
                    $"[WARN] Parça Kısıtları (Min/Maks) — {kv.Key}: {string.Join(", ", kv.Value)} — Prefabrik Malzeme Listesi eksik/hatalı olabilir.");

            // RULES mode: replace the generic "no match" note above with the actual per-cause reasons,
            // so the user can tell an unconfigured network from a Baca Çapı ↔ Taban mismatch.
            if (_rulesResolveFails.Count > 0)
                foreach (var g in _rulesResolveFails.GroupBy(x => x.Reason))
                    report.DiscoveryNotes.Add(
                        $"[WARN] Kurallar modu — {g.Key}: {string.Join(", ", g.Select(x => x.Node))}");
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
            RingFillMode           ringFillMode,
            bool                   ekleTemelAltiParca,
            bool                   pitWidthUsesOuter,
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
            ResolveExcavation(mh, family, taban, ekleTemelAltiParca, pitWidthUsesOuter, ref excavUnresolvedCount, ref steppedIgnoredCount);

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
            // RULES mode: restrict the shaft pieces to the heights the user left enabled in
            // "Kullanımdan Parça Çıkar" for this (family, diameter). TypeMapping mode is unchanged.
            var stackFamily = family;
            if (ProjectRulesStore.IsRulesMode && family != null && taban != null)
                stackFamily = RulesStackFamily(family, taban.TopOpeningDiameterMm,
                                               ProjectRulesStore.FindNetwork(mh.SystemName));

            mh.StackPreCast = (family != null && taban != null)
                ? ComputeFamilyStack(mh.Depth, mh.Diameter, stackFamily, taban, matchedTier, ringFillMode)
                : null;

            // Baca Altı Beton Parçası (user directive 2026-07-07): appended to the
            // BOM parts list AFTER the ring-count/height-budget stacking above has
            // already run to completion — it is physically below Taban, outside the
            // stacking column entirely, so it must never influence Gövde/Boyun
            // counts or any Min/Maks/gap math. Purely additive: on/off never
            // changes ComputeFamilyStack's own result, only whether this one extra
            // line is present. mh.ResolvedSubBaseParts was already populated (or
            // left empty) by ResolveExcavation, gated on the same setting.
            if (mh.StackPreCast != null && mh.ResolvedSubBaseParts.Count > 0)
            {
                var subBase = mh.ResolvedSubBaseParts[0];
                mh.StackPreCast.Parts.Add(NewStackedPart(subBase, subBase.EffectiveHeight / 1000.0, 1, false));
                SortPartsByPhysicalOrder(mh.StackPreCast);
            }

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

            // RULES mode: resolve from the per-network project rules instead of Tür Eşleştirme +
            // MasterPipeRules. Separate path so the TypeMapping code below stays byte-for-byte intact.
            if (ProjectRulesStore.IsRulesMode)
            {
                ResolveFamilyAndTabanRules(mh, inlets, outlets, out family, out taban, out tier);
                return;
            }

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

            // Real built shape/size of the resolved Taban — deliberately independent
            // of mh.DrawnShape above (see ManholeItem.ResolvedFootprint doc). Needed
            // by PipeNetLengthService, which must reduce by the manhole's actual
            // outer shell, not by how it happened to be drawn in the DWG.
            mh.ResolvedFootprint = taban.Footprint;
        }

        /// <summary>
        /// RULES-mode resolution: manhole family = the pipe's network default (or a per-network
        /// manhole exception keyed by the node's AG_GUID); the diameter comes from the network's
        /// connection rules (governing pipe Ø + manhole depth → ManholeDiameterMm), and the Taban is
        /// the family's base at that top-opening Ø. A synthetic <see cref="DepthTierRule"/> carries the
        /// tier's min/max ComponentConstraints so <c>ComputeFamilyStack</c> runs unchanged. The rule's
        /// diameter is authoritative — the manhole's own dimensions are backfilled from the Taban.
        /// </summary>
        private static void ResolveFamilyAndTabanRules(
            ManholeItem mh, List<SectionDebugRow> inlets, List<SectionDebugRow> outlets,
            out ComponentFamily family, out BottomElementComponent taban, out DepthTierRule tier)
        {
            family = null; taban = null; tier = null;

            var netRule = ProjectRulesStore.FindNetwork(mh.SystemName);
            if (netRule == null) { RulesFail(mh, "bu ağ için kural tanımlı değil"); return; }

            // Family (+ diameter) from a manhole exception (by node AG_GUID) override the network
            // default. A manhole's diameter comes from the rules, not Urbano — so the exception must
            // pin its own diameter (the connection-rule diameter is for the default family).
            Guid famId = netRule.ManholeFamilyId;
            double exDia = 0;
            var mhEx = netRule.Exceptions?.FindManhole(mh.AgGuid);
            if (mhEx != null && mhEx.ManholeFamilyId != Guid.Empty)
            {
                famId = mhEx.ManholeFamilyId;
                exDia = mhEx.ManholeDiameterMm;
            }
            if (famId == Guid.Empty) { RulesFail(mh, "Baca ailesi seçilmemiş"); return; }

            var catalog = SmartAssemblyCatalogStore.Current;
            var fam = catalog?.Families?.FirstOrDefault(f => f.Id == famId);
            if (fam == null) { RulesFail(mh, "seçilen Baca ailesi katalogda bulunamadı"); return; }

            // Governing connected pipe = largest diameter.
            var governingPipe = inlets.Concat(outlets).OrderByDescending(s => s.DiameterMm).FirstOrDefault();
            if (governingPipe == null) { RulesFail(mh, "bağlı boru yok"); return; }
            double govDia = governingPipe.DiameterMm;

            // Pick a (connection rule, tier): closest pipe range, then closest depth range.
            var pairs = (netRule.ConnectionRules ?? new List<ConnectionRule>())
                .SelectMany(r => (r.Tiers ?? new List<ConnDepthTier>()).Select(t => new { Rule = r, Tier = t }))
                .ToList();
            if (pairs.Count == 0) { RulesFail(mh, "Bağlantı Kuralı tanımlı değil"); return; }
            var best = pairs
                .Select(p => new
                {
                    p.Tier,
                    PipeDist  = DistanceToRange(govDia,   p.Rule.MinPipeMm, p.Rule.MaxPipeMm),
                    DepthDist = DistanceToRange(mh.Depth, p.Tier.MinDepthM, p.Tier.MaxDepthM)
                })
                .OrderBy(x => x.PipeDist).ThenBy(x => x.DepthDist)
                .First();
            var connTier = best.Tier;

            // A precast exception diameter forces precast even on a cast-in-situ tier; otherwise a
            // cast-in-situ tier → no precast Taban (leave nulls so the cast-in-place path is used).
            if (exDia <= 0 && connTier.IsCastInSitu) return;

            // Exception pins the diameter; otherwise it comes from the connection-rule tier.
            double targetDia = exDia > 0 ? exDia : connTier.ManholeDiameterMm;
            if (targetDia <= 0) { RulesFail(mh, "eşleşen katmanda Baca Çapı seçilmemiş"); return; }

            // Taban in the family at that top-opening Ø (prefer the drawn shape when several match).
            var bases = fam.Components.OfType<BottomElementComponent>()
                .Where(b => Math.Abs(b.TopOpeningDiameterMm - targetDia) < 1e-6)
                .ToList();
            if (bases.Count == 0)
            {
                RulesFail(mh, $"ailede Ø{targetDia:0} Taban yok (kuraldaki Baca Çapı aileyle uyuşmuyor)");
                return;
            }
            var baseComp = bases.FirstOrDefault(b => b.Footprint?.Shape == mh.DrawnShape) ?? bases[0];

            family = fam;
            taban  = baseComp;
            tier   = new DepthTierRule
            {
                MinDepthM      = connTier.MinDepthM,
                MaxDepthM      = connTier.MaxDepthM,
                SelectedBaseId = baseComp.Id,
                IsCastInSitu   = false
            };
            foreach (var cc in connTier.ComponentConstraints ?? new List<ComponentTypeConstraint>())
                tier.ComponentConstraints.Add(new ComponentTypeConstraint
                { Role = cc.Role, MinCount = cc.MinCount, MaxCount = cc.MaxCount });

            // The rule's diameter wins — backfill the manhole's own dimensions from the resolved Taban.
            var fp = baseComp.Footprint;
            if (fp != null)
            {
                if (fp.Shape == FootprintShape.Circular && fp.DiameterMm > 0)
                    mh.Diameter = (int)Math.Round(fp.DiameterMm);
                else if (fp.Shape == FootprintShape.Square)
                    mh.DrawnLengthM = mh.DrawnWidthM = fp.SideMm / 1000.0;
                else if (fp.Shape == FootprintShape.Rectangular)
                {
                    mh.DrawnLengthM = fp.LengthMm / 1000.0;
                    mh.DrawnWidthM  = fp.WidthMm  / 1000.0;
                }
            }
            mh.ResolvedFootprint = fp;
        }

        /// <summary>
        /// RULES mode: returns a copy of <paramref name="family"/> whose Gövde/Koni/Boyun/Kapak pieces
        /// are restricted to the heights allowed by the "Kullanımdan Parça Çıkar" row for this
        /// (family, diameter). No matching row (or no restriction) → the family is returned unchanged.
        /// Only the shaft pieces are filtered; the Taban is passed to the stacker separately.
        /// </summary>
        private static ComponentFamily RulesStackFamily(ComponentFamily family, double diameterMm, NetworkRule netRule)
        {
            var row = netRule?.PieceExclusionRows?.FirstOrDefault(
                r => r.ManholeFamilyId == family.Id && Math.Abs(r.ManholeDiameterMm - diameterMm) < 1e-6);
            if (row == null || row.Roles == null || row.Roles.Count == 0) return family;

            var filtered = new ComponentFamily { Id = family.Id, Name = family.Name, Malzeme = family.Malzeme };
            foreach (var c in family.Components)
            {
                var pe = row.Roles.FirstOrDefault(x => x.Role == c.Role);
                if (pe != null && !pe.AllowedHeightsMm.Any(h => Math.Abs(h - c.EffectiveHeight) < 1e-6))
                    continue;   // this role is restricted and this height isn't allowed
                filtered.Components.Add(c);
            }
            return filtered;
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
        /// <summary>RULES mode: narrows the global manhole-excavation rules to those whose soil scope
        /// includes the network's Zemin Tipi AND (when the network selected specific Kural Adı) whose
        /// name is selected. Non-RULES mode or unknown network → the full list, unchanged.</summary>
        private static List<ManholeExcavationRule> FilterManholeExcavRulesByNetwork(
            List<ManholeExcavationRule> rules, ManholeItem mh)
        {
            if (!ProjectRulesStore.IsRulesMode) return rules;
            var net = ProjectRulesStore.FindNetwork(mh.SystemName);
            if (net == null) return rules;
            string soil = net.SoilName ?? "";
            var names = net.ManholeExcavRuleNames;

            // Per-manhole excavation exception (by node AG_GUID) overrides soil + rule names.
            var mex = net.Exceptions?.FindManholeExcav(mh.AgGuid);
            if (mex != null) { soil = mex.SoilName ?? ""; names = mex.RuleNames; }

            return rules.Where(r =>
                (r.SelectedSoilNames == null || r.SelectedSoilNames.Count == 0 ||
                 string.IsNullOrEmpty(soil) || r.SelectedSoilNames.Contains(soil)) &&
                (names == null || names.Count == 0 || names.Contains(r.RuleName))).ToList();
        }

        private static void ResolveExcavation(
            ManholeItem mh, ComponentFamily family, BottomElementComponent taban,
            bool ekleTemelAltiParca, bool pitWidthUsesOuter,
            ref int excavUnresolvedCount, ref int steppedIgnoredCount)
        {
            if (taban == null) { excavUnresolvedCount++; return; }

            var rules = ManholeExcavationCatalogStore.Current;
            if (rules == null || rules.Count == 0) { excavUnresolvedCount++; return; }

            // RULES mode: keep only the rules this network's (or the manhole excav exception's) Zemin
            // Tipi + Kural Adı filter allow.
            rules = FilterManholeExcavRulesByNetwork(rules, mh);
            if (rules.Count == 0) { excavUnresolvedCount++; return; }

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

            // Pit width = Taban's own footprint (inner/nominal size, same scalar as
            // baseSizeMm above) + working clearance on both sides, and — when the
            // user chose "Dış Çap" (pitWidthUsesOuter) — the precast wall on both
            // sides too (user directive 2026-07-07). baseSizeMm above stays pure
            // inner/nominal regardless (rule matching + TemelAltiParca
            // BaglandiTabanCapiMm both key off the nominal diameter, not the
            // wall-inclusive outer size).
            double wallBothSidesM = pitWidthUsesOuter ? 2.0 * (taban.WallThicknessMm / 1000.0) : 0.0;
            double baseWidthM  = ResolveFootprintWidthM(taban.Footprint) + wallBothSidesM;
            double tabanThickM = taban.TabanKalinligiMm / 1000.0;

            // Baca Altı Beton Parçası (Eklesin/Yok, user directive 2026-07-07):
            // TemelAltiParcaComponent lives in the same family as the resolved
            // Taban and is matched purely by BaglandiTabanCapiMm == baseSizeMm
            // (the same shape-agnostic size scalar used for the excavation rule
            // match above — diameter for Circular, side for Square, longer side
            // for Rectangular). No match, or the setting is off, → 0 contribution.
            double temelAltiM = 0.0;
            mh.ResolvedSubBaseParts = new List<TemelAltiParcaComponent>();
            if (ekleTemelAltiParca && family != null)
            {
                var subBasePart = family.Components
                    .OfType<TemelAltiParcaComponent>()
                    .FirstOrDefault(c => Math.Abs(c.BaglandiTabanCapiMm - baseSizeMm) <= 1e-6);
                if (subBasePart != null)
                {
                    temelAltiM = subBasePart.EffectiveHeight / 1000.0;
                    mh.ResolvedSubBaseParts.Add(subBasePart);
                }
            }

            // mh.ExcavationDepth already holds the raw baseline set in
            // BoQParserService.ComputeManholeDepths (structural depth + the lowest
            // connected pipe's own wall thickness) — add the Taban slab, TemelAltiParca
            // thickness, and Alt Temel Katmanları on top of that.
            double finalDepth  = mh.ExcavationDepth + tabanThickM + temelAltiM + tier.TotalSubBaseDepthM;
            double baseSideM   = baseWidthM + 2.0 * tier.WorkingClearanceM;

            mh.ExcavationDepth        = finalDepth;
            mh.ExcavWorkingClearanceM = tier.WorkingClearanceM;
            mh.ExcavSlopeRatio        = tier.SlopeRatio;
            mh.ExcavBaseSideM         = baseSideM;
            mh.ExcavationVolume       = ManholeExcavationGeometry.ComputeFrustumVolume(
                baseSideM, finalDepth, tier.SlopeRatio);

            mh.ResolvedBackfillLayers = tier.BackfillLayers?.ToList() ?? new List<ManholeBackfillLayer>();
            mh.ResolvedSubBaseLayers  = tier.SubBaseLayers?.ToList()  ?? new List<SubBaseLayer>();

            // ── Dolgu-basis volume (user directive 2026-07-06) ──────────────────
            // Same pit shape (baseSideM/slope), same Taban/TemelAltiParca/Alt Temel
            // Katmanları additions — but re-run from mh.DolguBaselineDepth (ZDolgu top)
            // instead of the Kazı baseline (ZKazi top). Skipped when DolguInvalid.
            if (!mh.DolguInvalid)
            {
                double dolguFinalDepth = mh.DolguBaselineDepth + tabanThickM + temelAltiM + tier.TotalSubBaseDepthM;
                mh.DolguFinalDepth  = dolguFinalDepth;
                mh.DolguBasisVolume = ManholeExcavationGeometry.ComputeFrustumVolume(
                    baseSideM, dolguFinalDepth, tier.SlopeRatio);
            }
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

        // User correction 2026-07-06: the stacking calculation must always use
        // pure Etkin Yükseklik (EffectiveHeight) — NOT the UI grid's "Yükseklik"
        // display column (TotalHeightMm = EffectiveHeight + TabanKalinligiMm,
        // a convenience "installed height" shown only in Baca Parça Kataloğu's
        // component list). TabanKalinligiMm is still used separately for the
        // excavation PIT depth (ResolveExcavation) — that's a different budget
        // (ground-to-slab-bottom) from this one (the precast stack's own height
        // within mh.Depth).
        private static double TabanHeightM(BottomElementComponent taban)
            => taban.EffectiveHeight / 1000.0;

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
        /// 3. Greedily fill any remaining gap with diameter-matched Boyun
        ///    Bileziği pieces (largest first, as many as needed — same shape as
        ///    step 2). If a gap ≥ LeftoverTolerance (currently just under 6 cm,
        ///    the shortest available piece) still remains after both greedy
        ///    fills, it's flagged as unresolved rather than silently accepted.
        /// </summary>
        private static ManholeStackResult ComputeFamilyStack(
            double depth, int diameter, ComponentFamily family, BottomElementComponent taban,
            DepthTierRule tier, RingFillMode ringFillMode)
        {
            double shaftDiam = taban.TopOpeningDiameterMm;

            int konikMax = GetMaxCount(tier, ComponentRole.Reducer);
            var konikCandidates = konikMax == 0 ? new List<ReducerComponent>() : family.Components
                .OfType<ReducerComponent>()
                .Where(r => Math.Abs(r.BottomInnerDiameterMm - shaftDiam) < 1e-6)
                .OrderByDescending(r => r.EffectiveHeight)
                .ToList();

            // ── Pass 1: normal build, tallest matching Konik (or none) ─────────
            var stack = BuildStackAttempt(depth, diameter, family, taban, tier, ringFillMode,
                konikCandidates.Count > 0 ? konikCandidates[0] : null, forceMinimums: false,
                out int govdeUsed, out int boyunUsed, out bool isDegisken);

            if (isDegisken) return stack; // değişken absorbs the whole depth — Min-recompute below doesn't apply

            int govdeMin = GetMinCount(tier, ComponentRole.MiddleElement);
            int boyunMin = GetMinCount(tier, ComponentRole.Adjuster);
            if (govdeUsed >= govdeMin && boyunUsed >= boyunMin)
                return stack; // normal pass already satisfies both Min counts

            // ── Pass 2 (user directive 2026-07-06): a role's Min count wasn't met
            // by the normal fill (e.g. the exact depth happened to be closed by
            // Gövde alone, so Boyun — required at least once — never got used).
            // Recompute: place mandatory pieces, force Min pieces of each role's
            // SMALLEST available size, then fill whatever's left normally with
            // the remaining Max budget. If even the forced minimum doesn't fit
            // (negative leftover), retry with the next-shortest Konik variant of
            // the SAME diameter pair (tallest tried first, same as Pass 1) to
            // free up more room — only once every Konik height is exhausted does
            // this become an explicit, surfaced failure. ──
            var candidatesToTry = konikCandidates.Count > 0
                ? konikCandidates
                : new List<ReducerComponent> { null };
            foreach (var konikCandidate in candidatesToTry)
            {
                var forced = BuildStackAttempt(depth, diameter, family, taban, tier, ringFillMode,
                    konikCandidate, forceMinimums: true,
                    out int gU, out int bU, out bool deg, out bool infeasible);
                if (!infeasible) return forced;
            }

            stack.ConstraintViolated = true;
            stack.ConstraintViolationReason =
                "Zorunlu Min sayısına en kısa Konik ile bile ulaşılamadı — derinlik yetersiz";
            return stack;
        }

        /// <summary>
        /// Builds one complete manhole stack attempt: Taban/Konik(given)/Kapak/
        /// other-mandatory, then either the normal greedy/BestFit Gövde+Boyun
        /// fill, or (forceMinimums) places each role's Min-count at its SMALLEST
        /// available size FIRST, then fills whatever depth remains normally with
        /// the leftover Max budget. "değişken" (any mandatory piece marked
        /// IsVariable) always short-circuits both modes identically — it absorbs
        /// the entire remaining depth and Gövde/Boyun are never touched.
        /// </summary>
        private static ManholeStackResult BuildStackAttempt(
            double depth, int diameter, ComponentFamily family, BottomElementComponent taban,
            DepthTierRule tier, RingFillMode ringFillMode, ReducerComponent konikOverride,
            bool forceMinimums,
            out int govdeUsedCount, out int boyunUsedCount, out bool isDegisken)
        {
            return BuildStackAttempt(depth, diameter, family, taban, tier, ringFillMode,
                konikOverride, forceMinimums, out govdeUsedCount, out boyunUsedCount, out isDegisken,
                out _);
        }

        private static ManholeStackResult BuildStackAttempt(
            double depth, int diameter, ComponentFamily family, BottomElementComponent taban,
            DepthTierRule tier, RingFillMode ringFillMode, ReducerComponent konikOverride,
            bool forceMinimums,
            out int govdeUsedCount, out int boyunUsedCount, out bool isDegisken, out bool infeasible)
        {
            govdeUsedCount = 0; boyunUsedCount = 0; isDegisken = false; infeasible = false;

            var stack = new ManholeStackResult { NominalDiameter = diameter, IsPreCast = true };
            double shaftDiam = taban.TopOpeningDiameterMm;
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

            int kapakMax = GetMaxCount(tier, ComponentRole.Cover);

            // ── Step 1b: Konik (Reducer) — explicitly given by the caller (Pass 1
            // always uses the tallest matching candidate; Pass 2 retries shorter
            // ones), not looked up here. ──
            var konik = konikOverride;
            double neckDiam = shaftDiam; // no Konik → Boyun/Kapak fall back to matching the shaft directly
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
            {
                stack.ConstraintViolated = true;
                stack.ConstraintViolationReason = $"Konik (Min≥1) bulunamadı — şaft çapı {shaftDiam:0} mm ile eşleşen Konik yok";
            }

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
            {
                stack.ConstraintViolated = true;
                stack.ConstraintViolationReason = $"Kapak (Min≥1) bulunamadı — boyun çapı {neckDiam:0} mm ile eşleşen Kapak yok";
            }

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

            // ── Gövde/Boyun candidate pools ──
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
                    stack.Parts.Add(NewStackedPart(vp, perPieceHeight, 1, true, isDegisken: true));
                stack.ResidualM = 0;
                isDegisken = true;
                SortPartsByPhysicalOrder(stack);
                return stack;
            }

            double remaining = depth - fixedHeight;
            var variableRings = govdeCandidates
                .GroupBy(c => c.EffectiveHeight).Select(g => g.First())
                .OrderByDescending(c => c.EffectiveHeight).ToList();
            var boyunSizes = boyunCandidates
                .GroupBy(c => c.EffectiveHeight).Select(g => g.First())
                .OrderByDescending(c => c.EffectiveHeight).ToList();

            int govdeMax = GetMaxCount(tier, ComponentRole.MiddleElement);
            int boyunMax = GetMaxCount(tier, ComponentRole.Adjuster);
            int govdeMin = GetMinCount(tier, ComponentRole.MiddleElement);
            int boyunMin = GetMinCount(tier, ComponentRole.Adjuster);

            var ringUsage  = new Dictionary<double, RingUsageEntry>();
            var boyunUsage = new Dictionary<double, RingUsageEntry>();

            if (forceMinimums)
            {
                // Force each role's Min-count at its SMALLEST available size
                // first (user directive 2026-07-06), capped at Max if the
                // catalog's Min>Maks (a contradictory config — flagged naturally
                // below once actual usage is compared against Min again).
                if (govdeMin > 0)
                {
                    if (variableRings.Count == 0) { infeasible = true; return stack; }
                    int forcedCount = govdeMax >= 0 ? Math.Min(govdeMin, govdeMax) : govdeMin;
                    var smallest = variableRings.OrderBy(c => c.EffectiveHeight).First();
                    double hM = smallest.EffectiveHeight / 1000.0;
                    ringUsage[hM] = new RingUsageEntry { Component = smallest, Count = forcedCount };
                    remaining -= hM * forcedCount;
                    govdeUsedCount = forcedCount;
                }
                if (boyunMin > 0)
                {
                    if (boyunSizes.Count == 0) { infeasible = true; return stack; }
                    int forcedCount = boyunMax >= 0 ? Math.Min(boyunMin, boyunMax) : boyunMin;
                    var smallest = boyunSizes.OrderBy(c => c.EffectiveHeight).First();
                    double hM = smallest.EffectiveHeight / 1000.0;
                    boyunUsage[hM] = new RingUsageEntry { Component = smallest, Count = forcedCount };
                    remaining -= hM * forcedCount;
                    boyunUsedCount = forcedCount;
                }
                if (remaining < 0) { infeasible = true; return stack; }

                int govdeRemainingMax = govdeMax < 0 ? -1 : Math.Max(0, govdeMax - govdeUsedCount);
                if (remaining > 0 && variableRings.Count > 0 && govdeRemainingMax != 0)
                {
                    FillGap(variableRings, govdeRemainingMax, ringFillMode, ref remaining, ringUsage, out int extraGovde);
                    govdeUsedCount += extraGovde;
                }
                int boyunRemainingMax = boyunMax < 0 ? -1 : Math.Max(0, boyunMax - boyunUsedCount);
                if (remaining > 0 && boyunSizes.Count > 0 && boyunRemainingMax != 0)
                {
                    FillGap(boyunSizes, boyunRemainingMax, ringFillMode, ref remaining, boyunUsage, out int extraBoyun);
                    boyunUsedCount += extraBoyun;
                }
            }
            else
            {
                if (remaining > 0 && variableRings.Count > 0)
                    FillGap(variableRings, govdeMax, ringFillMode, ref remaining, ringUsage, out govdeUsedCount);
                if (boyunMax != 0 && boyunSizes.Count > 0 && remaining > 0)
                    FillGap(boyunSizes, boyunMax, ringFillMode, ref remaining, boyunUsage, out boyunUsedCount);
            }

            if (govdeUsedCount < govdeMin)
            {
                stack.ConstraintViolated = true;
                stack.ConstraintViolationReason =
                    $"Gövde Halkası sayısı yetersiz (Min={govdeMin}, kullanılan={govdeUsedCount}) — Maks={(govdeMax < 0 ? "∞" : govdeMax.ToString())}";
            }
            if (boyunUsedCount < boyunMin)
            {
                stack.ConstraintViolated = true;
                stack.ConstraintViolationReason =
                    $"Boyun bileziği sayısı yetersiz (Min={boyunMin}, kullanılan={boyunUsedCount}) — Maks={(boyunMax < 0 ? "∞" : boyunMax.ToString())}";
            }

            stack.ResidualM = Math.Max(0, remaining);
            // Final check: even after every role's Max cap was respected, the
            // target depth couldn't be reached — the exact "used the max
            // allowed count per piece but still short" case the user asked
            // about. Never silently swallowed (see ConstraintViolated doc).
            if (stack.ResidualM > LeftoverTolerance)
            {
                stack.ConstraintViolated = true;
                stack.ConstraintViolationReason =
                    $"hedef derinliğe {stack.ResidualM:0.###} m eksik kaldı " +
                    $"(Gövde Maks={(govdeMax < 0 ? "∞" : govdeMax.ToString())}, Boyun Maks={(boyunMax < 0 ? "∞" : boyunMax.ToString())})";
            }

            foreach (var kv in ringUsage.OrderByDescending(k => k.Key))
                if (kv.Value.Count > 0) stack.Parts.Add(NewStackedPart(kv.Value.Component, kv.Key, kv.Value.Count, true));
            foreach (var kv in boyunUsage.OrderByDescending(k => k.Key))
                if (kv.Value.Count > 0) stack.Parts.Add(NewStackedPart(kv.Value.Component, kv.Key, kv.Value.Count, true));

            SortPartsByPhysicalOrder(stack);
            return stack;
        }

        // Builds a StackedPart carrying the underlying catalog component's PozNo/
        // Aciklama/volumes — proves the stack is actually catalog-driven downstream.
        // isDegisken (user directive 2026-07-06): a "değişken" component's
        // catalog MaterialVolume/ExternalVolume represent the RATE for 1 metre
        // of height, not a fixed total — scale by the actual computed height.
        // Non-değişken components keep the catalog value as-is (a fixed total
        // for that component's own EffectiveHeight), unchanged from before.
        //
        // Taban exception (user directive 2026-07-08): a değişken Taban is NOT a
        // pure wall — it has a fixed-thickness floor slab whose volume does not
        // scale with the (wall-only) height. So its catalog ExternalVolume/
        // MaterialVolume are the WALL rate per 1 m, and the floor is a separate
        // fixed volume (FloorExternalVolume/FloorMaterialVolume) added on top:
        //   volume = wallRate × heightM + floorVolume.
        // heightM here is the wall height already (EffectiveHeight, which excludes
        // TabanKalinligiMm — the slab is tracked separately for the pit depth), so
        // nothing is subtracted. This is the single point where per-unit volumes
        // are computed; every consumer (tables, Excel, backfill StructureVolume,
        // BOM totals, DWG serialization) reads the resulting UnitXxxVolume.
        private static StackedPart NewStackedPart(
            ManholeComponent component, double heightM, int count, bool isVariableRing,
            bool isDegisken = false)
        {
            double unitMaterial, unitExternal;
            var bottom = component as BottomElementComponent;
            if (isDegisken && bottom != null)
            {
                unitMaterial = component.MaterialVolume * heightM + bottom.FloorMaterialVolume;
                unitExternal = component.ExternalVolume * heightM + bottom.FloorExternalVolume;
            }
            else if (isDegisken)
            {
                unitMaterial = component.MaterialVolume * heightM;
                unitExternal = component.ExternalVolume * heightM;
            }
            else
            {
                unitMaterial = component.MaterialVolume;
                unitExternal = component.ExternalVolume;
            }

            return new StackedPart
            {
                PartName          = component.Name,
                HeightM           = heightM,
                Count             = count,
                IsVariableRing    = isVariableRing,
                PozNo             = component.PozNo,
                Aciklama          = component.Aciklama,
                UnitMaterialVolume = unitMaterial,
                UnitExternalVolume = unitExternal,
                WallThicknessMm    = ResolveWallThicknessMm(component),
                Role               = component.Role
            };
        }

        // "değişken" (variable-height) pieces are appended to stack.Parts after ALL fixed
        // pieces regardless of their real physical position (see ComputeFamilyStack) — e.g.
        // a değişken Taban ends up after a fixed Konik/Kapak even though it physically sits
        // at the bottom. Sort by ComponentRole (already declared bottom-to-top: BottomElement,
        // MiddleElement, Reducer, Adjuster, Cover) so consumers that need true vertical order
        // (e.g. PipeNetLengthService's per-ring Z-band lookup) get it right. Stable sort keeps
        // same-role pieces (e.g. multiple Gövde ring sizes) in their existing relative order.
        //
        // TemelAltiParca was appended to the enum AFTER Cover (to avoid renumbering
        // existing roles) but sits physically BELOW BottomElement — RoleSortKey special-
        // cases it to -1 instead of relying on the raw enum int value.
        private static void SortPartsByPhysicalOrder(ManholeStackResult stack)
            => stack.Parts = stack.Parts.OrderBy(p => RoleSortKey(p.Role)).ToList();

        private static int RoleSortKey(ComponentRole role)
            => role == ComponentRole.TemelAltiParca ? -1 : (int)role;

        /// <summary>Wall thickness (mm) of the underlying component, for types that track one.
        /// Used by PipeNetLengthService to compute a manhole's outer-shell radius at a given
        /// pipe invert elevation. Null for component types with no wall-thickness concept (e.g. Kapak).</summary>
        private static double? ResolveWallThicknessMm(ManholeComponent component)
        {
            switch (component)
            {
                case BottomElementComponent b: return b.WallThicknessMm;
                case MiddleElementComponent m:  return m.WallThicknessMm;
                case AdjusterComponent a:       return a.WallThicknessMm;
                case ReducerComponent r:        return r.WallThicknessMm;
                default:                        return null;
            }
        }

        // ── Small helper to avoid tuples in .NET 4.8 ─────────────────────────
        private sealed class RingUsageEntry
        {
            public ManholeComponent Component { get; set; }
            public int              Count     { get; set; }
        }

        // ── Gövde/Boyun gap-fill (user setting, Ayarlar dialog 2026-07-06) ─────

        /// <summary>
        /// Fills as much of <paramref name="remaining"/> as possible using
        /// <paramref name="sizes"/> (distinct-height components of one role),
        /// respecting <paramref name="maxCount"/> (total pieces across all sizes;
        /// -1 = unlimited, 0 = none). Dispatches to <see cref="BestFitFill"/> or
        /// the original greedy largest-first loop depending on
        /// <paramref name="mode"/>. Adds the chosen pieces into
        /// <paramref name="usage"/>, decrements <paramref name="remaining"/> by
        /// whatever was achieved, and reports the total piece count used.
        /// </summary>
        private static void FillGap(
            IEnumerable<ManholeComponent> sizes, int maxCount, RingFillMode mode,
            ref double remaining, Dictionary<double, RingUsageEntry> usage, out int usedCount)
        {
            usedCount = 0;
            if (maxCount == 0) return;

            if (mode == RingFillMode.BestFit)
            {
                var bestUsage = BestFitFill(sizes, remaining, maxCount, out double achievedM);
                foreach (var kv in bestUsage) AddUsage(usage, kv.Value.Component, kv.Key, kv.Value.Count);
                usedCount = bestUsage.Values.Sum(v => v.Count);
                remaining -= achievedM;
                return;
            }

            // Greedy (original): largest size first, as many of that size as fit,
            // then move to the next-smaller size.
            foreach (var size in sizes)
            {
                if (maxCount >= 0 && usedCount >= maxCount) break;
                double hM = size.EffectiveHeight / 1000.0;
                if (hM <= 1e-9) continue;
                int count = (int)(remaining / hM);
                if (maxCount >= 0) count = Math.Min(count, maxCount - usedCount);
                if (count > 0)
                {
                    AddUsage(usage, size, hM, count);
                    remaining -= count * hM;
                    usedCount += count;
                }
            }
        }

        /// <summary>
        /// Adds to an existing usage entry at this height instead of overwriting
        /// it — needed since the forced-minimum pass (Pass 2, user directive
        /// 2026-07-06) pre-populates <paramref name="usage"/> with each role's
        /// Min-count at its smallest size BEFORE calling FillGap to fill
        /// whatever's left, and the leftover fill may legitimately want more of
        /// that exact same size.
        /// </summary>
        private static void AddUsage(
            Dictionary<double, RingUsageEntry> usage, ManholeComponent component, double heightM, int count)
        {
            if (usage.TryGetValue(heightM, out var existing)) existing.Count += count;
            else usage[heightM] = new RingUsageEntry { Component = component, Count = count };
        }

        /// <summary>
        /// Bounded-knapsack search: among every combination of <paramref
        /// name="sizeComponents"/> (each reusable any number of times, TOTAL
        /// piece count ≤ <paramref name="maxCount"/> when ≥0) finds the one
        /// whose sum is the closest achievable value ≤ <paramref
        /// name="targetM"/> — e.g. an 8cm + 7cm combination closing a 15cm gap
        /// exactly instead of a single 10cm piece leaving 5cm unclosed.
        ///
        /// Implementation: classic minimum-coins DP over depth in whole
        /// millimetres (`minCount[d]` = fewest pieces to reach exactly d mm, or
        /// unreachable). The largest d ≤ target with `minCount[d] ≤ maxCount` is
        /// the answer — if the CHEAPEST way to reach some depth already needs
        /// more pieces than allowed, no combination reaches it within budget
        /// either, so a 1-D DP suffices (no need to track piece-count as a
        /// second dimension). Depth is at most a few thousand mm and the size
        /// list has a handful of entries, so this runs in well under a
        /// millisecond per manhole.
        /// </summary>
        private static Dictionary<double, RingUsageEntry> BestFitFill(
            IEnumerable<ManholeComponent> sizeComponents, double targetM, int maxCount,
            out double achievedM)
        {
            var usage = new Dictionary<double, RingUsageEntry>();
            achievedM = 0;

            var heights = sizeComponents
                .Select(c => (Mm: (int)Math.Round(c.EffectiveHeight), Comp: c))
                .Where(x => x.Mm > 0)
                .ToList();
            int targetMm = (int)Math.Round(targetM * 1000.0);
            if (targetMm <= 0 || heights.Count == 0) return usage;

            const int INF = int.MaxValue / 2;
            var minCount = new int[targetMm + 1];
            var choice   = new int[targetMm + 1];
            for (int d = 1; d <= targetMm; d++) { minCount[d] = INF; choice[d] = -1; }

            for (int d = 1; d <= targetMm; d++)
                for (int hi = 0; hi < heights.Count; hi++)
                {
                    int h = heights[hi].Mm;
                    if (h > d) continue;
                    int prev = minCount[d - h];
                    if (prev >= INF) continue;
                    if (prev + 1 < minCount[d]) { minCount[d] = prev + 1; choice[d] = hi; }
                }

            int bestD = 0;
            for (int d = targetMm; d >= 1; d--)
            {
                if (minCount[d] < INF && (maxCount < 0 || minCount[d] <= maxCount))
                {
                    bestD = d;
                    break;
                }
            }

            int cursor = bestD;
            while (cursor > 0 && choice[cursor] >= 0)
            {
                int hi = choice[cursor];
                double hM = heights[hi].Mm / 1000.0;
                if (!usage.TryGetValue(hM, out var entry))
                    usage[hM] = entry = new RingUsageEntry { Component = heights[hi].Comp, Count = 0 };
                entry.Count++;
                cursor -= heights[hi].Mm;
            }

            achievedM = bestD / 1000.0;
            return usage;
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
