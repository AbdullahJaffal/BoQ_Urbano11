using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.Models
{
    /// <summary>
    /// One entry from Urbano's own embedded PIPE/MANHOLE catalog (inside the
    /// project's own &lt;catalogs&gt; block of an ars_export_xml file) — the "as-drawn"
    /// side of the Type Mapping link. See BoQParserService.ListCatalogItems.
    /// </summary>
    public class CatalogItemInfo
    {
        public string Guid      { get; set; } = "";
        public string Name      { get; set; } = "";
        /// <summary>"PIPE" or "MANHOLE".</summary>
        public string Reference { get; set; } = "";
        /// <summary>
        /// Name of the enclosing Urbano catalog group (isGroup="1" parent), e.g.
        /// "UYG_YSU_UZ (KRG - CTP)". Two items can share the same diameter/name but
        /// belong to different groups — shown alongside Name so the user linking
        /// them in Type Mapping doesn't pick the wrong one. Empty if the item isn't
        /// nested under a group.
        /// </summary>
        public string GroupName { get; set; } = "";
        /// <summary>As-drawn shape (MANHOLE items only; CBS_MANHOLESHAPE-derived). Circular by default.</summary>
        public FootprintShape Shape { get; set; } = FootprintShape.Circular;
        /// <summary>Drawn long side / A (m) — populated when Shape is Square or Rectangular.</summary>
        public double LengthM { get; set; }
        /// <summary>Drawn short side / B (m) — populated when Shape is Square or Rectangular.</summary>
        public double WidthM { get; set; }
    }

    /// <summary>
    /// A CAD-selection calculation scope: the manholes/pipes the user picked from
    /// the drawing (URBANO_BOQ_CADSELECT). When present, URBANO_BOQ_HESAPLA restricts
    /// the geometry, clash detection and output to the sections these resolve to
    /// (see <c>BoQParserService.ApplySelectionScope</c>). Manhole depths are still
    /// computed from ALL connected pipes (physically correct), only clash+output are
    /// scoped. Null / <see cref="IsEmpty"/> => whole-active-network calc, as before.
    ///
    /// Raw picked data is captured here (GUIDs + pipe line endpoints); it is resolved
    /// into actual sections later, once the parser has the sn/en topology, so no
    /// dependency on parse order is baked into the selection command.
    /// </summary>
    public sealed class SelectionScope
    {
        /// <summary>AG_GUID of every selected MANHOLE entity (circle / block reference).</summary>
        public HashSet<string> ManholeGuids { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>AG_GUID of every selected PIPE entity that carried one.</summary>
        public HashSet<string> PipeGuids { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Endpoints {x1,y1,x2,y2} of each selected pipe line — geometry fallback for
        /// pipes whose drawing entity has no usable GUID (matched to a section's two
        /// node coordinates within tolerance, mirroring UrbanoLock's RestoreScopeSelector).
        /// </summary>
        public List<double[]> PipeSegments { get; } = new List<double[]>();

        public bool IsEmpty =>
            ManholeGuids.Count == 0 && PipeGuids.Count == 0 && PipeSegments.Count == 0;
    }

    /// <summary>
    /// Calculation-scope mode chosen in the Metraj window's split button:
    /// <see cref="Full"/> = all active networks, <see cref="Cad"/> = a fresh drawing
    /// selection, <see cref="Son"/> = reuse the last CAD selection (this session).
    /// </summary>
    public enum ScopeMode { None, Full, Cad, Son }

    // =========================================================================
    // Phase 1 – Pipe and Manhole aggregation models
    // =========================================================================

    public class PipeItem
    {
        public int    Diameter                { get; set; }  // mm
        public string Material                { get; set; }
        public double TotalLength             { get; set; }  // m  (2-D from @pos coordinates)
        public double TotalExcavationVolume   { get; set; }  // m³  total trench excavation (after clash deduction)
        public double TotalBeddingVolume      { get; set; }  // m³  bedding sand layer
        public double TotalSurroundVolume     { get; set; }  // m³  pipe surround sand (net of pipe)
        public double TotalBackfillVolume     { get; set; }  // m³  compacted backfill (after clash deduction)
        public double OverlapExcavDeducted    { get; set; }  // m³  kazı  deducted due to trench clash
        public double OverlapBackfillDeducted { get; set; }  // m³  dolgu deducted due to trench clash
    }

    /// <summary>
    /// One row = one physical manhole node (no grouping).
    /// Phase 2 fields are populated by ManholeAIService.Process().
    /// </summary>
    public class ManholeItem
    {
        // ── Identity & geometry ───────────────────────────────────────────────
        public string NodeName           { get; set; }  // AG_NAME  (e.g. "1Y", "4Y")
        public double X                  { get; set; }  // easting  (m)
        public double Y                  { get; set; }  // northing (m)
        public double TerrainElevation   { get; set; }  // TH1      (m, a.s.l.) — this is actually the COVER-TOP elevation (Kapak Üstü Kotu). Display only.
        /// <summary>Existing ground elevation (m, a.s.l.) — TH2. Falls back to TerrainElevation (TH1) when TH2 is absent from the export. Display only.</summary>
        public double ExistingGroundElevation { get; set; }

        // ── Settings-resolved elevations (Kot Ayarları / Seviye Ayarları) ────
        // Each is whichever THn the user's Genel Ayarlar mapping resolves to for
        // that role — NOT hardcoded to TH1. See BoQParserService.ResolveElevations.
        /// <summary>Elevation used as the excavation (Kazı) top reference.</summary>
        public double ZKazi      { get; set; }
        /// <summary>Elevation used as the backfill (Dolgu) top reference (e.g. under-asphalt/Terrasman level).</summary>
        public double ZDolgu     { get; set; }
        /// <summary>Elevation used as the manhole cover (Baca Kapak) reference.</summary>
        public double ZBacaKapak { get; set; }

        public double Depth              { get; set; }  // computed manhole depth (m), from ZBacaKapak
        public int    Diameter           { get; set; }  // nominal shaft Ø (mm)
        /// <summary>Urbano's own MANHOLE catalog item GUID (the "MH" node property) — used to resolve a Type Mapping link.</summary>
        public string MhGuid             { get; set; }

        /// <summary>The topology node's AG_GUID (@g) — the key a per-network manhole exception matches on.</summary>
        public string AgGuid             { get; set; }

        /// <summary>Owning network name (SectionDebugRow.SystemName) — set at build; needed to look up per-network project rules.</summary>
        public string SystemName         { get; set; }

        /// <summary>
        /// The ACTUAL footprint of the resolved precast Taban component (set by
        /// ManholeAIService.ResolveFamilyAndTaban, right after Diameter/DrawnLengthM/
        /// DrawnWidthM are back-filled). Null until a Taban resolves.
        ///
        /// NOT the same thing as DrawnShape/DrawnLengthM/DrawnWidthM above — those
        /// reflect how the manhole was AS-DRAWN in Urbano/Civil3D, which can differ
        /// from the shape of the precast family it's actually linked to (e.g. drawn
        /// as a generic circle in the DWG but built from a square "Izgara" Taban).
        /// PipeNetLengthService needs the real built shape, so it uses this field
        /// instead of DrawnShape/Diameter.
        /// </summary>
        public Footprint ResolvedFootprint { get; set; }

        /// <summary>
        /// Catalog sub-base components (TemelAltiParcaComponent) linked to the resolved Taban
        /// via BaglandiTabanCapiMm. Empty when none are defined in the catalog or no Taban
        /// is resolved yet. Set by ManholeAIService alongside the excavation-depth calculation.
        /// </summary>
        public List<TemelAltiParcaComponent> ResolvedSubBaseParts { get; set; } = new List<TemelAltiParcaComponent>();

        /// <summary>
        /// The matched ManholeExcavationDepthTier's Alt Temel Katmanları (sub-base
        /// preparation layers, e.g. "yataklama kumu"), set by ManholeAIService.
        /// ResolveExcavation alongside ResolvedBackfillLayers. Consumed by
        /// ManholeExcavOverlapService.Compute to derive SubBaseVolume (Simpson's
        /// rule frustum at the pit bottom) — must be persisted so the Load()-only
        /// Baca Kesif Tablosu reload path can re-run that computation.
        /// </summary>
        public List<SubBaseLayer> ResolvedSubBaseLayers { get; set; } = new List<SubBaseLayer>();

        /// <summary>
        /// As-drawn shape from Urbano's MANHOLE catalog item (CBS_MANHOLESHAPE: "Dairesel"
        /// → Circular, "Dörtgen" → Square when MANHOLE_A≈MANHOLE_B else Rectangular).
        /// Defaults to Circular when the property is absent (older exports / no shape data).
        /// </summary>
        public FootprintShape DrawnShape { get; set; } = FootprintShape.Circular;
        /// <summary>Drawn long side / A (m) — populated when DrawnShape is Square or Rectangular.</summary>
        public double DrawnLengthM       { get; set; }
        /// <summary>Drawn short side / B (m) — populated when DrawnShape is Square or Rectangular.</summary>
        public double DrawnWidthM        { get; set; }

        /// <summary>
        /// Urbano's own node rotation, radians, standard math CCW-from-+X
        /// convention — confirmed 2026-07-06 against a real manually-rotated
        /// manhole (stored value decoded to exactly 315°, matching the user's own
        /// recollection). Sourced from the node's "NR" property (explicit manual
        /// override) when present, else "NLR" (Urbano's always-present current/
        /// effective rotation). Null when neither exists (very old export) —
        /// ManholeExcavOverlapService.ComputeRotationAngle then falls back to its
        /// bisector-of-connected-pipes heuristic. This is now the PRIMARY source
        /// for manhole square/rectangle orientation, superseding that heuristic
        /// whenever Urbano actually provides it.
        /// </summary>
        public double? RotationAngleRad  { get; set; }

        /// <summary>
        /// Display string for "Çap (mm)" columns: the plain diameter number for
        /// Circular (unchanged), "side×side" for Square, "length×width" for
        /// Rectangular — since a single Diameter number never applies to a
        /// non-circular manhole (Diameter stays 0 for those; DrawnLengthM/
        /// DrawnWidthM carry the real drawn size instead).
        /// </summary>
        public string DiameterDisplay
        {
            get
            {
                switch (DrawnShape)
                {
                    case FootprintShape.Square:
                        return $"{Math.Round(DrawnLengthM * 1000)}×{Math.Round(DrawnLengthM * 1000)}";
                    case FootprintShape.Rectangular:
                        return $"{Math.Round(DrawnLengthM * 1000)}×{Math.Round(DrawnWidthM * 1000)}";
                    default:
                        return Diameter > 0 ? Diameter.ToString() : "?";
                }
            }
        }

        // kept for summary totals / backwards compat
        public int    Count              { get; set; } = 1;
        public double TotalDepth         => Depth;
        public double AverageDepth       => Depth;

        // ── Phase 2 – Manhole AI topology ────────────────────────────────────
        /// <summary>Number of inlet sections that are NOT drop-pipes.</summary>
        public int    ValidInletCount    { get; set; }
        /// <summary>Number of outlet sections.</summary>
        public int    ValidOutletCount   { get; set; }
        /// <summary>True if at least one inlet is classified as a drop-pipe.</summary>
        public bool   HasDropPipe        { get; set; }
        /// <summary>
        /// Human-readable type string generated by ManholeAIService.
        /// Example: "Baca O1000 - (2 Giris / 1 Cikis)"
        /// Null until ManholeAIService.Process() has been called.
        /// </summary>
        public string SmartTypeName      { get; set; }
        /// <summary>Pre-cast stacking result. Null if catalog missing or AI not yet run.</summary>
        public ManholeStackResult StackPreCast     { get; set; }
        /// <summary>Cast-in-place stacking result (ConcreteDepth). Null if AI not yet run.</summary>
        public ManholeStackResult StackCastInPlace { get; set; }
        /// <summary>Backward-compat accessor (PreCast wins if available).</summary>
        public ManholeStackResult Stack => StackPreCast ?? StackCastInPlace;

        // ── Manhole vs Manhole geometric states ──────────────────────────────
        /// <summary>
        /// Lower segment: ZBottom → zTouch. No overlap exists here.
        /// Null when zTouch == ZBottom (no lower zone).
        /// </summary>
        public ManholeGeoSegment GeoLower { get; set; }
        /// <summary>
        /// Upper segment: zTouch → ZTop. Overlap zone; carries Inside/Outside split.
        /// </summary>
        public ManholeGeoSegment GeoUpper { get; set; }

        // ── Manhole excavation & backfill (Phase 7 — ManholeExcavationCatalog) ──
        /// <summary>
        /// Excavation depth (m). Raw baseline = TerrainElevation − lowest connected
        /// pipe invert + MHB (set in BoQParserService.ComputeManholeDepths — this
        /// part never depends on the catalog). Once ManholeAIService resolves a
        /// matching ManholeExcavationRule/DepthTier, this is replaced with the full
        /// depth = raw baseline + Taban.TabanKalinligiMm + the tier's
        /// TotalSubBaseDepthM (Alt Temel Katmanları). Stays at the raw baseline
        /// (explicit-failure convention, never guessed) if no rule/tier matches.
        /// </summary>
        public double ExcavationDepth  { get; set; }
        /// <summary>
        /// Isolated manhole excavation volume (m³), square frustum via Simpson's
        /// 1/3 rule using <see cref="ExcavBaseSideM"/>/<see cref="ExcavSlopeRatio"/>.
        /// 0 until ManholeAIService resolves a matching catalog rule (explicit
        /// failure — never a guessed/default value). Does NOT yet deduct
        /// trench-overlap zones (see ManholeExcavOverlapService).
        /// </summary>
        public double ExcavationVolume { get; set; }

        /// <summary>
        /// Backfill-basis baseline depth (m), mirrors <see cref="ExcavationDepth"/>'s
        /// raw baseline but measured from <see cref="ZDolgu"/> instead of
        /// <see cref="ZKazi"/> — set in BoQParserService.ComputeManholeDepths.
        /// Never clamped to 0: a negative value means Dolgu Seviyesi resolves below
        /// the lowest connected invert (or the manhole floor), which is a design
        /// error, not a legitimate "shallow backfill" case (see DolguInvalid).
        /// </summary>
        public double DolguBaselineDepth { get; set; }
        /// <summary>
        /// True when DolguBaselineDepth (or the per-station Dolgu top along a
        /// connected pipe) resolved below the invert/manhole floor — an invalid
        /// Dolgu Seviyesi configuration. DolguBasisVolume/BackfillVolume stay 0
        /// and a note is added to BoQReport.DiscoveryNotes naming this manhole.
        /// </summary>
        public bool DolguInvalid { get; set; }
        /// <summary>
        /// Same square-frustum volume formula as <see cref="ExcavationVolume"/>
        /// (same ExcavBaseSideM/ExcavSlopeRatio — same pit shape), but using the
        /// Dolgu-basis depth (ZDolgu top) instead of the Kazı-basis depth (ZKazi
        /// top). This is the raw "backfill basis" volume before the
        /// structure/bedding/surround deductions in ManholeExcavOverlapService are
        /// applied to produce the final BackfillVolume. 0 until resolved or when
        /// DolguInvalid is true.
        /// </summary>
        public double DolguBasisVolume { get; set; }
        /// <summary>
        /// Total Dolgu-basis pit depth (ZDolgu top down to the same floor as
        /// ExcavationDepth: DolguBaselineDepth + Taban slab + Alt Temel Katmanları),
        /// set alongside DolguBasisVolume in ManholeAIService.ResolveExcavation.
        /// Used by ManholeExcavOverlapService to bound the Dolgu-range bedding/
        /// surround overlap slice ([ZDolgu − DolguFinalDepth, ZDolgu]).
        /// </summary>
        public double DolguFinalDepth { get; set; }

        /// <summary>Working clearance (m) each side of the Taban footprint, from the
        /// matched ManholeExcavationDepthTier. 0 if unresolved.</summary>
        public double ExcavWorkingClearanceM { get; set; }
        /// <summary>H:V slope ratio for this manhole's excavation pit, from the
        /// matched tier. 0 if unresolved.</summary>
        public double ExcavSlopeRatio { get; set; }
        /// <summary>
        /// Square excavation pit base side (m) = resolved Taban footprint width
        /// (diameter/side/long-side, whichever shape) + 2×ExcavWorkingClearanceM.
        /// Consumed by ManholeExcavOverlapService so both services build the exact
        /// same pit geometry instead of two independently-hardcoded copies. 0 if
        /// unresolved (ManholeExcavOverlapService then skips this manhole cleanly).
        /// </summary>
        public double ExcavBaseSideM { get; set; }

        /// <summary>
        /// The matched DepthTier's Geri Dolgu (backfill-around-shaft) layers,
        /// carried from ManholeAIService's catalog resolution so
        /// ManholeExcavOverlapService (which computes BackfillVolume) can split it
        /// into named layers once the total volume is known. Empty if unresolved.
        /// </summary>
        public List<ManholeBackfillLayer> ResolvedBackfillLayers { get; set; } = new List<ManholeBackfillLayer>();

        /// <summary>
        /// Geri Dolgu volume split by named layer (thickness-ratio, mirrors the
        /// pipe-trench layer-split design) — Volume populated once BackfillVolume
        /// is known. Empty if unresolved.
        /// </summary>
        public List<TrenchLayerSplit> BackfillLayerSplits { get; set; } = new List<TrenchLayerSplit>();

        /// <summary>
        /// Total spatial displacement of all stacked manhole parts (m³) =
        /// Σ (UnitExternalVolume × Count). Populated by ManholeExcavOverlapService.Compute.
        /// </summary>
        public double StructureVolume { get; set; }

        /// <summary>
        /// Volume of Alt Temel Katmanları at the pit bottom, computed via Simpson's 1/3
        /// rule on the frustum slice [zBottom, zBottom+TotalSubBaseDepthM].
        /// Displayed as "Baca Yatakalama". Populated by ManholeExcavOverlapService.Compute.
        /// </summary>
        public double SubBaseVolume { get; set; }

        /// <summary>
        /// Baca Geri Dolgu (m³) = DolguBasisVolume − StructureVolume − SubBaseVolume
        ///                       − Σ ManholeBeddingDeducted − Σ ManholeSurroundDeducted
        /// for all pipe sections connected to this manhole. Uses DolguBasisVolume
        /// (ZDolgu top), NOT ExcavationVolume (ZKazi top). 0 when DolguInvalid.
        /// Populated by ManholeExcavOverlapService.Compute (runtime only).
        /// </summary>
        public double BackfillVolume { get; set; }

        /// <summary>
        /// This manhole's own share of excavation volume that overlaps with every
        /// adjacent manhole pit (Bacalar Arası Kazı Çakışması), summed across all
        /// overlapping neighbours. Computed via the half-plane geometric split in
        /// ManholeExcavOverlapService.ComputeManholeVsManhole (runtime only, reset
        /// and re-accumulated on every call). 0 when no overlap.
        /// </summary>
        public double ManholeVsManholeExcavDeducted { get; set; }

        /// <summary>
        /// Same as <see cref="ManholeVsManholeExcavDeducted"/> but computed
        /// independently against the Dolgu-basis Z-range (ZDolgu/DolguFinalDepth)
        /// instead of the Kazı-basis range — the two pits can differ in extent,
        /// same independence convention as everywhere else in this pipeline.
        /// </summary>
        public double ManholeVsManholeBackfillDeducted { get; set; }

        // ── Baca Keşif Tablosu — inlet/outlet linkage (populated by ManholeConnectionLinkService) ──
        /// <summary>Connected inlet pipes (pipes whose EndNodeName == this manhole). Empty until ManholeConnectionLinkService.Populate() runs.</summary>
        public List<ManholeConnectionInfo> Inlets  { get; set; } = new List<ManholeConnectionInfo>();
        /// <summary>Connected outlet pipes (pipes whose StartNodeName == this manhole). Empty until ManholeConnectionLinkService.Populate() runs.</summary>
        public List<ManholeConnectionInfo> Outlets { get; set; } = new List<ManholeConnectionInfo>();

        /// <summary>
        /// Sum of SectionDebugRow.ManholeExcavDeducted across every connected pipe
        /// (inlets + outlets) — the total volume where this manhole's excavation
        /// frustum overlaps its connected pipe trenches (already computed per pipe
        /// by ManholeExcavOverlapService.Compute; this is just that same value
        /// summed per manhole). Populated by ManholeConnectionLinkService.Populate(),
        /// which must run AFTER ManholeExcavOverlapService.Compute.
        /// </summary>
        public double TotalPipeExcavOverlap { get; set; }
    }

    /// <summary>One pipe connection to a manhole — either an inlet or an outlet (see ManholeItem.Inlets/Outlets).</summary>
    public class ManholeConnectionInfo
    {
        public string NeighborNodeName { get; set; }
        /// <summary>Invert elevation (m, a.s.l.) at THIS manhole's end of the pipe.</summary>
        public double InvertElevation  { get; set; }
        public int    DiameterMm       { get; set; }
        /// <summary>Straight-line distance (m) to the neighboring manhole — SectionDebugRow.Length2D.</summary>
        public double Distance2D       { get; set; }
        /// <summary>Net pipe length (m), excluding both manholes' outer-shell radii —
        /// SectionDebugRow.NetLength. 0 unless PipeNetLengthService.Compute(report) has
        /// been run (runtime-only, not persisted; the standalone Baca Kesif command
        /// re-runs it after DwgBoQStore.Load()).</summary>
        public double NetLength        { get; set; }
        /// <summary>SectionDebugRow.SystemName of the connecting pipe — used by
        /// ManholeKesifExportService to detect a cross-system connection (this pipe's
        /// system differs from the manhole's own sheet/system).</summary>
        public string SystemName       { get; set; }
    }

    /// <summary>
    /// Geometric state of a manhole excavation footprint at one Z elevation.
    /// </summary>
    public class ManholeGeoLevel
    {
        /// <summary>Elevation (m a.s.l.) this level represents.</summary>
        public double Z { get; set; }

        /// <summary>Original unmodified footprint ring at this Z.</summary>
        public List<double[]> RawPoly { get; set; }

        /// <summary>
        /// This manhole's assigned share of the intersection zone at this Z.
        /// Null for lower-segment levels (no intersection below zTouch).
        /// </summary>
        public List<List<double[]>> InsideRegion { get; set; }

        /// <summary>
        /// Part of RawPoly that lies entirely OUTSIDE the total intersection zone.
        /// = Difference(RawPoly, totalIntersect_at_z).
        /// For lower-segment levels this equals the full RawPoly.
        /// </summary>
        public List<List<double[]>> OutsideRegion { get; set; }
    }

    /// <summary>
    /// Three Simpson's integration levels (Bottom / Mid / Top) covering one
    /// vertical segment of a manhole's excavation frustum.
    /// Two segments are stored per manhole: GeoLower (ZBottom→zTouch) and
    /// GeoUpper (zTouch→ZTop).
    /// </summary>
    public class ManholeGeoSegment
    {
        public ManholeGeoLevel Bottom { get; set; }
        public ManholeGeoLevel Mid    { get; set; }
        public ManholeGeoLevel Top    { get; set; }
    }

    public class SystemBoQ
    {
        public string            SystemName { get; set; }
        public List<PipeItem>    Pipes      { get; set; } = new List<PipeItem>();
        public List<ManholeItem> Manholes   { get; set; } = new List<ManholeItem>();
    }

    // =========================================================================
    // Phase 1 – Per-station cross-section data (computed every ~5 m along pipe)
    // =========================================================================

    public class CrossSectionStation
    {
        public double StationDist   { get; set; }  // distance from start node (m)
        public double WorldX        { get; set; }
        public double WorldY        { get; set; }
        public double TerrainZ      { get; set; }  // ZKazi interpolated at this station (excavation top)
        public double InvertZ       { get; set; }
        public double TrueDepth     { get; set; }  // depthToInvert + TrBedHeight
        public double TopWidthExcav { get; set; }  // TrWidth + 2*TrueDepth*SlopeRatio

        /// <summary>ZDolgu interpolated at this station — backfill's own top reference (independent of TerrainZ/ZKazi).</summary>
        public double DolguZ           { get; set; }
        /// <summary>Backfill-basis true depth = max(0, DolguZ − InvertZ) + TrBedHeight — same formula as TrueDepth but from DolguZ.</summary>
        public double TrueDepthDolgu   { get; set; }
        /// <summary>Backfill-basis top width = TrWidth + 2×TrueDepthDolgu×SlopeRatio.</summary>
        public double TopWidthDolgu    { get; set; }
        /// <summary>True when DolguZ is below InvertZ at this station — invalid Dolgu Seviyesi; AreaBackfill stays 0 here.</summary>
        public bool   DolguInvalid     { get; set; }

        // ── Polygon vertices in local (U, Z) frame ─────────────────────────────
        // U = 0 is the pipe centreline axis; U is the signed perpendicular offset.
        // Each element: double[2] { U, Z }.  CCW winding.
        public List<double[]> ExcavPoly    { get; set; }   // full excavation trapezoid
        public List<double[]> BeddingPoly  { get; set; }   // bedding sand layer
        public List<double[]> SurroundPoly { get; set; }   // pipe surround (gross — includes pipe void)
        public List<double[]> BackfillPoly { get; set; }   // backfill zone above surround

        // ── Overlap-adjusted polygons (null = no overlap, or this pipe wins) ────
        // ExcavPolyModified is the REMAINING polygon after the overlap zone is
        // removed — i.e. the actual cross-section shape to loft into a 3-D solid.
        // null means either HasOverlap=false or this pipe is assigned full ownership
        // of the overlap zone (LowerPipe/UpperPipe wins), so ExcavPoly is used as-is.
        public List<double[]> ExcavPolyModified    { get; set; }
        public List<double[]> BackfillPolyModified { get; set; }

        // ── Cross-section areas (m²) computed from polygons ───────────────────
        public double AreaExcav    { get; set; }   // PolygonArea(ExcavPoly)
        public double AreaBedding  { get; set; }   // PolygonArea(BeddingPoly)
        public double AreaSurround { get; set; }   // PolygonArea(SurroundPoly) − PipeArea (net)
        public double AreaBackfill { get; set; }   // PolygonArea(BackfillPoly)

        // ── Net areas after per-station overlap deduction ─────────────────────
        // Equal to the gross areas above when HasOverlap is false.
        public double AreaExcavNet    { get; set; }
        public double AreaBackfillNet { get; set; }

        public bool HasOverlap { get; set; }

        /// <summary>
        /// True when this station was synthetically injected at the exact boundary
        /// (entry or exit) of a crossing zone between two pipes.
        /// Interior stations inside the crossing zone are NOT marked — only the
        /// two boundary stations per crossing pair are marked on each pipe.
        /// Downstream commands can use this to annotate cross-sections, highlight
        /// boundary positions in 3-D, or drive special labelling in reports.
        /// </summary>
        public bool IsCrossingBoundary { get; set; }

        // ── Pseudo-3D engine (Clipper2) — "Pre-calculate and Cache" ──────────────
        // The new Phase 3 orchestrator fills these at URBANO_BOQ time; the legacy
        // *PolyModified fields above are retained only for backward compatibility
        // with old DWGs and are not produced by the new engine.
        //
        // For EVERY station we cache the fully-resolved geometry + net area of all
        // four layers under ALL THREE preference scenarios. Downstream commands
        // (solids, cross-section plotting) and the results-dialog drop-down just
        // read the matching ScenarioProfile — no Clipper booleans are re-run.
        // See TrenchGeometry.cs / ClipperGeo.cs.

        /// <summary>Solid pipe-body cross-section as an N-gon in (U,Z). Deducted from
        /// Bedding/Surround/Backfill — never from Excavation.</summary>
        public List<double[]> PipeBodyPoly { get; set; }

        /// <summary>Cached resolved profile — preference "Keep Upper" (shallower wins ties).</summary>
        public ScenarioProfile ScenarioKeepUpper { get; set; }
        /// <summary>Cached resolved profile — preference "Keep Lower" (deeper wins ties).</summary>
        public ScenarioProfile ScenarioKeepLower { get; set; }
        /// <summary>Cached resolved profile — preference "50/50 vertical split".</summary>
        public ScenarioProfile ScenarioSplit { get; set; }

        /// <summary>Returns the cached profile for the given preference.</summary>
        public ScenarioProfile Scenario(TiePreference p)
        {
            switch (p)
            {
                case TiePreference.KeepUpper: return ScenarioKeepUpper;
                case TiePreference.KeepLower: return ScenarioKeepLower;
                case TiePreference.Ignore:    return null;
                default:                      return ScenarioSplit;
            }
        }
    }

    // =========================================================================
    // Phase 1 – Per-section excavation debug row
    // =========================================================================

    /// <summary>
    /// One PipeTrenchCatalog sub-layer's share of a combined layer-group volume
    /// (Yataklama / Boru Etrafı / Boru Üstü / Geri Dolgu). Ratio is a fixed
    /// geometric constant from the matched tier (area of this layer at its real
    /// cumulative height within the stack, ÷ the group's total area) — computed
    /// once at parse time, independent of overlap/clash scenario. Volume = Ratio
    /// × the group's final resolved total volume (set after RecomputeRow).
    /// See project memory "Trench layer splitting design".
    /// </summary>
    public class TrenchLayerSplit
    {
        public string LayerName    { get; set; } = "";
        public string MaterialType { get; set; } = "";
        public double Ratio        { get; set; }
        public double Volume       { get; set; }
    }

    public class SectionDebugRow
    {
        // ── Identity ─────────────────────────────────────────────────────────
        public string SystemName      { get; set; }
        public string PipeName        { get; set; }   // "1Y → 2Y"
        /// <summary>Phase 2 topology — start node name from the parser.</summary>
        public string StartNodeName   { get; set; }
        /// <summary>Phase 2 topology — end node name from the parser.</summary>
        public string EndNodeName     { get; set; }
        /// <summary>Start/end node (manhole) GUIDs from the Urbano topology. Used by
        /// the overlap engine to skip clashes between pipes sharing a manhole
        /// (connected pipes never count as a real trench clash). Compute-time only —
        /// not serialised, since overlap resolution runs before the data is saved.</summary>
        public string StartNodeGuid   { get; set; }
        public string EndNodeGuid     { get; set; }
        public int    DiameterMm      { get; set; }
        public string Material        { get; set; }   // for re-aggregating PipeItems from cache
        public double PipeOuterDiamM  { get; set; }
        public double Length2D        { get; set; }
        /// <summary>Net length (m) = Length2D minus each connected manhole's outer-shell
        /// radius (inner radius + wall thickness of the precast ring at this pipe's invert
        /// elevation). Runtime-only — computed by PipeNetLengthService.Compute after
        /// ManholeAIService.Process(); never persisted to the DWG.</summary>
        public double NetLength        { get; set; }

        // ── Type Mapping (Phase 5) — from our own PipeCatalog via a linked PipeDefinition ──
        public string PozNo              { get; set; }
        public string Sinif              { get; set; }
        public string Aciklama           { get; set; }
        /// <summary>PipeFamily.Id owning the linked PipeDefinition — used by ManholeAIService's Baca-Boru Bağlantı Kuralları resolution.</summary>
        public Guid   LinkedPipeFamilyId { get; set; }

        // ── Node world coordinates (for 3-D solid generation) ─────────────────
        public double StartX          { get; set; }
        public double StartY          { get; set; }
        public double StartTerrainZ   { get; set; }   // ZKazi at start node (settings-resolved; historically TH1)
        public double EndX            { get; set; }
        public double EndY            { get; set; }
        public double EndTerrainZ     { get; set; }   // ZKazi at end node (settings-resolved; historically TH1)
        /// <summary>ZDolgu (Dolgu Seviyesi-resolved elevation) at the start node — backfill's own top reference, independent of StartTerrainZ/ZKazi.</summary>
        public double StartDolguZ     { get; set; }
        /// <summary>ZDolgu at the end node.</summary>
        public double EndDolguZ       { get; set; }
        /// <summary>True when ZDolgu resolves below the invert at either end of this pipe — an invalid Dolgu Seviyesi configuration. Backfill volume stays 0 for this section and a note names the offending pipe.</summary>
        public bool   DolguInvalid    { get; set; }

        // ── Elevation inputs ─────────────────────────────────────────────────
        public double InvertStart     { get; set; }
        public double InvertEnd       { get; set; }
        public double DepthToInvStart { get; set; }
        public double DepthToInvEnd   { get; set; }

        // ── Trench catalog ────────────────────────────────────────────────────
        public double TrWidth         { get; set; }
        public double TrBedHeight     { get; set; }
        public double TrSandOverPipe  { get; set; }
        public double TrAngleL        { get; set; }
        public double TrAngleR        { get; set; }

        // ── Constant cross-section ────────────────────────────────────────────
        public double SlopeRatio      { get; set; }
        public double TopWidthBed     { get; set; }
        public double ABed            { get; set; }
        public double HSurround       { get; set; }
        public double BaseWidthSurr   { get; set; }
        public double TopWidthSurr    { get; set; }
        public double ASurroundGross  { get; set; }
        public double PipeArea        { get; set; }
        public double ASurroundNet    { get; set; }

        // ── Variable cross-section ────────────────────────────────────────────
        public double TrueDepthStart  { get; set; }
        public double TrueDepthEnd    { get; set; }
        public double TopWidthExcavS  { get; set; }
        public double TopWidthExcavE  { get; set; }
        public double AExcavStart     { get; set; }
        public double AExcavEnd       { get; set; }
        public double ABackfillStart  { get; set; }
        public double ABackfillEnd    { get; set; }

        // ── Volumes ───────────────────────────────────────────────────────────
        public double VBedding              { get; set; }
        public double VSurround             { get; set; }
        public double VExcav                { get; set; }
        public double VBackfill             { get; set; }

        // V2 pre-computed volumes per scenario (stored in DWG under V2_VOLUMES).
        // When HasV2Volumes == true, BoQScenarioAggregator uses these directly
        // instead of re-integrating from per-station ScenarioProfile.NetArea.
        public double VExcavKU     { get; set; }
        public double VExcavKL     { get; set; }
        public double VExcavSP     { get; set; }
        public double VExcavGross  { get; set; }
        public double VBackfillKU  { get; set; }
        public double VBackfillKL  { get; set; }
        public double VBackfillSP  { get; set; }
        public double VBackfillGross { get; set; }
        public bool   HasV2Volumes  { get; set; }

        // ── Clash detection ───────────────────────────────────────────────────
        public double OverlapExcavDeducted    { get; set; }
        public double OverlapBackfillDeducted { get; set; }

        // ── Manhole–pipe overlap (runtime only, not persisted) ─────────────────
        /// <summary>Overlap between this pipe's excavation trench and all connected manhole
        /// excavations. Populated by ManholeExcavOverlapService.Compute.</summary>
        public double ManholeExcavDeducted    { get; set; }
        /// <summary>Overlap between this pipe's bedding layer and all connected manhole
        /// excavations. Populated by ManholeExcavOverlapService.Compute.</summary>
        public double ManholeBeddingDeducted  { get; set; }
        /// <summary>Overlap between this pipe's surround layer and all connected manhole
        /// excavations. Populated by ManholeExcavOverlapService.Compute.</summary>
        public double ManholeSurroundDeducted { get; set; }
        /// <summary>Overlap between this pipe's backfill layer and all connected manhole
        /// excavations. Deducted from VBackfill when BacaKaziHesapla=true.</summary>
        public double ManholeBackfillDeducted { get; set; }
        public List<string> ClashLog        { get; set; } = new List<string>();

        // ── Integration-averaging corrections (set by ApplyExcavationAveraging) ──
        // Volume adjustment (m³) added to the raw trapezoidal integral under each
        // excavation scenario. Eliminates the discretization asymmetry that arises
        // because integrating the same 3-D intersection along two different pipe
        // axes gives slightly different floating-point sums. Zero until that pass
        // runs; preserved across RecomputeRow calls so dropdown changes stay exact.
        public double ExcavAvgAdjKU { get; set; }
        public double ExcavAvgAdjKL { get; set; }
        public double ExcavAvgAdjSP { get; set; }

        // ── Per-station cross-sections (populated by BoQParserService) ────────
        public List<CrossSectionStation> Stations { get; set; } = new List<CrossSectionStation>();

        // ── Trench layer split (Phase 2b — PipeTrenchCatalog per-layer breakdown) ─
        // Ratios computed once in ParseSections from the matched tier's geometry;
        // Volume filled in by BoQScenarioAggregator.ApplyLayerSplits after
        // RecomputeRow resolves the group's final (post-clash) total. Empty list
        // means "no breakdown for this group" (old DWG, no matching rule, or a
        // fill-to-surface layer present — falls back to the single combined total).
        public List<TrenchLayerSplit> BeddingLayerSplits    { get; set; } = new List<TrenchLayerSplit>();
        public List<TrenchLayerSplit> BoruEtrafiLayerSplits { get; set; } = new List<TrenchLayerSplit>();
        public List<TrenchLayerSplit> BoruUstuLayerSplits   { get; set; } = new List<TrenchLayerSplit>();
        public List<TrenchLayerSplit> BackfillLayerSplits   { get; set; } = new List<TrenchLayerSplit>();
    }

    // =========================================================================
    // Phase 1 – Aggregate report
    // =========================================================================

    public class BoQReport
    {
        public DateTime              GeneratedAt    { get; set; } = DateTime.Now;
        public List<SystemBoQ>       Systems        { get; set; } = new List<SystemBoQ>();
        public List<string>          DiscoveryNotes { get; set; } = new List<string>();
        public List<SectionDebugRow> SectionDebug   { get; set; } = new List<SectionDebugRow>();

        /// <summary>
        /// Every non-group PIPE/MANHOLE item found in this export's own embedded
        /// &lt;catalogs&gt; block — persisted into the DWG (TYPE_MAPPING/DISCOVERED_ITEMS)
        /// on every save, so the Type Mapping UI never depends on the ephemeral
        /// %TEMP% export file (which doesn't travel with the DWG to another machine).
        /// </summary>
        public List<CatalogItemInfo> CatalogItems   { get; set; } = new List<CatalogItemInfo>();

        public double TotalPipeLength =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.TotalLength);

        public int TotalManholeCount =>
            Systems.SelectMany(s => s.Manholes).Sum(m => m.Count);

        public double TotalExcavationVolume =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.TotalExcavationVolume);

        public double TotalBeddingVolume =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.TotalBeddingVolume);

        public double TotalSurroundVolume =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.TotalSurroundVolume);

        public double TotalBackfillVolume =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.TotalBackfillVolume);

        public double TotalOverlapExcavDeducted =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.OverlapExcavDeducted);

        public double TotalOverlapBackfillDeducted =>
            Systems.SelectMany(s => s.Pipes).Sum(p => p.OverlapBackfillDeducted);

        public double TotalManholeExcavationVolume =>
            Systems.SelectMany(s => s.Manholes).Sum(m => m.ExcavationVolume);

        public double TotalManholeSubBaseVolume =>
            Systems.SelectMany(s => s.Manholes).Sum(m => m.SubBaseVolume);

        public double TotalManholeBackfillVolume =>
            Systems.SelectMany(s => s.Manholes).Sum(m => m.BackfillVolume);

        public double TotalManholeVsManholeExcavDeducted =>
            Systems.SelectMany(s => s.Manholes).Sum(m => m.ManholeVsManholeExcavDeducted);

        public double TotalManholeVsManholeBackfillDeducted =>
            Systems.SelectMany(s => s.Manholes).Sum(m => m.ManholeVsManholeBackfillDeducted);
    }

    // =========================================================================
    // Phase 2 – Manhole AI catalog models
    // =========================================================================

    /// <summary>
    /// One row from the Manhole_Catalog.xlsx template (new row-per-part schema).
    ///
    /// Catalog columns: Nominal_Diameter | Part_Name | Height_m | Is_Mandatory | Is_Variable_Ring
    /// </summary>
    public class CatalogPart
    {
        /// <summary>Part label as written in the catalog, e.g. "Taban", "Konik", "Govde".</summary>
        public string PartName       { get; set; }
        /// <summary>Height of this component in metres.</summary>
        public double HeightM        { get; set; }
        /// <summary>True → always add exactly 1 unit per manhole regardless of depth.</summary>
        public bool   IsMandatory    { get; set; }
        /// <summary>True → eligible for the greedy height-filling algorithm.</summary>
        public bool   IsVariableRing { get; set; }
    }

    /// <summary>
    /// All catalog parts for one nominal manhole diameter.
    /// Populated by ManholeAIService.ReadCatalog().
    /// </summary>
    public class CatalogEntry
    {
        /// <summary>Manhole shaft internal diameter (mm).</summary>
        public int NominalDiameter { get; set; }

        /// <summary>All parts defined for this diameter in the catalog.</summary>
        public List<CatalogPart> Parts { get; set; } = new List<CatalogPart>();

        /// <summary>Parts where IsMandatory == true (always 1 unit per manhole).</summary>
        public List<CatalogPart> MandatoryParts =>
            Parts.Where(p => p.IsMandatory).ToList();

        /// <summary>
        /// Parts where IsVariableRing == true, sorted largest → smallest.
        /// Used by the greedy stacking algorithm.
        /// </summary>
        public List<CatalogPart> VariableRings =>
            Parts.Where(p => p.IsVariableRing)
                 .OrderByDescending(p => p.HeightM)
                 .ToList();

        /// <summary>Sum of heights of all mandatory parts (fixed stack base height).</summary>
        public double TotalMandatoryHeight =>
            Parts.Where(p => p.IsMandatory).Sum(p => p.HeightM);
    }

    // =========================================================================
    // Phase 2 – Stacking result models
    // =========================================================================

    /// <summary>
    /// One concrete part entry in a manhole's stack.
    /// Mandatory parts always have Count == 1.
    /// Variable rings may have Count > 1.
    /// </summary>
    public class StackedPart
    {
        /// <summary>Part name from the catalog (e.g. "Taban", "Govde").</summary>
        public string PartName       { get; set; }
        /// <summary>Height of this part (m).</summary>
        public double HeightM        { get; set; }
        /// <summary>Number of identical units stacked.</summary>
        public int    Count          { get; set; }
        /// <summary>True if this part was selected by the greedy ring algorithm.</summary>
        public bool   IsVariableRing { get; set; }

        // ── From our ComponentFamily catalog (proves the stack is catalog-driven) ──
        public string PozNo               { get; set; }
        public string Aciklama            { get; set; }
        /// <summary>Net concrete/material volume of ONE unit (m³), from ManholeComponent.MaterialVolume.</summary>
        public double UnitMaterialVolume  { get; set; }
        /// <summary>Total spatial displacement of ONE unit (m³), from ManholeComponent.ExternalVolume.</summary>
        public double UnitExternalVolume  { get; set; }
        /// <summary>Wall thickness (mm) of the underlying component, when that component type
        /// tracks one (BottomElement/MiddleElement/Adjuster/Reducer). Null for types that don't
        /// (e.g. Cover) — used by PipeNetLengthService to resolve the ring at a pipe's invert.</summary>
        public double? WallThicknessMm    { get; set; }
        /// <summary>Underlying component's role — used to sort Parts into true bottom-to-top
        /// physical order (BottomElement, MiddleElement, Reducer, Adjuster, Cover). Insertion
        /// order alone is NOT reliable: "değişken" (variable-height) pieces are appended after
        /// all fixed pieces regardless of their real physical position in the stack.</summary>
        public ComponentRole Role         { get; set; }
    }

    /// <summary>
    /// Complete stacking solution for one manhole.
    /// </summary>
    public class ManholeStackResult
    {
        public int  NominalDiameter { get; set; }
        /// <summary>True = pre-cast discrete parts; false = cast-in-place concrete pour.</summary>
        public bool IsPreCast       { get; set; }

        /// <summary>
        /// Ordered list of all stacked parts:
        ///   mandatory parts first (in catalog order), then variable rings (largest first).
        /// </summary>
        public List<StackedPart> Parts { get; set; } = new List<StackedPart>();

        /// <summary>
        /// Height gap remaining after all rings have been stacked (metres).
        /// Should be ≤ LeftoverTolerance (0.05 m) after the algorithm runs.
        /// </summary>
        public double ResidualM { get; set; }

        /// <summary>
        /// True when the tier's Parça Kısıtları (Min/Maks per-role component
        /// counts) could not all be satisfied — either a MinCount wasn't met
        /// (a required piece type has no diameter-matched catalog component),
        /// or the greedy fill still left a gap &gt; LeftoverTolerance after
        /// respecting every role's MaxCount. Aggregated into a single
        /// DiscoveryNotes warning by ManholeAIService.Process — never silently
        /// swallowed.
        /// </summary>
        public bool ConstraintViolated { get; set; }
        /// <summary>Short human-readable reason for ConstraintViolated (e.g. "Konik (Min≥1) bulunamadı", "hedef derinliğe 0.08m eksik kaldı") — surfaced per-manhole in the aggregate warning so a real cause is visible instead of just a count.</summary>
        public string ConstraintViolationReason { get; set; }

        /// <summary>Diagnostic: smallest available shaft piece (Gövde/Boyun) height in mm at fill time.
        /// When a residual gap is flagged and this is &gt; the residual, the family simply has no piece
        /// fine enough to close it (inherent catalog tiling), not an engine/mode issue.</summary>
        public double SmallestAvailPieceMm { get; set; }

        // ── Cast-in-place only ────────────────────────────────────────────────
        /// <summary>Total concrete shaft depth in metres (IsPreCast == false only).</summary>
        public double ConcreteDepth { get; set; }
    }
}
