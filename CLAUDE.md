# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Is

An AutoCAD Civil 3D 2023 plugin (DLL) that extracts Bill-of-Quantities data from **Urbano 11** utility network designs. Urbano exposes **no public API** — all data must be obtained by automating Urbano's own `ARS_EXPORT_XML` modal dialog via Windows UI Automation + Win32, then parsing its binary-encoded XML output.

## Build & Run

```bat
build_v7.bat
```

This calls MSBuild targeting `bin\DebugV7\`. When the output DLL is locked by a running AutoCAD, increment the output folder suffix (DebugV8, DebugV9, …) — **do not kill AutoCAD**. The `/p:Platform=x64` flag is required; omitting it silently no-ops. The `.csproj` uses an explicit `<Compile>` list, so new `.cs` files must be added manually.

## Tests (Geometry Harness)

The standalone geometry tests live at `Tests/GeoHarness/` and run without AutoCAD:

```
cd Tests\GeoHarness
dotnet run
```

Exit code 0 = all 13 tests pass. Tests validate `ClipperGeo.cs`, `BoQOverlapResolver.cs`, and `TrenchGeometry.cs` using shared source links (no circular references). When modifying polygon math, run this before building the main plugin.

## Architecture

### Data Flow

```
ARS_EXPORT_XML dialog (Urbano 11)
  ↓  automated via UrbanoExportService.cs (STA thread + Win32/UIAutomation)
Hex-float XML → BoQParserService.cs
  ↓  ClipperGeo.cs (Clipper2 polygon booleans) + BoQOverlapResolver.cs
BoQReport (BoQModels.cs)
  ↓  ManholeAIService.cs (smart pre-cast stacking)
ExcelExportService.cs → .xlsx (EPPlus 4.5.3.3)
```

### Key File Responsibilities

| File | Role |
|------|------|
| `UrbanoPlugin.cs` | `IExtensionApplication` entry point; ribbon init |
| `BoQ/BoQCommand.cs` | `URBANO_BOQ` command; orchestrates STA thread + Idle handler |
| `BoQ/Services/UrbanoExportService.cs` | 5-phase modal dialog automation (Win32 + UIAutomation) |
| `BoQ/Services/BoQParserService.cs` | Hex-float XML decoder; topology graph parser |
| `BoQ/Services/ClipperGeo.cs` | Clipper2 integer-space polygon boolean operations |
| `BoQ/Services/BoQOverlapResolver.cs` | AABB pre-filter → Sutherland-Hodgman clash detection |
| `BoQ/Services/ExcelExportService.cs` | Multi-sheet EPPlus workbook generation |
| `BoQ/Services/ManholeAIService.cs` | Greedy pre-cast ring stacking algorithm |
| `BoQ/Models/BoQModels.cs` | All data structures (PipeItem, ManholeItem, CrossSectionStation, …) |
| `BoQ/Models/BoQSettings.cs` | OverlapAssignment enum, ExportLanguage enum, settings class |
| `BoQ/UI/InputBlocker.cs` | Reusable click-blocking wait window — see memory for modification rules |
| `URBANO_ARCHITECTURE_RULES.md` | Authoritative reverse-engineered rulebook — read this before touching parsing |

## Critical Constraints

### Threading (will deadlock if violated)

Dialog automation **must** run on a dedicated **STA thread**, never `Task.Run`:

```csharp
var thread = new Thread(() => { success = svc.WaitAndAutomate(exportPath, cts.Token); });
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
doc.SendStringToExecute("ARS_EXPORT_XML\n", true, false, true);  // blocks main thread
thread.Join();
```

`editor.WriteMessage()` while the Urbano dialog is alive **deadlocks**. Buffer all log messages and flush them only after `WM_CLOSE` is sent in the `finally` block.

### Hex-Float Encoding

Every numeric in Urbano's XML has a type-tag prefix (`8001`, `8005`, `5003`, `5005`) fused directly onto a C99 hex-float. The coordinate separator inside `@pos` is the 9-char ASCII string `"=EF=BE=89"` — **not** the Unicode character U+FE09. Always extract numbers via the mandatory regex:

```csharp
private static readonly Regex HexFloatRx = new Regex(
    @"[-+]?0x[0-9a-fA-F]+\.[0-9a-fA-F]+p[-+]?[0-9]+",
    RegexOptions.Compiled);
```

See `URBANO_ARCHITECTURE_RULES.md §3` for the full `DecodeHexDouble` implementation.

### GDI+ in AutoCAD AppDomain

**Never call** `AutoFit()`, `ws.Column(c).AutoFit()`, or `AdjustToContents()` on EPPlus objects — these crash with a GDI+ exception inside AutoCAD's AppDomain. Use fixed column widths only (`ws.Column(c).Width = 20`).

### Excel Engine

Use **EPPlus 4.5.3.3** only. EPPlus 5+ requires `LicenseContext` and is untested. ClosedXML ≥0.97 depends on SixLabors.ImageSharp which also crashes (GDI+).

EPPlus quirks: property is `Numberformat` (lowercase 'n'); must set `PatternType = Solid` before applying a background color.

### Exception Type Collision

`System.Exception` collides with `Autodesk.AutoCAD.Runtime.Exception`. Use the full namespace or add an alias at the top of any file that catches both.

### Polygon Boolean Engine (ClipperGeo)

- All coordinates scaled by `1e8` before converting to integers
- All input rings must be CCW convex
- Minimum area threshold: `1e-6 m²`
- Run spur-removal after every `Difference()` operation

### Excavation Independence Invariant

Excavation depth/volume is **fully decoupled** from yataklama/gömlekleme/geri dolgu layers. They may reference different datums and use different logic. Never force cross-layer consistency checks.
