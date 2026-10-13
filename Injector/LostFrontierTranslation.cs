// Lost Frontier - multi-language translation plugin for BepInEx 5
// Hooks:
//   * TextPrint.printText(string)  -> formatted texts (dialogues, descriptions, toasts)
//   * sweep of every TextMesh just before rendering -> UI labels, unit names, buttons...
// Texts: BepInEx/plugins/LostFrontierTranslation/translations/*.tsv (one file per kind of game text,
//        English and every language on the same row)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LostFrontierTranslation
{
    [BepInPlugin("lostfrontier.translation", "Lost Frontier Translation", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin I;

        ConfigEntry<string> cfgLanguage;
        ConfigEntry<KeyCode> cfgNextKey;
        ConfigEntry<string> cfgGlyphMode;
        ConfigEntry<bool> cfgShrink;
        ConfigEntry<bool> cfgLogMissing;
        ConfigEntry<float> cfgFitTolerance;

        readonly TranslationCore core = new TranslationCore();
        int generation = 1;                         // bumps on every language change

        class MeshState { public string Original, Applied; public int Gen; }
        readonly Dictionary<int, MeshState> meshStates = new Dictionary<int, MeshState>();
        readonly HashSet<int> excluded = new HashSet<int>();        // TextPrint row meshes
        TextMesh[] meshes = new TextMesh[0];
        float nextRefresh;
        int lastSweepFrame = -1;

        readonly Dictionary<int, string> printOriginals = new Dictionary<int, string>();
        readonly Dictionary<int, float> baseCharSize = new Dictionary<int, float>();
        readonly Dictionary<long, bool> glyphCache = new Dictionary<long, bool>();

        readonly HashSet<string> missing = new HashSet<string>();
        readonly List<string> missingQueue = new List<string>();
        float nextMissingFlush;

        string toast; float toastUntil;

        void Awake()
        {
            I = this;
            string pluginDir = Path.GetDirectoryName(Info.Location);
            cfgLanguage = Config.Bind("General", "Language", "auto",
                "auto = Windows language, or a language column code of the translation files (en = original English). Also changeable in the game's Options menu.");
            cfgNextKey = Config.Bind("General", "SwitchLanguageKey", KeyCode.None,
                "Optional hotkey that cycles languages (None = disabled; use the Options menu).");
            cfgFitTolerance = Config.Bind("Display", "AutoFitTolerance", 1.0f,
                "Max width of a translated label relative to the English one (1.0 = never wider). Wider labels are scaled down. 0 = off.");
            cfgGlyphMode = Config.Bind("Display", "MissingGlyphs", "Auto",
                "Auto = strip accents only when the game font lacks the letter (font patch not installed); Keep = never strip; Strip = always strip.");
            cfgShrink = Config.Bind("Display", "ShrinkLongTexts", true,
                "Slightly shrinks dialogue text that is longer than the English so it fits the 3-line boxes.");
            cfgLogMissing = Config.Bind("Debug", "LogUntranslated", true,
                "Writes texts that have no translation to BepInEx/LFT_untranslated_<lang>.tsv (same columns as the translation files: paste the rows into the right file).");

            core.Warn = s => Logger.LogWarning(s);
            core.Missing = NoteMissing;
            string rep = core.Load(Path.Combine(pluginDir, "translations"));
            string startCode = ResolveCode(cfgLanguage.Value);
            if (!core.SetLanguage(startCode)) Logger.LogWarning("Language '" + startCode + "' not found, using English.");

            try { new Harmony("lostfrontier.translation").PatchAll(typeof(Plugin).Assembly); }
            catch (Exception e) { Logger.LogError("Harmony patch failed: " + e); }

            SceneManager.sceneLoaded += (s, m) => { nextRefresh = 0; };
            Camera.onPreCull += SweepBeforeRender;
            Logger.LogInfo("Loaded: " + rep + "| current: " + core.CurrentCode + " | key: " + cfgNextKey.Value);
        }

        // ------------------------------------------------------------ language switching
        string ResolveCode(string v)
        {
            v = (v ?? "auto").Trim().ToLowerInvariant();
            if (v != "auto" && v != "") return v;
            switch (Application.systemLanguage)
            {
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Portuguese: return "pt";
                default: return "en";
            }
        }

        List<string> AllCodes()
        {
            var l = new List<string> { "en" };
            foreach (var x in core.Langs) l.Add(x.Code);
            return l;
        }

        void StepLanguage(int dir)
        {
            var codes = AllCodes();
            int i = codes.IndexOf(core.CurrentCode);
            i = ((i + dir) % codes.Count + codes.Count) % codes.Count;
            ApplyLanguage(codes[i], false);
        }

        void NextLanguage() { StepLanguage(1); }

        void ApplyLanguage(string code, bool showToast)
        {
            core.SetLanguage(code);
            cfgLanguage.Value = code;   // persisted in BepInEx/config
            generation++;
            missing.Clear();
            if (showToast || cfgNextKey.Value != KeyCode.None)
            {
                toast = "Language: " + core.CurrentName;
                toastUntil = Time.realtimeSinceStartup + 3f;
            }
            UpdateLangValue();
            RefreshAllPrints();
            nextRefresh = 0;
        }

        // ------------------------------------------------------------ untranslated log
        void NoteMissing(string s)
        {
            if (!cfgLogMissing.Value || s.Length > 2000) return;
            if (missing.Add(s)) missingQueue.Add(s);
        }

        void FlushMissing()
        {
            if (missingQueue.Count == 0 || core.Current == null) { missingQueue.Clear(); return; }
            try
            {
                string path = Path.Combine(Paths.BepInExRootPath, "LFT_untranslated_" + core.CurrentCode + ".tsv");
                var existing = new HashSet<string>();
                var sb = new StringBuilder();
                if (File.Exists(path)) foreach (var l in File.ReadAllLines(path, Encoding.UTF8)) existing.Add(l);
                else sb.Append("en\t" + core.CurrentCode + "\r\n");
                foreach (var s in missingQueue)
                {
                    string e = TranslationCore.Escape(s) + "\t";
                    if (existing.Add(e)) sb.Append(e).Append("\r\n");
                }
                if (sb.Length > 0) File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e) { Logger.LogWarning("missing log: " + e.Message); }
            missingQueue.Clear();
        }

        // ------------------------------------------------------------ glyphs
        bool FontHas(Font f, char c)
        {
            if (f == null) return true;
            long key = ((long)f.GetInstanceID() << 20) ^ c;
            bool v;
            if (!glyphCache.TryGetValue(key, out v))
            {
                try { v = f.HasCharacter(c); } catch { v = true; }
                glyphCache[key] = v;
            }
            return v;
        }

        string FixGlyphs(string s, Font f)
        {
            string mode = cfgGlyphMode.Value ?? "Auto";
            if (mode.Equals("Keep", StringComparison.OrdinalIgnoreCase)) return s;
            bool always = mode.Equals("Strip", StringComparison.OrdinalIgnoreCase);
            return TranslationCore.FoldGlyphs(s, c => FontHas(f, c), always);
        }

        // ------------------------------------------------------------ TextPrint (formatted texts)
        static Font PrintFont(TextPrint tp)
        {
            try { return tp.rows[0].textMesh[0].font; } catch { return null; }
        }

        internal string OnPrintText(TextPrint tp, string original)
        {
            if (tp == null) return original;
            ExcludeRows(tp);
            string eng = core.ToEnglish(original);
            printOriginals[tp.GetInstanceID()] = eng;
            string t = FixGlyphs(core.Translate(eng), PrintFont(tp));
            ApplyShrink(tp, eng, t);
            return t;
        }

        // The dialogue box meshes belong to the typewriter: make sure the label sweep never touches them,
        // even if the box appeared after the last refresh of the mesh list.
        void ExcludeRows(TextPrint tp)
        {
            try
            {
                foreach (var row in tp.rows)
                    foreach (var tm in row.textMesh)
                        if (tm != null && excluded.Add(tm.GetInstanceID())) meshStates.Remove(tm.GetInstanceID());
            }
            catch { }
        }

        void ApplyShrink(TextPrint tp, string eng, string tr)
        {
            float factor = 1f;
            if (cfgShrink.Value && core.Current != null)
            {
                int le = TranslationCore.VisibleLen(eng), lt = TranslationCore.VisibleLen(tr);
                if (lt > 70 && lt > le * 1.05f) factor = Mathf.Clamp(le * 1.05f / lt, 0.72f, 1f);
            }
            try
            {
                foreach (var row in tp.rows)
                    foreach (var tm in row.textMesh)
                    {
                        if (tm == null) continue;
                        int id = tm.GetInstanceID();
                        float bs;
                        if (!baseCharSize.TryGetValue(id, out bs))
                        {
                            // size before any label auto-fit was applied to it
                            if (!meshBaseSize.TryGetValue(id, out bs)) bs = tm.characterSize;
                            baseCharSize[id] = bs;
                        }
                        tm.characterSize = bs * factor;
                    }
            }
            catch { }
        }

        void RefreshAllPrints()
        {
            UnityEngine.Object[] all;
            try { all = Resources.FindObjectsOfTypeAll(typeof(TextPrint)); } catch { return; }
            foreach (var o in all)
            {
                var tp = o as TextPrint;
                if (tp == null || !tp.gameObject.activeInHierarchy) continue;
                string orig;
                if (!printOriginals.TryGetValue(tp.GetInstanceID(), out orig) || string.IsNullOrEmpty(orig)) continue;
                bool visible = tp.enabled;
                try { visible |= tp.rows[0].textRenderer[0].enabled; } catch { }
                if (!visible) continue;
                try { tp.printText(orig); } catch (Exception e) { Logger.LogWarning("reprint: " + e.Message); }
            }
        }

        // ------------------------------------------------------------ TextMesh sweep
        void RefreshMeshList()
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(typeof(TextMesh));
                var list = new TextMesh[arr.Length];
                for (int i = 0; i < arr.Length; i++) list[i] = arr[i] as TextMesh;
                meshes = list;
                excluded.Clear();
                EnsureOptionsRow();
                foreach (var tm in langValueMeshes) if (tm != null) excluded.Add(tm.GetInstanceID());
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(TextPrint)))
                {
                    var tp = o as TextPrint;
                    if (tp == null || tp.rows == null) continue;
                    foreach (var row in tp.rows)
                        if (row != null && row.textMesh != null)
                            foreach (var tm in row.textMesh) if (tm != null) excluded.Add(tm.GetInstanceID());
                }
            }
            catch (Exception e) { Logger.LogWarning("refresh: " + e.Message); }
        }

        void Sweep()
        {
            if (Time.frameCount == lastSweepFrame) return;
            lastSweepFrame = Time.frameCount;
            if (Time.realtimeSinceStartup >= nextRefresh)
            {
                RefreshMeshList();
                nextRefresh = Time.realtimeSinceStartup + 0.5f;
            }
            for (int i = 0; i < meshes.Length; i++)
            {
                var tm = meshes[i];
                if ((object)tm == null) continue;
                if (!tm) { meshes[i] = null; continue; }
                int id = tm.GetInstanceID();
                if (excluded.Contains(id)) continue;
                string text = tm.text;
                MeshState st;
                meshStates.TryGetValue(id, out st);
                if (st != null && text == st.Applied)
                {
                    if (st.Gen == generation) continue;
                    // language changed: re-apply from the stored English original
                    string t2 = FixGlyphs(core.Translate(st.Original), tm.font);
                    st.Gen = generation;
                    if (t2 != text) tm.text = t2;
                    st.Applied = t2;
                    Fit(tm, st.Original, t2);
                    continue;
                }
                if (st == null) { st = new MeshState(); meshStates[id] = st; }
                st.Gen = generation;
                if (string.IsNullOrEmpty(text) || TranslationCore.IsTrivial(text))
                {
                    st.Original = text; st.Applied = text;
                    continue;
                }
                // new text set by the game (or by a previous language)
                string eng = core.ToEnglish(text);
                string t = FixGlyphs(core.Translate(eng), tm.font);
                st.Original = eng;
                if (t != text) tm.text = t;
                st.Applied = t;
                Fit(tm, eng, t);
            }
        }

        // ------------------------------------------------------------ auto-fit of UI labels
        readonly Dictionary<int, float> meshBaseSize = new Dictionary<int, float>();

        static float Measure(TextMesh tm, string s)
        {
            var f = tm.font;
            if (f == null || string.IsNullOrEmpty(s)) return s == null ? 0 : s.Length;
            int size = tm.fontSize > 0 ? tm.fontSize : f.fontSize;
            var style = tm.fontStyle;
            try { f.RequestCharactersInTexture(s, size, style); } catch { }
            float best = 0, cur = 0;
            CharacterInfo ci;
            foreach (char c in s)
            {
                if (c == '\n') { if (cur > best) best = cur; cur = 0; continue; }
                if (f.GetCharacterInfo(c, out ci, size, style)) cur += ci.advance;
                else cur += size * 0.5f;
            }
            return cur > best ? cur : best;
        }

        void Fit(TextMesh tm, string eng, string tr)
        {
            float tol = cfgFitTolerance.Value;
            int id = tm.GetInstanceID();
            float baseSize;
            if (!meshBaseSize.TryGetValue(id, out baseSize))
            {
                if (tol <= 0) return;
                baseSize = tm.characterSize; meshBaseSize[id] = baseSize;
            }
            float factor = 1f;
            if (tol > 0 && core.Current != null && eng != tr && !string.IsNullOrEmpty(eng))
            {
                float we = Measure(tm, eng), wt = Measure(tm, tr);
                if (we > 0 && wt > we * tol) factor = Mathf.Max(0.55f, we * tol / wt);
            }
            float want = baseSize * factor;
            if (Mathf.Abs(tm.characterSize - want) > 0.0001f) tm.characterSize = want;
        }

        // ------------------------------------------------------------ "Language" row in the Options menu
        const string RowName = "LFT_Language", ValueName = "LFT_LanguageValue";
        readonly List<TextMesh> langValueMeshes = new List<TextMesh>();
        readonly List<Transform> langLeft = new List<Transform>(), langRight = new List<Transform>();

        void UpdateLangValue()
        {
            string name = core.Current == null ? "English" : core.Current.Name;
            foreach (var tm in langValueMeshes)
                if (tm != null && tm.text != name) tm.text = name;
        }

        static void SetAllText(GameObject go, string text)
        {
            foreach (var tm in go.GetComponentsInChildren<TextMesh>(true)) tm.text = text;
        }

        void EnsureOptionsRow()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Transform)))
            {
                var scaleRoot = o as Transform;
                if (scaleRoot == null || scaleRoot.name != "ScaleRoot" || scaleRoot.parent == null || scaleRoot.parent.name != "OptionsRoot") continue;
                if (!scaleRoot.gameObject.scene.IsValid()) continue;          // skip prefab assets
                if (scaleRoot.Find(RowName) != null) continue;               // already built
                var label = scaleRoot.Find("ScreenMode");
                var value = scaleRoot.Find("Resolution Number (1)");
                var music = scaleRoot.Find("Music");
                if (label == null || value == null || music == null) continue;
                try { BuildRow(scaleRoot, label, value, music.localPosition.y); }
                catch (Exception e) { Logger.LogWarning("options row: " + e); }
            }
            langValueMeshes.RemoveAll(t => t == null);
            langLeft.RemoveAll(t => t == null);
            langRight.RemoveAll(t => t == null);
        }

        void BuildRow(Transform scaleRoot, Transform label, Transform value, float topY)
        {
            // compress the 4 existing rows to make room for a 5th one
            const float step = 0.9f;
            float top = topY + 0.22f;
            string[][] rows = {
                new[] { "Music", "MusicNumber" }, new[] { "SFX", "SFXNumber" },
                new[] { "Resolution", "Resolution Number" }, new[] { "ScreenMode", "Resolution Number (1)" } };
            for (int r = 0; r < rows.Length; r++)
                foreach (var n in rows[r])
                {
                    var t = scaleRoot.Find(n);
                    if (t == null) continue;
                    var p = t.localPosition; p.y = top - r * step; t.localPosition = p;
                }
            float y = top - 4 * step;

            var newLabel = (GameObject)Instantiate(label.gameObject, scaleRoot);
            newLabel.name = RowName;
            var lp = label.localPosition; lp.y = y; newLabel.transform.localPosition = lp;
            newLabel.transform.localRotation = label.localRotation; newLabel.transform.localScale = label.localScale;
            SetAllText(newLabel, "Language");

            var newValue = (GameObject)Instantiate(value.gameObject, scaleRoot);
            newValue.name = ValueName;
            var vp = value.localPosition; vp.y = y; newValue.transform.localPosition = vp;
            newValue.transform.localRotation = value.localRotation; newValue.transform.localScale = value.localScale;
            foreach (var t in newValue.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("RightArrow")) { t.name = "LFT_LangRight"; langRight.Add(t); }
                else if (t.name.StartsWith("LeftArrow")) { t.name = "LFT_LangLeft"; langLeft.Add(t); }
            }
            foreach (var tm in newValue.GetComponentsInChildren<TextMesh>(true)) { langValueMeshes.Add(tm); excluded.Add(tm.GetInstanceID()); }
            UpdateLangValue();
            Logger.LogInfo("Language row added to the Options menu.");
        }

        void HandleOptionsClick()
        {
            if (langLeft.Count == 0 && langRight.Count == 0) return;
            try
            {
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null || !cam.isActiveAndEnabled) continue;
                    var hits = Physics.RaycastAll(cam.ScreenPointToRay(Input.mousePosition), 2000f);
                    foreach (var h in hits)
                    {
                        var t = h.transform;
                        if (t == null || !t.gameObject.activeInHierarchy) continue;
                        if (langRight.Contains(t)) { StepLanguage(1); return; }
                        if (langLeft.Contains(t)) { StepLanguage(-1); return; }
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("options click: " + e.Message); }
        }

        void SweepBeforeRender(Camera c) { try { Sweep(); } catch (Exception e) { Logger.LogWarning("sweep: " + e.Message); } }

        void LateUpdate() { try { Sweep(); } catch { } }

        void Update()
        {
            if (cfgNextKey.Value != KeyCode.None && Input.GetKeyDown(cfgNextKey.Value)) NextLanguage();
            if (Input.GetMouseButtonDown(0)) HandleOptionsClick();
            if (Time.realtimeSinceStartup > nextMissingFlush) { nextMissingFlush = Time.realtimeSinceStartup + 10f; FlushMissing(); }
        }

        void OnApplicationQuit() { FlushMissing(); }

        void OnGUI()
        {
            if (toast == null || Time.realtimeSinceStartup > toastUntil) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = Mathf.Max(14, Screen.height / 40), alignment = TextAnchor.MiddleCenter };
            float w = Screen.width * 0.5f, h = style.fontSize * 2.2f;
            GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height * 0.08f, w, h), toast, style);
        }
    }

    // ------------------------------------------------------------ Harmony patches
    [HarmonyPatch(typeof(TextPrint), "printText")]
    static class PrintTextPatch
    {
        static void Prefix(TextPrint __instance, ref string __0)
        {
            try { if (Plugin.I != null) __0 = Plugin.I.OnPrintText(__instance, __0); }
            catch (Exception e) { Debug.LogWarning("[LFT] printText: " + e); }
        }
    }

    // The game has no bounds check: a text needing more rows than the box has would throw every frame.
    [HarmonyPatch(typeof(TextPrint), "Update")]
    static class TextPrintUpdatePatch
    {
        static bool Prefix(TextPrint __instance)
        {
            try
            {
                var rows = __instance.rows;
                if (rows == null || __instance.targetString == null) return true;
                if (__instance.currentRow >= rows.Length ||
                    __instance.currentMesh >= rows[__instance.currentRow].textMesh.Length)
                {
                    __instance.nextChar = __instance.targetString.Length;
                    return false;
                }
            }
            catch { }
            return true;
        }

        static Exception Finalizer(TextPrint __instance, Exception __exception)
        {
            if (__exception is IndexOutOfRangeException || __exception is ArgumentOutOfRangeException)
            {
                try { __instance.nextChar = __instance.targetString.Length; } catch { }
                return null;
            }
            return __exception;
        }
    }
}
