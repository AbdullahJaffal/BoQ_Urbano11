using System.Collections.Generic;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Sections
{
    // =========================================================================
    // One drawable cross-section (En Kesit) of one pipe at one chainage, resolved
    // under the chosen Kazı / Dolgu scenarios. Built by the V2 engine itself
    // (BoQSimplifiedTestV2Engine.Sections.cs) — AutoCAD-free, tested in GeoHarness.
    //
    // Frame: U = signed offset from the pipe axis, positive to the LEFT of the
    // pipe direction (start → end node); Z = elevation (m). Inside a crossing
    // (bisector) zone the cut is the engine's oblique bisector cut (widths / cos θ/2).
    // =========================================================================

    public enum SectionLayer { Kazi = 0, Yataklama = 1, Gomlekleme = 2, GeriDolgu = 3, BoruGovdesi = 4 }

    public enum SectionRegionRole
    {
        /// <summary>This pipe's share under the chosen scenario.</summary>
        Owned = 0,
        /// <summary>Part of this pipe's gross section given to a clashing pipe.</summary>
        LostToOther = 1,
        /// <summary>A clashing pipe's own section in this cut (outside this pipe's share).</summary>
        OtherTrench = 2,
        /// <summary>This pipe's gross section before any deduction (reference outline).</summary>
        GrossOutline = 3
    }

    /// <summary>Where the drawn geometry came from (decision 1: always a computed section).</summary>
    public enum SectionStationKind
    {
        /// <summary>A station the engine itself computed at this chainage.</summary>
        EngineStation = 0,
        /// <summary>Clash-free chainage off the engine grid — the gross section from the
        /// engine's own BuildGrossProfiles at exactly this chainage.</summary>
        GrossAtChainage = 1,
        /// <summary>Inside a crossing zone off the engine grid — the nearest engine station.</summary>
        SnappedInClash = 2
    }

    public sealed class SectionRegion
    {
        public SectionLayer      Layer           { get; set; }
        public SectionRegionRole Role            { get; set; }
        /// <summary>Clashing pipe (index into the engine rows), -1 for this pipe's own regions.</summary>
        public int               OtherPipeIndex  { get; set; } = -1;
        public string            OtherPipeName   { get; set; }
        public string            OtherSystemName { get; set; }
        /// <summary>Clashing pipe's nominal diameter (fills the {CAP} layer token).</summary>
        public int               OtherDiameterMm { get; set; }
        /// <summary>Rings in (U,Z); Clipper orientation (holes opposite to outers).</summary>
        public List<List<double[]>> Rings        { get; set; } = new List<List<double[]>>();
    }

    /// <summary>One catalog sub-layer of a fill group, clipped to the group's owned region.</summary>
    public sealed class SectionSubBand
    {
        public TrenchStackGroup Group        { get; set; }
        public SectionLayer     Layer        { get; set; }
        public string           LayerName    { get; set; }
        public string           MaterialType { get; set; }
        public double           ZLow         { get; set; }
        public double           ZHigh        { get; set; }
        /// <summary>Area of <see cref="Rings"/> (m²) — this pipe's owned share of the sub-layer.</summary>
        public double           Area         { get; set; }
        public List<List<double[]>> Rings    { get; set; } = new List<List<double[]>>();
    }

    /// <summary>A horizontal sub-layer division line inside a fill group, as (U0, U1) segments at Z.</summary>
    public sealed class SectionDivisionLine
    {
        public SectionLayer        Layer    { get; set; }
        public double              Z        { get; set; }
        public List<double[]>      Segments { get; set; } = new List<double[]>();   // {u0, u1}
    }

    public sealed class SectionClashInfo
    {
        public int    OtherPipeIndex  { get; set; }
        public string OtherPipeName   { get; set; }
        public string OtherSystemName { get; set; }
        public bool   ThisIsUpper     { get; set; }
        /// <summary>The other pipe's axis offset in this cut (m).</summary>
        public double OtherOffsetU    { get; set; }
        /// <summary>SP split line position (NaN when the trenches do not overlap).</summary>
        public double SplitU          { get; set; } = double.NaN;
    }

    public sealed class SectionCut
    {
        // ── Identity ─────────────────────────────────────────────────────────
        public int    PipeIndex       { get; set; }
        public string PipeName        { get; set; }
        public string SystemName      { get; set; }
        public string StartNodeName   { get; set; }
        public string EndNodeName     { get; set; }
        public int    DiameterMm      { get; set; }
        public string Material        { get; set; }
        public double OuterDiamM      { get; set; }

        // ── Position ─────────────────────────────────────────────────────────
        public double             RequestedDist  { get; set; }
        /// <summary>Local chainage actually drawn (differs from RequestedDist only when snapped).</summary>
        public double             LocalChainage  { get; set; }
        public SectionStationKind Kind           { get; set; }
        /// <summary>Kol (branch line) chainage — NaN when not assigned (e.g. BASINCLI).</summary>
        public double             GlobalChainage { get; set; } = double.NaN;
        public string             KolName        { get; set; }
        public double             WorldX         { get; set; }
        public double             WorldY         { get; set; }
        public bool               IsPipeStart    { get; set; }
        public bool               IsPipeEnd      { get; set; }
        public bool               IsZoneBoundary { get; set; }

        // ── Levels / dimensions ──────────────────────────────────────────────
        public double KaziGroundZ    { get; set; }
        public double DolguGroundZ   { get; set; }
        public bool   DolguInvalid   { get; set; }
        public double InvertZ        { get; set; }
        public double TrenchBottomZ  { get; set; }
        public double TrueDepth      { get; set; }
        public double BottomWidth    { get; set; }
        public double TopWidth       { get; set; }
        /// <summary>Şev as x of 1:x (horizontal per vertical) — the UI convention.</summary>
        public double SlopeX         { get; set; }
        public bool   IsBisectorFrame { get; set; }
        /// <summary>Crossing angle in plan (bisector cuts only).</summary>
        public double CrossingAngleDeg { get; set; }

        // ── Scenario ─────────────────────────────────────────────────────────
        public TiePreference ExcavScenario    { get; set; }
        public TiePreference BackfillScenario { get; set; }
        /// <summary>Scenario-comparison mode: cuts of one station share a group (one sheet row); -1 = normal.</summary>
        public int           CompareGroup     { get; set; } = -1;

        // ── Geometry ─────────────────────────────────────────────────────────
        public List<SectionRegion>       Regions       { get; set; } = new List<SectionRegion>();
        public List<SectionSubBand>      SubBands      { get; set; } = new List<SectionSubBand>();
        public List<SectionDivisionLine> DivisionLines { get; set; } = new List<SectionDivisionLine>();
        public List<SectionClashInfo>    Clashes       { get; set; } = new List<SectionClashInfo>();

        // ── Areas (m²) — measured on the regions above (decision 3) ──────────
        public double AreaKaziNet        { get; set; }
        public double AreaKaziGross      { get; set; }
        public double AreaYataklamaNet   { get; set; }
        public double AreaYataklamaGross { get; set; }
        public double AreaGomleklemeNet  { get; set; }
        public double AreaGomleklemeGross { get; set; }
        public double AreaGeriDolguNet   { get; set; }
        public double AreaGeriDolguGross { get; set; }

        // ── Extent of everything in the cut (incl. clashing pipes) ───────────
        public double MinU { get; set; }
        public double MaxU { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
    }
}
