# URBANO_ARCHITECTURE_RULES.md

> **Status:** Authoritative, living rulebook for all Urbano 11 plugin development.
> **Source:** Reverse-engineered from live drawings and verified against the running
> `UrbanoMetraj` plugin codebase.  Every rule here was derived from actual binary
> inspection, regex analysis, and confirmed field output.
>
> **Rule 0 — Golden Law:** Urbano exposes NO public API.  Every data point must be
> obtained through one of the documented extraction paths below.  Do NOT trust label
> text as a primary source; it may be stale or hidden.

---

## Table of Contents

1. [Extraction Pipeline — ARS_EXPORT_XML Automation](#1-extraction-pipeline)
2. [XML Graph Structure — Exact Navigation Path](#2-xml-graph-structure)
3. [The HexFloat Decoder — Why Regex Is Mandatory](#3-the-hexfloat-decoder)
4. [Node (Baca) Data Dictionary](#4-node-baca-data-dictionary)
5. [Section (Boru) Data Dictionary](#5-section-boru-data-dictionary)
6. [Catalog Dictionary — Pipe, Trench, Manhole](#6-catalog-dictionary)
7. [Master Entity Identification via XData](#7-master-entity-identification)
8. [Spatial Matching Principle](#8-spatial-matching-principle)
9. [NOD (Named Objects Dictionary) Layout](#9-nod-layout)
10. [Calculation Formulas](#10-calculation-formulas)
11. [Clash Detection Algorithm](#11-clash-detection-algorithm)
12. [Known Encoding Pitfalls](#12-known-encoding-pitfalls)
13. [Build & Deployment Rules](#13-build--deployment-rules)

---

## 1. Extraction Pipeline

### Overview

Urbano 11 has no programmatic data-export API.  The only reliable way to obtain
committed network data is to trigger Urbano's own built-in export command
(`ARS_EXPORT_XML`) and silently automate its modal dialog using Windows UI
Automation + Win32.

### Threading Contract — CRITICAL

The dialog automation **must** run on a dedicated **STA thread**, not `Task.Run`.

- UI Automation uses COM internally.
- AutoCAD's main thread is **blocked** while the modal dialog is alive.
- Calling `editor.WriteMessage()` while the dialog is alive **deadlocks**.  All
  log messages must be buffered and flushed only **after** `WM_CLOSE` is sent.

```csharp
// Correct pattern (from BoQCommand.cs)
var cts    = new CancellationTokenSource();
var svc    = new UrbanoExportService(ed);
var thread = new Thread(() => { success = svc.WaitAndAutomate(exportPath, cts.Token); });
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
// Then send ARS_EXPORT_XML on the main thread (which blocks):
doc.SendStringToExecute("ARS_EXPORT_XML\n", true, false, true);
thread.Join();
```

### Five-Phase Automation Sequence

| Phase | Action | Implementation |
|-------|--------|----------------|
| 1 | Poll for HWND with `FindWindow(null, "Urbano XML'e topoloji ihraç")` | 30 × 500 ms = 15 s timeout |
| 2a | Set export file path via `ValuePattern.SetValue(exportPath)` on first `ControlType.Edit` | Required before clicking export |
| 2b | Toggle all `ControlType.CheckBox` to `ToggleState.On` | Selects all network systems |
| 2c | Invoke `ControlType.Button` named `"Dışa aktar"` via `InvokePattern.Invoke()` | Triggers the actual export |
| 3 | Poll for `"Tamam"` button (success popup) and invoke it | 20 × 500 ms = 10 s timeout |
| 4 | Poll until XML file exists, is non-empty, and is not locked | 20 × 500 ms = 10 s timeout |
| 5 | Send `WM_CLOSE` (0x0010) to main HWND to release AutoCAD main thread | **Always runs in `finally` block** |

### Dialog Window Details

```
Window title  : "Urbano XML'e topoloji ihraç"
Export button : "Dışa aktar"
OK popup btn  : "Tamam"
```

### Export Path Convention

```csharp
string exportPath = Path.Combine(
    Path.GetTempPath(),
    $"urbano_boq_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
```

Use a unique timestamped temp file to avoid stale-file false positives.

---

## 2. XML Graph Structure

### Root Structure

The exported file is a single XML document.  Navigate it with LINQ to XML
(`XDocument` / `XElement`).  The document has **no XML namespace** — all element
and attribute names are plain local names.

```
drawing
└── topology
    └── networkTopology
        ├── main
        │   └── tpl                    ← PRIMARY DATA ROOT
        │       ├── ns                 ← Node collection
        │       │   └── n [...]        ← One element per manhole
        │       └── ss                 ← Section collection
        │           └── s [...]        ← One element per pipe section
        └── (auxiliary topology — ignored)

catalogs
└── catalog [...]                      ← Named catalogs (pipe, trench, manhole)
    └── catalogItem [...]              ← One item per catalog entry
        └── ppsEx
            └── ct [...]
                └── pEx [...]          ← Property key-value pairs

gisSystem [...]                        ← Network system name registry
```

### Finding the Data Root in Code

```csharp
private static XElement FindMainTpl(XDocument doc)
    => doc.Descendants("topology")
          .Descendants("networkTopology")
          .Descendants("main")
          .Descendants("tpl")
          .FirstOrDefault();
```

This is tolerant of intermediate elements and works regardless of nesting depth.

### System Names

```csharp
foreach (var gs in doc.Descendants("gisSystem"))
{
    string id   = (string)gs.Attribute("id");    // integer key
    string name = (string)gs.Attribute("name");  // e.g. "ET1_YSU"
}
```

### Property Bag Pattern

Every `<n>` (node) and `<s>` (section) element stores its properties as a
child `<ps>` element containing `<p>` elements:

```xml
<n g="{GUID}" pos="...">
  <ps>
    <p t="TH1"         v="80010x1.8p+0"/>
    <p t="MHB"         v="80010x1.43d70a3d70a4p+3"/>
    <p t="AG_NAME"     v="50034Y"/>
    <p t="AG_ID_SYSTEM" v="50031"/>
    <p t="MH"          v="5005{GUID}"/>
  </ps>
</n>
```

Reading the property bag:

```csharp
private static Dictionary<string, string> ReadTopoProps(XElement el)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in el.Elements("ps").Elements("p"))
    {
        string t = (string)p.Attribute("t") ?? "";
        string v = (string)p.Attribute("v") ?? "";
        if (!string.IsNullOrEmpty(t) && !d.ContainsKey(t)) d[t] = v;
    }
    return d;
}
```

---

## 3. The HexFloat Decoder

### Why Standard Parsing Fails

Every numeric value in Urbano's XML is encoded as a **C99 hex-float** with a
type-tag prefix attached directly to it:

```
Raw value of TH1  : "80010x1.8p+0"
              ^^^^  ^^^^^^^^^^^^
              prefix  hex-float token

Raw value of @pos : "8005-0x1.4d13d6a9e1ad0p+15=EF=BE=890x1.2c1a89f4926c0p+12=EF=BE=89..."
```

The separator between coordinates inside `@pos` is the **9-character ASCII string
`"=EF=BE=89"`** (quoted-printable encoding of UTF-8 bytes `0xEF 0xBE 0x89`).

It is **NOT** the Unicode character U+FE09.  Splitting on Unicode characters
or stripping fixed-length prefixes always produces wrong results.

### The Mandatory Regex

```csharp
private static readonly Regex HexFloatRx = new Regex(
    @"[-+]?0x[0-9a-fA-F]+\.[0-9a-fA-F]+p[-+]?[0-9]+",
    RegexOptions.Compiled);
```

Apply this to the raw string.  Each `Match.Value` is a clean, parseable C99
hex-float token, with no prefix or suffix contamination.

### Coordinate Extraction from `@pos`

```csharp
private static void ParsePos(string raw, out double x, out double y)
{
    x = 0; y = 0;
    if (string.IsNullOrEmpty(raw)) return;
    MatchCollection matches = HexFloatRx.Matches(raw);
    if (matches.Count >= 2)
    {
        x = DecodeHexDouble(matches[0].Value);   // Easting
        y = DecodeHexDouble(matches[1].Value);   // Northing
        return;
    }
    // ... plain-decimal fallback for future schema variants
}
// matches[2] = Z elevation (available but not used for 2-D BoQ)
```

### Scalar Property Extraction

```csharp
private static double DecodeFloatProp(string raw)
{
    if (string.IsNullOrEmpty(raw)) return 0;

    // Strategy 1: Regex on the whole raw string
    Match m = HexFloatRx.Match(raw);
    if (m.Success) return DecodeHexDouble(m.Value);

    // Strategy 2: strip known prefix, parse as plain decimal
    string s = raw.StartsWith("8001") ? raw.Substring(4)
             : raw.StartsWith("8005") ? raw.Substring(4)
             : raw.StartsWith("5003") ? raw.Substring(4)
             : raw.StartsWith("5005") ? raw.Substring(4)
             : raw;
    return double.TryParse(s.Trim(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double v) ? v : 0;
}
```

### C99 Hex-Float Decoder (Full Implementation)

```csharp
private static double DecodeHexDouble(string hex)
{
    try
    {
        bool   neg = hex.StartsWith("-");
        string s   = neg ? hex.Substring(1) : hex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

        int pIdx = s.IndexOfAny(new[] { 'p', 'P' });
        if (pIdx < 0) return 0;

        int exp = int.Parse(s.Substring(pIdx + 1),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        string mantPart = s.Substring(0, pIdx);
        string[] mParts = mantPart.Split('.');
        long intBits    = long.Parse(mParts[0], NumberStyles.HexNumber);
        long fracBits   = 0;
        int  fracLen    = 0;
        if (mParts.Length > 1 && mParts[1].Length > 0)
        {
            fracBits = long.Parse(mParts[1], NumberStyles.HexNumber);
            fracLen  = mParts[1].Length * 4;
        }

        // Zero check — "0x0.0000000000000p+0" must return 0.0, not 1.0
        if (intBits == 0 && fracBits == 0) return 0.0;

        long biasedExp = exp + 1023;
        long frac52    = fracLen <= 52 ? (fracBits << (52 - fracLen))
                                       : (fracBits >> (fracLen - 52));
        long bits = (biasedExp << 52) | (frac52 & 0x000FFFFFFFFFFFFFL);
        double val = BitConverter.Int64BitsToDouble(bits);
        return neg ? -val : val;
    }
    catch { return 0; }
}
```

### Known Type-Tag Prefixes

| Prefix | Meaning |
|--------|---------|
| `8001` | Float / double value |
| `8005` | Coordinate / position value |
| `5003` | Integer value |
| `5005` | String / GUID value |

Strip exactly 4 characters when no hex-float is found and the strategy-2
decimal fallback is needed.

---

## 4. Node (Baca) Data Dictionary

### XML Location

```
topology/networkTopology/main/tpl/ns/n
```

Each `<n>` element is one manhole node.

### Element Attributes

| Attribute | Type | Description |
|-----------|------|-------------|
| `g`       | GUID | Globally unique node identifier (always uppercase after parsing) |
| `pos`     | encoded | X, Y, Z coordinates as concatenated hex-floats (see Section 3) |

### Property Bag Keys (`<ps><p t="KEY" v="RAW"/>`)

| Key | Decoded Type | Turkish Name | Description |
|-----|-------------|--------------|-------------|
| `AG_NAME` | string (prefix `5003` or `5005`) | Baca Adı | Node label shown in drawing, e.g. `"4Y"`, `"1Y"` |
| `AG_ID_SYSTEM` | int (prefix `5003`) | Sistem ID | Integer key into `gisSystem` name registry |
| `TH1` | float (hex-float, prefix `8001`) | Arazi Kotu | **Absolute terrain/ground elevation in metres a.s.l.** |
| `MHB` | float (hex-float, prefix `8001`) | Taban Boşluğu | Gap between the lowest pipe invert and manhole floor, metres.  Added to depth after pipe invert depth calculation. |
| `MH`  | GUID (prefix `5005`) | Baca Katalog | GUID of the manhole catalog entry (used to resolve nominal shaft diameter) |

### `AG_NAME` Decoding

```csharp
private static string DecodeStrProp(string raw)
{
    if (string.IsNullOrEmpty(raw)) return "";
    if (raw.StartsWith("5005")) return raw.Substring(4);
    if (raw.StartsWith("5003")) return raw.Substring(4);
    return raw;
}
```

### `MH` GUID Decoding

```csharp
private static string DecodeGuidStr(string raw)
{
    string s = DecodeStrProp(raw);
    var m = GuidRx.Match(s);   // GuidRx = standard 8-4-4-4-12 hex pattern
    return m.Success ? m.Value.ToUpperInvariant() : s.ToUpperInvariant().Trim();
}
```

---

## 5. Section (Boru) Data Dictionary

### XML Location

```
topology/networkTopology/main/tpl/ss/s
```

Each `<s>` element is one pipe section between two nodes.

### Element Attributes

| Attribute | Type | Description |
|-----------|------|-------------|
| `g`  | GUID | Globally unique section identifier |
| `sn` | GUID | **Start Node** GUID (links to a `<n g="...">`) |
| `en` | GUID | **End Node** GUID (links to a `<n g="...">`) |

**Critical:** `sn` and `en` are the ONLY structural links between sections and
nodes.  There is no direct positional data on sections; coordinates are derived
by looking up the node's `@pos`.

### Property Bag Keys

| Key | Decoded Type | Turkish Name | Description |
|-----|-------------|--------------|-------------|
| `AG_ID_SYSTEM` | int | Sistem ID | Integer key into system name registry |
| `LL10` | float (hex-float) | Ölçüm Başlangıç | Elevation at the **start** node; which cross-section point it measures is defined by `LLPOS` (see below). |
| `LL11` | float (hex-float) | Ölçüm Bitiş | Elevation at the **end** node; same `LLPOS` applies. |
| `LLPOS` | int (prefix `5003`) | Seviye Konumu | Cross-section point measured by LL10/LL11. **Encoded values:** `1`=Üst dış (outer top), `2`=Üst iç (inner top), `4`=Aks (centreline), `8`=Alt iç (inner bottom = AkarKot, **most common**), `16`=Alt dış (outer bottom). Default when absent: `8`. |
| `PPR`  | GUID (prefix `5005`) | Boru Kataloğu | GUID of the pipe catalog entry |
| `TRNC` | GUID (prefix `5005`) | Hendek Kataloğu | GUID of the trench catalog entry |

### Invert Elevations (AkarKot)

LL10 and LL11 measure the elevation of a specific cross-section point on the pipe, identified by `LLPOS`. Converting to AkarKot (pipe invert = inner-bottom flow level):

```
LLPOS  Cross-section point    AkarKot formula
─────  ────────────────────   ───────────────────────────────
  1    Üst dış  (outer top)   AkarKot = LL − OD
  2    Üst iç   (inner top)   AkarKot = LL − ID
  4    Aks      (centreline)  AkarKot = LL − ID/2
  8    Alt iç   (inner btm)   AkarKot = LL              ← most common
 16    Alt dış  (outer btm)   AkarKot = LL + (OD−ID)/2
```

Where OD = PIPE_DV/1000 (outer diameter, m), ID = PIPE_NO/1000 (nominal = inner diameter, m).

In code: `LlToInvert(ll, llpos, odM, idM)` in `BoQParserService.cs`.

> **NOTE (2026-06)**: Earlier documentation incorrectly stated LL10/LL11 always = AkarKot.
> XML analysis of test4.2.xml confirms LLPOS varies per pipe: 4 of 5 pipes use `8` (Alt iç = AkarKot
> direct) and 1 uses `1` (Üst dış = outer top → AkarKot = LL − OD).

### Deriving 2-D Length

```csharp
double dx = endNode.X - startNode.X;
double dy = endNode.Y - startNode.Y;
double length2D = Math.Sqrt(dx * dx + dy * dy);
```

Coordinates come from hex-float decoding of the nodes' `@pos` attributes.

---

## 6. Catalog Dictionary

### XML Location

```
drawing/catalogs/catalog/catalogItem
```

(Also accessible from any point in the document via `doc.Descendants("catalogItem")`.)

### Catalog Item Structure

```xml
<catalogItem guid="{GUID}" name="SD_Tip1 ...">
  <ppsEx>
    <ct>
      <pEx t="PIPE_DV"       v="80011.0p+9"/>     <!-- outer diameter mm -->
      <pEx t="PIPE_NO"       v="80011.0p+9"/>     <!-- nominal diameter mm -->
      <pEx t="PIPE_MATERIAL" v="5005BETON"/>
      <pEx t="TR_WIDTH"      v="80011.0p+0"/>
      <pEx t="TR_ANGLE-L"    v="80011.dp+6"/>
    </ct>
  </ppsEx>
</catalogItem>
```

All attributes on `<catalogItem>` itself are also stored as `"@attributeName"` keys
in the parsed dictionary for completeness.

### Building the Catalog Index

```csharp
var dict = new Dictionary<string, Dictionary<string, string>>(
    StringComparer.OrdinalIgnoreCase);

foreach (var item in doc.Descendants("catalogItem"))
{
    string guid = (string)item.Attribute("guid") ?? "";
    if (string.IsNullOrEmpty(guid) || guid == "SYS") continue;

    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var ct in item.Elements("ppsEx").Elements("ct"))
        foreach (var pEx in ct.Elements("pEx"))
        {
            string t = (string)pEx.Attribute("t") ?? "";
            string v = (string)pEx.Attribute("v") ?? "";
            if (!string.IsNullOrEmpty(t) && !props.ContainsKey(t))
                props[t] = v;
        }
    foreach (var attr in item.Attributes())
        props["@" + attr.Name.LocalName] = attr.Value;

    dict[guid.ToUpperInvariant()] = props;
}
```

### Pipe Catalog Keys

| Key | Description | Unit |
|-----|-------------|------|
| `PIPE_DV` | Outer diameter **(primary)** | mm (decode hex-float, value is in mm) |
| `PIPE_DU` | Outer diameter **(fallback)** | mm |
| `PIPE_NO` | Nominal diameter | mm |
| `PIPE_MATERIAL` | Material string | — |
| `CATALOGITEM_NAME` | Full catalog name string | — |

Outer diameter in metres: `odM = PIPE_DV / 1000.0`

### Trench Catalog Keys

| Key | Description | Unit |
|-----|-------------|------|
| `TR_WIDTH`       | Trench bottom width | m |
| `TR_BEDHEIGHT`   | Sand bed layer height below pipe | m |
| `TR_SANDOVERPIPE`| Sand cover above pipe outer top | m |
| `TR_ANGLE-L`     | Left wall angle from horizontal | degrees |
| `TR_ANGLE-R`     | Right wall angle from horizontal | degrees |

Default fallback values if catalog entry not found: `TR_WIDTH=1.0`, `TR_ANGLE-L=90.0`,
`TR_ANGLE-R=90.0` (vertical walls), `TR_BEDHEIGHT=0`, `TR_SANDOVERPIPE=0`.

### Manhole Catalog Keys

| Key | Description |
|-----|-------------|
| `MANHOLE_DN` | Nominal internal diameter mm (integer, preferred) |
| `MANHOLE_D2` | Internal diameter in m (secondary) |
| `CATALOGITEM_NAME` | Full name string — use as fallback for diameter extraction |

### Manhole Diameter from `CATALOGITEM_NAME`

Urbano encodes the `Φ` separator as the quoted-printable sequence `"=EF=BF=98"`.
The nominal shaft diameter is the **first** 3–4 digit run immediately after this
encoded character:

```
"SD_Tip1 =EF=BF=981000_=EF=BF=98400-=EF=BF=98500 1300 mm"
                  ^^^^
                  1000 = nominal shaft diameter (mm)
```

```csharp
Match m = Regex.Match(catName, @"(?:=[0-9A-Fa-f]{2})+(\d{3,4})");
if (m.Success && int.TryParse(m.Groups[1].Value, out int v))
    nominalDiam = v;
```

---

## 7. Master Entity Identification

### Problem

Urbano places multiple AutoCAD entities on the drawing for each logical object:
a **master entity** (the AcDbLine for a pipe, or AcDbCircle for a manhole center)
plus graphical sub-entities (contour lines, label text, fill areas, etc.).
Only master entities carry the full GUID chain.

### Detecting a Master Entity

Master entities have an XData group whose registered-app name starts with
`"DRAWER_ID."`:

```csharp
public static string GetDrawerContent(Dictionary<string, List<TypedValue>> xd)
{
    const string pfx = "DRAWER_ID.";
    foreach (string k in xd.Keys)
    {
        if (!k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) continue;
        if (xd.TryGetValue(k, out var v) && v.Count > 0)
            return v[0].Value?.ToString() ?? k.Substring(pfx.Length);
        return k.Substring(pfx.Length);
    }
    return null;  // not a master entity
}
```

### Entity Type from Drawer Content

| Drawer string contains | Entity type |
|------------------------|-------------|
| `"pipe"` (case-insensitive) | Pipe section — AcDbLine |
| `"realview"` (case-insensitive) | Manhole — AcDbCircle |

### GUID Keys in XData

| App name | Content |
|----------|---------|
| `AG_GUID`    | Permanent GUID of this entity (survives handle changes) |
| `TOPOGUID`   | Topology GUID (usually same as AG_GUID) |
| `AG_LAB_HANDLES` | Semicolon-separated handles of label children (may be stale — prefer GUID lookup) |

### Label Entity XData

| App name | Content |
|----------|---------|
| `AG_LAB_ENTITY` | GUID of the master entity this label belongs to |
| `AG_LAB_DATAID` | Property field name the label displays, e.g. `"PIPE_DIA_NOM"` |
| `AG_LAB_ST` | Display state: `0` = hidden, `1` = visible |

**Prerequisite:** Run `ARS_LABEL_N` and `ARS_LABEL_S` inside AutoCAD/Urbano before
scanning labels.  These commands regenerate all label entities with current data.

---

## 8. Spatial Matching Principle

### The Problem

Urbano's XML (from `ARS_EXPORT_XML`) and AutoCAD's DWG entities live in separate
data stores.  There is no embedded OBJID or DWG handle that links an XML node to
its AutoCAD entity directly.

### The Solution: Coordinate Matching

The canonical node position from the XML `@pos` attribute (after HexFloat
decoding) matches the `BlockReference.Position` (or `Circle.Center`) of the
corresponding AutoCAD entity.

**Tolerance:** `0.1` drawing units (metres in Civil 3D).

```csharp
const double Tolerance = 0.1;

bool IsMatch(NodeInfo xmlNode, Point3d acadPos)
{
    double dx = acadPos.X - xmlNode.X;
    double dy = acadPos.Y - xmlNode.Y;
    return (dx * dx + dy * dy) <= Tolerance * Tolerance;
}
```

### Rules

1. **Always use the decoded `@pos` coordinates as the reference**, not any
   other XML attribute.  `TH1` is elevation, not position.
2. **Never use entity handles** to cross-reference XML — handles change when
   Urbano regenerates the drawing.  GUIDs and spatial coordinates are stable.
3. The `@pos` attribute contains at least three hex-floats: `[0]=X`, `[1]=Y`,
   `[2]=Z`.  Only X and Y are needed for the 2-D BoQ match.
4. For pipe sections there is no `@pos` — derive coordinates from start/end
   node GUIDs (`sn`, `en`) and look up those nodes' positions.

---

## 9. NOD Layout

The AutoCAD Named Objects Dictionary (NOD) stores Urbano's global data records.
These are only populated **after** a network analysis has been run and saved from
within Urbano (not from a plain AutoCAD save).

### Known NOD Keys

| Key | Content when populated |
|-----|------------------------|
| `ARSX_NETWORKTOPOLOGY` | Full network topology XML (same structure as ARS_EXPORT_XML output) |
| `ARSX_AUXTOPOLOGY` | Auxiliary topology XML |
| `ARSX_DCT_PIPE` | Pipe catalogue XML |
| `ARSX_DCT_MANHOLE` | Manhole catalogue XML |
| `ARSX_DCT_TRENCH` | Trench dimension catalogue XML |
| `ARSX_LSINSTANCES` | Layer-set instance data |
| `__SA__.LD` | Session data (main data record) |

### XRecord Storage Format

Each NOD key → sub-dictionary → `"XML"` child XRecord.  The XRecord payload
may be:

- One or more `DxfCode.BinaryChunk` (310) entries → reassemble bytes, then:
  - GZip decode if magic `0x1F 0x8B` detected
  - Deflate decode as fallback
  - Decode bytes as UTF-8
- One or more `DxfCode.Text` (1) strings → concatenate directly

### NOD vs ARS_EXPORT_XML

The NOD content is often `"nullData"` (Urbano has not committed it).  The
`ARS_EXPORT_XML` automation path is **always preferred** because:

1. It forces Urbano to serialize its current in-memory graph.
2. It produces the same XML schema regardless of commit state.
3. It works even when the drawing has uncommitted changes.

---

## 10. Calculation Formulas

### Manhole Depth

```
Depth = (TH1 - Lowest_Invert_at_Node) + MHB
```

- `TH1` = terrain elevation at the node
- `Lowest_Invert_at_Node` = minimum of all pipe invert elevations connecting to this node
- `MHB` = manhole base gap (floor-to-lowest-invert clearance)

### Pipe Invert Elevations

```
Invert_Start = LlToInvert(LL10, LLPOS, OD_m, ID_m)
Invert_End   = LlToInvert(LL11, LLPOS, OD_m, ID_m)
```

See the LLPOS table in Section 5 for formulas. Most pipes use LLPOS=8 (Alt iç), so `Invert = LL` directly.

Use `LlToInvert()` — do NOT assume LL10/LL11 always equals AkarKot; it depends on LLPOS.

### Trench Cross-Section Geometry

```
SlopeRatio    = 1 / tan(TR_ANGLE-L * π/180)   [0 for vertical walls = 90°]

# Bedding zone (constant)
TopWidthBed   = TR_WIDTH + 2 × TR_BEDHEIGHT × SlopeRatio
A_Bedding     = (TR_WIDTH + TopWidthBed) / 2 × TR_BEDHEIGHT

# Surround zone (constant)
H_Surround    = Outer_Diameter_m + TR_SANDOVERPIPE
TopWidthSurr  = TopWidthBed + 2 × H_Surround × SlopeRatio
A_SurrGross   = (TopWidthBed + TopWidthSurr) / 2 × H_Surround
A_SurrNet     = A_SurrGross - π × (Outer_Diameter_m / 2)²

# Excavation & Backfill (variable per end)
TrueDepth     = (TH1 - Invert) + TR_BEDHEIGHT
TopWidthExcav = TR_WIDTH + 2 × TrueDepth × SlopeRatio
A_Excav       = (TR_WIDTH + TopWidthExcav) / 2 × TrueDepth
A_Backfill    = A_Excav - A_Bedding - A_SurrGross
```

### Volume Calculations (Prismatoid)

```
V_Bedding  = A_Bedding  × Length2D                        (constant section)
V_Surround = A_SurrNet  × Length2D                        (constant section)
V_Excav    = (A_Excav_Start + A_Excav_End) / 2 × Length2D    (prismatoid average)
V_Backfill = (A_Backfill_Start + A_Backfill_End) / 2 × Length2D
```

---

## 11. Clash Detection Algorithm

When two pipe trenches physically overlap in plan view, the excavation and backfill
volumes must be deducted to avoid double-counting.  Bedding and surround are **not**
deducted (they are physically separate material layers).

### Algorithm

1. Build a 2-D trapezoidal **footprint** for each section (the top opening of the trench):
   - Direction vector: `(endNode.X - startNode.X, endNode.Y - startNode.Y)` normalized
   - Half-widths: `TopWidthExcavS / 2` at start, `TopWidthExcavE / 2` at end
   - 4 vertices in CCW order
2. **AABB pre-test**: reject pairs whose bounding boxes don't overlap (O(n²) is only
   needed for pairs that pass the fast pre-filter)
3. **Sutherland-Hodgman clipping**: clip the subject polygon against each half-plane
   of the clip polygon → intersection polygon
4. **Shoelace area**: `|Σ (xi × yi+1 − xi+1 × yi)| / 2`
5. **Excavation overlap**: `Intersection_Area × avg(TrueDepth_A, TrueDepth_B)`
6. **Backfill overlap**: `Excavation_Overlap × R_bf`, where
   `R_bf = avg(ABackfill / AExcav)` over the two pipes — the trench share that is
   backfill (not bedding/surround). Bedding + surround are therefore never deducted.
7. **Assignment**: lower = deeper-invert pipe (الخط الأدنى), upper = shallower
   (الخط الأعلى). Excavation and backfill overlaps are each assigned independently
   via `OverlapAssignment` (3 × 3 = 9 combinations):
   - `Split` → 50/50 (each pipe loses half)
   - `LowerPipe` → full amount kept by lower ⇒ deducted from upper
   - `UpperPipe` → full amount kept by upper ⇒ deducted from lower
   These are chosen in the startup dialog (Excavation overlap / Backfill overlap).

> **Note — plan-view magnitude.** Overlap detection and magnitude are computed in
> plan view (footprint × depth), unchanged from the original design. The vertical
> cross-section methodology (apex of converging walls, per-layer split) determines
> *how* the shared volume is classified and assigned, not the plan-view magnitude.

### Convexity Requirement

Sutherland-Hodgman requires **CCW convex** polygons.  The trench footprint is
always a convex quadrilateral; the builder produces CCW order, confirmed by the
sign of the shoelace sum.

### Minimum Threshold

Intersections with area < 1×10⁻⁶ m² (< 1 cm²) are ignored as numerical noise.

---

## 12. Known Encoding Pitfalls

### 12.1 The `=EF=BE=89` Coordinate Separator

The separator between X, Y, Z inside `@pos` is the 9-character ASCII string
`"=EF=BE=89"` (quoted-printable bytes).  **Never** split on a Unicode character;
always use the Regex to extract all hex-floats from the raw string.

### 12.2 The `Φ` Diameter Separator in Catalog Names

Urbano encodes the `Φ` (phi) character in `CATALOGITEM_NAME` as `"=EF=BF=98"`.
Extract the nominal diameter using the regex `(?:=[0-9A-Fa-f]{2})+(\d{3,4})`.

### 12.3 XML Encoding Issues

Urbano's export may produce XML files with:
- BOM (byte-order mark) at the start
- Encoding declared as one codepage but written in another
- Legacy Turkish Windows-1254 characters

Robust loader strategy:

```csharp
private static XDocument LoadXmlRobust(string path)
{
    try { return XDocument.Load(path); }
    catch (XmlException) { }

    byte[] raw = File.ReadAllBytes(path);
    foreach (int cp in new[] { 0, 1254, 1252, 28591 })  // Default, Win-1254, Win-1252, ISO-8859-1
    {
        try
        {
            string txt = cp == 0 ? Encoding.Default.GetString(raw)
                                 : Encoding.GetEncoding(cp).GetString(raw);
            // Re-declare encoding as UTF-8 so XDocument.Parse accepts it
            return XDocument.Parse(
                Regex.Replace(txt, @"<\?xml\b[^?]*\?>",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
        }
        catch { }
    }
    throw new Exception("XML load failed — tried all codepages.");
}
```

### 12.4 `XmlConvert.IsXmlChar` / Invalid Characters

If raw text from the NOD (not from ARS_EXPORT_XML) contains bytes that are
invalid in XML 1.0 (control characters, surrogates), strip them before parsing:

```csharp
var sb = new StringBuilder(raw.Length);
foreach (char c in raw)
    if (XmlConvert.IsXmlChar(c)) sb.Append(c);
string cleaned = sb.ToString();
```

This is NOT required for ARS_EXPORT_XML output (Urbano itself produces valid XML).
It IS required when reading raw bytes from NOD XRecords.

### 12.5 GDI+ Crash in AutoCAD AppDomain

The AutoCAD process runs plugins in a shared AppDomain.  **Never call:**
- `ExcelPackage.AutoFit()` / `ws.Column(c).AutoFit()`
- `AdjustToContents()` in any Excel library

Both invoke GDI+ font-measurement APIs which crash in AutoCAD's sandboxed
environment.  Always use fixed column widths:

```csharp
ws.Column(1).Width = 20;   // fixed — never AutoFit
```

### 12.6 ClosedXML is Forbidden

ClosedXML ≥ 0.97 transitively imports `SixLabors.Fonts` → `SixLabors.ImageSharp`,
which calls unmanaged GDI+ APIs and throws:
```
TypeInitializationException: The type initializer for
'SixLabors.Fonts.Tables.TableLoader' threw an exception.
```

**Mandatory:** Use **EPPlus 4.5.3.3** only.  EPPlus 4.x has zero external
dependencies, is LGPL-licensed, and does NOT require `LicenseContext` (that is
EPPlus 5+ only).

### 12.7 `nullData` Sentinel

When a NOD XRecord has not been committed by Urbano, its string value is the
literal `"nullData"`.  Always check for this before attempting XML parsing.

---

## 13. Build & Deployment Rules

### Project Configuration

| Setting | Value |
|---------|-------|
| Target Framework | .NET Framework 4.8 |
| Platform target | x64 |
| Output type | Class Library (DLL) |
| Excel engine | EPPlus 4.5.3.3 (NuGet, `PackageReference`) |

### Build Command

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
$proj    = "D:\Abdullah-ElsaProje\Software\UrbanoMetraj\UrbanoMetraj.csproj"
& $msbuild $proj /p:Configuration=Debug /p:OutputPath=bin\DebugVNN\ /t:Rebuild /v:minimal
```

Use a new output path (`DebugV11`, `DebugV12`, …) for each major build session to
avoid the DLL being locked by a running AutoCAD instance.

### Loading into AutoCAD

```
Command: NETLOAD
File: D:\Abdullah-ElsaProje\Software\UrbanoMetraj\bin\DebugVNN\UrbanoMetraj.dll
```

### Exception Alias — CRITICAL

`Autodesk.AutoCAD.Runtime` defines its own `Exception` class.  In any file that
uses `System.Exception`, add the alias at the top:

```csharp
using Exception = System.Exception;
```

Without this alias, catch clauses silently break against the wrong exception type.

### AutoFit / AdjustToContents — FORBIDDEN

See Section 12.5.  Use only `ws.Column(c).Width = N` (fixed integer widths).

### EPPlus Fill Pattern — Required Before Color

```csharp
// WRONG — color will not appear
cell.Style.Fill.BackgroundColor.SetColor(Color.Blue);

// CORRECT — PatternType must be set first
cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
cell.Style.Fill.BackgroundColor.SetColor(Color.Blue);
```

### Casing: `Numberformat` vs `NumberFormat`

EPPlus 4.x uses lowercase 'n':

```csharp
cell.Style.Numberformat.Format = "#,##0.00";  // lowercase — correct for EPPlus 4.x
```

---

## Appendix A — File & Class Map

| File | Responsibility |
|------|----------------|
| `BoQ/Services/UrbanoExportService.cs` | UI Automation of ARS_EXPORT_XML dialog |
| `BoQ/Services/BoQParserService.cs` | XML graph parser, HexFloat decoder, clash detection |
| `BoQ/Services/ManholeAIService.cs` | Topology analysis, drop-pipe detection, stacking algorithm |
| `BoQ/Services/ManholeConfigService.cs` | Pre-cast catalog template generator (EPPlus) |
| `BoQ/Services/ExcelExportService.cs` | BoQ Excel workbook writer (EPPlus) |
| `BoQ/Models/BoQModels.cs` | All data models (PipeItem, ManholeItem, CatalogPart, …) |
| `BoQ/BoQCommand.cs` | AutoCAD command entry point (`URBANO_BOQ`) |
| `UrbanoXmlExtractor.cs` | Diagnostic: ExtractUrbanoXML command (NOD dump) |
| `UrbanoDataExtractor.cs` | Diagnostic: ExtractUrbanoDataDeep (XData + label scan) |

## Appendix B — Urbano Command Reference

| AutoCAD Command | Effect |
|-----------------|--------|
| `ARS_EXPORT_XML` | Opens modal export dialog — automate with UrbanoExportService |
| `ARS_LABEL_N` | Regenerates all node (manhole) label entities with current data |
| `ARS_LABEL_S` | Regenerates all section (pipe) label entities with current data |
| `ExtractUrbanoXML` | Our diagnostic: dumps all NOD XML to Desktop |
| `ExtractUrbanoDataDeep` | Our diagnostic: full XData + NOD + label scan for selected entities |
| `ExtractUrbanoNOD` | Our diagnostic: raw NOD dump to Desktop\UrbanoNOD.txt |
| `URBANO_BOQ` | Our main command: full BoQ extraction and Excel export |

---

*Last updated: 2026-05-14 — reflects UrbanoMetraj build DebugV11 (Phase 2 complete)*
