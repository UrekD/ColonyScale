using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityStubs.Gui;
using Verse;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// Text.CalcSize measures text on every call (StripTags, a GUIContent update and, in 1.7, a full text layout in the
    /// ImGui backend: GUIStyle.CalcSize -> ImGuiGuiBackend.MeasureText -> TextLayout.Layout),
    /// and labels are measured every frame: three times per humanlike pawn label alone (GenMapUI.DrawPawnLabel ->
    /// GetPawnLabel -> GenText.Truncate). Results are memoized per (font, word wrap, anchor, text); a prefix returns
    /// the stored size and a postfix stores vanilla's result on a miss.
    ///
    /// Exact: CalcSize returns CurFontStyle.CalcSize(StripTags(text)). StripTags is a pure function of the text and
    /// CurFontStyle is fontStyles[Font] after setting its alignment and wordWrap from Anchor and WordWrap, so the
    /// result depends only on the key, on that style's other properties and on the backend's fonts. The stub
    /// GUIStyle.CalcSize lays out text alone (no wrap, no image) from the style's font index, size and font style, and
    /// the backend's layout depends only on those and on the host's fonts: ImGuiHost.PixelSizeFor is the given size or
    /// the font's import size, glyph advances come from the fonts, which are only appended, and are rebuilt when
    /// FontGeneration changes (a language switch picks other fallback fonts). Vanilla never changes the style after
    /// Text's static constructor; in case a mod does, the cache of a font is dropped when its font, size, font style,
    /// rich-text flag, the backend instance, its FontGeneration or the clamped font index differ on any call, or its
    /// padding, fixed size, image position or Prefs.UIScale differ at the first call of a frame. With any other GUI
    /// backend (or none yet) the cache is bypassed. A hit still evaluates CurFontStyle, so the style is left as vanilla
    /// leaves it; the GUIContent scratch object is private and always overwritten before use. Null/empty text,
    /// a missing font, an invalid Font/Anchor value and calls off the main thread run vanilla. Any other mod's patch
    /// on CalcSize turns this off (a returning-false prefix would also skip theirs), and only results of the
    /// unpatched original are stored.
    /// </summary>
    public sealed class TextSizeCache : Optimization
    {
        private const string OptId = "textSizeCache";
        private const int MaxEntriesPerSlot = 2048;
        private const int Anchors = 9;

        public override string Id => OptId;
        public override string Label => "Cache text measurements";
        public override string Description =>
            "Labels (pawn names, item labels, UI text) are measured once and reused instead of being measured again every frame.";
        public override string Category => "Frame";

        public override IEnumerable<string> IncompatiblePackageIds => new[] { "Taranchuk.PerformanceOptimizer" };

        private static readonly MethodInfo CalcSize = AccessTools.Method(typeof(Text), nameof(Text.CalcSize), new[] { typeof(string) });
        // The 1.7 host types are internal to RimWorld.dll; resolved by name, so a rename leaves DependsOn incomplete
        // (the optimization stays off) instead of failing to load.
        private static readonly Type BackendType = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiGuiBackend");
        private static readonly Type HostType = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiHost");
        private static AccessTools.FieldRef<object, object> backendHost;
        private static AccessTools.FieldRef<object, int> hostFontGeneration;
        private static AccessTools.FieldRef<object, object> hostFonts;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            backendHost = AccessTools.FieldRefAccess<object>(BackendType, "host");
            hostFontGeneration = AccessTools.FieldRefAccess<int>(HostType, "fontGeneration");
            hostFonts = AccessTools.FieldRefAccess<object>(HostType, "fonts");
            yield return PatchSpec.For(CalcSize)
                .WithPrefix(typeof(TextSizeCache), nameof(Prefix), replacesOriginal: true, priority: Priority.First)
                .WithPostfix(typeof(TextSizeCache), nameof(Postfix), priority: Priority.First);
        }

        // The target itself is listed so that any other mod's patch on it keeps this off.
        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            CalcSize,
            AccessTools.PropertyGetter(typeof(Text), nameof(Text.CurFontStyle)),
            AccessTools.PropertyGetter(typeof(Text), nameof(Text.Font)),
            AccessTools.PropertyGetter(typeof(Text), nameof(Text.Anchor)),
            AccessTools.PropertyGetter(typeof(Text), nameof(Text.WordWrap)),
            AccessTools.Method(typeof(ColoredText), nameof(ColoredText.StripTags)),
            AccessTools.Method(typeof(GUIStyle), nameof(GUIStyle.CalcSize), new[] { typeof(GUIContent) }),
            AccessTools.Method(BackendType, "MeasureText"),
            AccessTools.Method(HostType, "PixelSizeFor"),
            AccessTools.Method(HostType, "SetTextLanguage"),
            AccessTools.Method(AccessTools.TypeByName("RimWorldWaylith.Imgui.TextLayout"), "Layout"),
        };

        public override void ClearCaches()
        {
            Array.Clear(caches, 0, caches.Length);
            Array.Clear(styles, 0, styles.Length);
        }

        /// <summary>Indexed by (font * 2 + wordWrap) * 9 + anchor.</summary>
        private static readonly Dictionary<string, Vector2>[] caches = new Dictionary<string, Vector2>[3 * 2 * Anchors];

        /// <summary>Snapshot of fontStyles[font] the cached sizes of that font were measured with.</summary>
        private sealed class StyleState
        {
            public Font font;
            public int fontSize;
            public FontStyle fontStyle;
            public bool richText;
            public int frame;
            public int padLeft, padRight, padTop, padBottom;
            public float fixedWidth, fixedHeight;
            public ImagePosition imagePosition;
            public float uiScale;
            public object backend;
            public int fontGeneration;
            public int fontIndex;
        }

        private static readonly StyleState[] styles = new StyleState[3];

        public struct CallState
        {
            public int slot;     // -1: nothing to do
            public bool verify;  // hit in verify mode: compare with size
            public Vector2 size;
        }

        public static bool Prefix(string text, ref Vector2 __result, out CallState __state)
        {
            __state = new CallState { slot = -1 };
            if (string.IsNullOrEmpty(text) || !UnityData.IsInMainThread) return true;
            int font = (int)Text.Font;
            int anchor = (int)Text.Anchor;
            if ((uint)font >= 3 || (uint)anchor >= Anchors) return true;
            if (!StyleValid(font, Text.fontStyles[font])) return true;

            int slot = (font * 2 + (Text.WordWrap ? 1 : 0)) * Anchors + anchor;
            var cache = caches[slot];
            if (cache == null || !cache.TryGetValue(text, out Vector2 size))
            {
                __state.slot = slot;
                return true;
            }
            _ = Text.CurFontStyle; // vanilla's side effect: the style's alignment and wordWrap
            if (Verify.Enabled)
            {
                __state.verify = true;
                __state.size = size;
                return true;
            }
            __result = size;
            return false;
        }

        public static void Postfix(string text, Vector2 __result, CallState __state, bool __runOriginal)
        {
            if (__state.verify)
            {
                Verify.Record(OptId, __result.x.Equals(__state.size.x) && __result.y.Equals(__state.size.y));
                return;
            }
            if (__state.slot < 0 || !__runOriginal) return;
            var cache = caches[__state.slot];
            if (cache == null) caches[__state.slot] = cache = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            else if (cache.Count >= MaxEntriesPerSlot) cache.Clear();
            cache[text] = __result;
        }

        /// <summary>False if the font or the ImGui backend is missing; drops the font's cached sizes if its style or the backend's fonts changed.</summary>
        private static bool StyleValid(int font, GUIStyle style)
        {
            var s = styles[font];
            Font f = style.font;
            object backend = GuiBackend.Current;
            if ((object)f == null || backend == null || backend.GetType() != BackendType) return false;
            object host = backendHost(backend);
            int generation = System.Threading.Volatile.Read(ref hostFontGeneration(host));
            int fontIndex = Math.Clamp(style.FontIndex, 0, ((System.Collections.ICollection)hostFonts(host)).Count - 1);
            if (s == null || !ReferenceEquals(f, s.font) || style.fontSize != s.fontSize || style.fontStyle != s.fontStyle || style.richText != s.richText
                || !ReferenceEquals(backend, s.backend) || generation != s.fontGeneration || fontIndex != s.fontIndex)
            {
                Reset(font, style, f, backend, generation, fontIndex);
                return true;
            }
            int frame = Time.frameCount;
            if (frame != s.frame)
            {
                s.frame = frame;
                RectOffset pad = style.padding;
                if (pad.left != s.padLeft || pad.right != s.padRight || pad.top != s.padTop || pad.bottom != s.padBottom
                    || !style.fixedWidth.Equals(s.fixedWidth) || !style.fixedHeight.Equals(s.fixedHeight)
                    || style.imagePosition != s.imagePosition || !Prefs.UIScale.Equals(s.uiScale))
                    Reset(font, style, f, backend, generation, fontIndex);
            }
            return true;
        }

        private static void Reset(int font, GUIStyle style, Font f, object backend, int generation, int fontIndex)
        {
            for (int i = font * 2 * Anchors; i < (font + 1) * 2 * Anchors; i++) caches[i] = null;
            RectOffset pad = style.padding;
            styles[font] = new StyleState
            {
                font = f,
                fontSize = style.fontSize,
                fontStyle = style.fontStyle,
                richText = style.richText,
                frame = Time.frameCount,
                padLeft = pad.left,
                padRight = pad.right,
                padTop = pad.top,
                padBottom = pad.bottom,
                fixedWidth = style.fixedWidth,
                fixedHeight = style.fixedHeight,
                imagePosition = style.imagePosition,
                uiScale = Prefs.UIScale,
                backend = backend,
                fontGeneration = generation,
                fontIndex = fontIndex,
            };
        }
    }
}
