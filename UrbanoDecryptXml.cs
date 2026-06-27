// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoDecryptXml.cs
//
// Command : ARS_DECRYPT_XML
//
// PURPOSE
// ───────
// Opens a raw Urbano-exported XML file (from ars_export_xml or ExtractUrbanoXML),
// decodes every IEEE 754 hex-float token into a readable decimal number, then
// pretty-prints and saves the result to Desktop\Readable_Urbano_Data.xml.
//
// The hex-float format Urbano uses looks like:
//   8001-0x1.95bb0018dd8bap-3   (single double, negative)
//   80010x1.e71e20bf986b3p+9    (single double, positive)
//   8005-0x1.4d...p+N=EF=BE=89...  (position: 3 doubles separated by =EF=BE=89)
//
// All occurrences are matched by a single compiled Regex and replaced via a
// MatchEvaluator — one O(n) pass over the string, safe for 6 MB+ files.
//
// DECODE ALGORITHM (confirmed against Urbano 11 schema)
// ──────────────────────────────────────────────────────
//   hex-float = (-?) 0x [intHex] . [fracHex] p [exp]
//   biasedExp = exp + 1023
//   fracBits  = first 13 hex chars of fracHex (= 52 mantissa bits)
//   bits      = (biasedExp << 52) | fracBits
//   value     = BitConverter.Int64BitsToDouble(bits)   [negate if sign == '-']
//   special   = intPart == 0 AND frac all-zero  →  0.0000
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoDecryptXml))]

namespace UrbanoMetraj
{
    public class UrbanoDecryptXml
    {
        // ── Compiled regex ────────────────────────────────────────────────────
        // Groups: 1=sign  2=intHex  3=fracHex  4=exponent
        // Matches both positive and negative hex-floats anywhere in the string.
        private static readonly Regex HexFloatRx = new Regex(
            @"(-?)0x([0-9a-fA-F]+)\.([0-9a-fA-F]*)p([+\-]?\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ═════════════════════════════════════════════════════════════════════
        // COMMAND ENTRY POINT
        // ═════════════════════════════════════════════════════════════════════

        [CommandMethod("ARS_DECRYPT_XML")]
        public void ArsDecryptXml()
        {
            Document acDoc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed    = acDoc.Editor;

            void Say(string msg) => ed.WriteMessage("\n" + msg);

            Say(new string('═', 70));
            Say("  ARS_DECRYPT_XML — Urbano Universal XML Decoder");
            Say(new string('═', 70));

            // ── 1. File selection ─────────────────────────────────────────────
            var openOpts = new PromptOpenFileOptions(
                "\nSelect raw Urbano XML file to decode");
            openOpts.Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*";

            PromptFileNameResult fr = ed.GetFileNameForOpen(openOpts);
            if (fr.Status != PromptStatus.OK || string.IsNullOrEmpty(fr.StringResult))
            {
                Say("  Cancelled.");
                return;
            }

            string inputPath = fr.StringResult;
            Say($"  Input  : {inputPath}");

            // ── 2. Read & clean ───────────────────────────────────────────────
            string raw;
            try
            {
                raw = ReadWithFallback(inputPath, ed);
            }
            catch (System.Exception ex)
            {
                Say($"  ERROR reading file: {ex.Message}");
                return;
            }

            Say($"  Read   : {raw.Length:#,0} chars");

            // Remove XML-illegal low-level ASCII control chars (keep \t \n \r).
            raw = StripIllegalXmlChars(raw);

            // ── 3. Universal hex-float decode ─────────────────────────────────
            int hitCount = 0;
            string decoded = HexFloatRx.Replace(raw, m =>
            {
                hitCount++;
                return DecodeHexFloat(m);
            });

            Say($"  Decoded: {hitCount:#,0} hex-float value(s) replaced");

            // ── 4. Parse XML ──────────────────────────────────────────────────
            XDocument xdoc;
            try
            {
                xdoc = XDocument.Parse(decoded, LoadOptions.None);
            }
            catch (System.Exception ex)
            {
                Say($"  XML parse failed: {ex.Message}");
                // Still save the decoded text so the user can inspect it
                string fallbackPath = Path.Combine(Desktop, "Readable_Urbano_Data_DECODED.txt");
                try
                {
                    File.WriteAllText(fallbackPath, decoded, Encoding.UTF8);
                    Say($"  Decoded text (pre-parse) saved → {fallbackPath}");
                }
                catch { }
                return;
            }

            // ── 5. Pretty-print & save ────────────────────────────────────────
            string outPath = Path.Combine(Desktop, "Readable_Urbano_Data.xml");

            var xmlSettings = new XmlWriterSettings
            {
                Indent              = true,
                IndentChars         = "  ",
                NewLineOnAttributes = false,
                Encoding            = new UTF8Encoding(false), // UTF-8, no BOM
                OmitXmlDeclaration  = false,
            };

            using (XmlWriter writer = XmlWriter.Create(outPath, xmlSettings))
                xdoc.Save(writer);

            long outBytes = new FileInfo(outPath).Length;

            Say($"  Output : {outPath}");
            Say($"  Size   : {outBytes:#,0} bytes");
            Say(new string('═', 70));
            Say("  Decoded file saved to Desktop!");
            Say("  You can now explore the entire raw database.");
            Say(new string('═', 70));
        }

        // ═════════════════════════════════════════════════════════════════════
        // Read file with encoding fallback
        // Try UTF-8 strict first, then Windows-1254 (Turkish ANSI), then lenient.
        // ═════════════════════════════════════════════════════════════════════

        private static string ReadWithFallback(string path, Editor ed)
        {
            // UTF-8 strict (encoderShouldEmitUTF8Identifier=false, throwOnInvalidBytes=true)
            try
            {
                return File.ReadAllText(path, new UTF8Encoding(false, true));
            }
            catch { }

            // Windows-1254 (Turkish ANSI — used by some Urbano export variants)
            try
            {
                Encoding w1254 = Encoding.GetEncoding(1254);
                ed.WriteMessage("\n  (Falling back to Windows-1254 encoding)");
                return File.ReadAllText(path, w1254);
            }
            catch { }

            // Last resort: lenient UTF-8
            ed.WriteMessage("\n  (Using lenient UTF-8 fallback)");
            return File.ReadAllText(path, new UTF8Encoding(false, false));
        }

        // ═════════════════════════════════════════════════════════════════════
        // Strip characters that are illegal in XML 1.0
        // Legal: #x9 | #xA | #xD | [#x20–#xD7FF] | [#xE000–#xFFFD]
        // ═════════════════════════════════════════════════════════════════════

        private static string StripIllegalXmlChars(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\t' || c == '\n' || c == '\r'  ||
                    (c >= '\x20' && c <= '\xD7FF')        ||
                    (c >= '\xE000' && c <= '\xFFFD'))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Decode a single hex-float regex match → decimal string
        //
        // Regex groups: 1=sign('-'|'')  2=intHex  3=fracHex  4=exp
        //
        // IEEE 754 double layout:
        //   bits[63]    = sign
        //   bits[62:52] = biased exponent (exp + 1023)
        //   bits[51:0]  = mantissa (fractional hex digits, 52 bits)
        // ═════════════════════════════════════════════════════════════════════

        private static string DecodeHexFloat(Match m)
        {
            try
            {
                bool   negative = m.Groups[1].Value == "-";
                string intHex   = m.Groups[2].Value;
                string fracHex  = m.Groups[3].Value;
                int    expVal   = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

                long intPart = Convert.ToInt64(intHex, 16);

                // Zero special case (e.g. Z=0 nodes: 0x0.0p+0)
                if (intPart == 0)
                {
                    bool allZero = true;
                    foreach (char c in fracHex)
                        if (c != '0') { allZero = false; break; }
                    if (allZero || fracHex.Length == 0) return "0.0000";
                }

                // Normalise fractional part to exactly 13 hex digits (= 52 bits).
                // Pad with trailing zeros if shorter; truncate if longer.
                const int MantissaHexLen = 13;
                string frac13 = fracHex.Length >= MantissaHexLen
                    ? fracHex.Substring(0, MantissaHexLen)
                    : fracHex + new string('0', MantissaHexLen - fracHex.Length);

                long fracBits  = Convert.ToInt64(frac13, 16);
                int  biasedExp = expVal + 1023;

                long doubleBits = ((long)biasedExp << 52) | fracBits;
                double value    = BitConverter.Int64BitsToDouble(doubleBits);

                if (negative) value = -value;

                return value.ToString("F4", CultureInfo.InvariantCulture);
            }
            catch
            {
                return m.Value; // decode failure — keep original token unchanged
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string Desktop
            => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }
}
