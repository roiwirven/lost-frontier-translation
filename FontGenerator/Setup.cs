// LostFrontierTranslation_Setup.exe
//   1. finds the Lost Frontier folder (Steam registry keys + Steam libraries), or asks for it
//   2. copies the "mod" folder next to this exe into the game folder (BepInEx, injector, translations)
//   3. generates the accented letters missing from the game font, inside the player's own
//      Lost Frontier_Data/sharedassets2.assets (the untouched file is kept as sharedassets2.assets.original;
//      running the setup again regenerates from that copy, so the result is always the same)
// Usage: LostFrontierTranslation_Setup.exe ["path\to\Lost Frontier"]
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LostFrontierTranslation.FontGenerator
{
    static class Setup
    {
        const string GameExe = "Lost Frontier.exe";

        static int Main(string[] args)
        {
            int rc;
            try { rc = Run(args); }
            catch (Exception e) { Console.WriteLine("ERROR: " + e.Message); rc = 1; }
            Console.WriteLine(); Console.WriteLine("Press a key to close.");
            try { Console.ReadKey(true); } catch { }
            return rc;
        }

        static int Run(string[] args)
        {
            Console.WriteLine("Lost Frontier - translation patch setup");
            Console.WriteLine("=======================================");
            string game = args.Length > 0 ? args[0].Trim('"', ' ') : FindGame();
            while (game == null || !File.Exists(Path.Combine(game, GameExe)))
            {
                if (game != null) Console.WriteLine("\"" + GameExe + "\" was not found in: " + game);
                Console.WriteLine("Enter the Lost Frontier folder (you can drag and drop it here), then press Enter:");
                string line = Console.ReadLine();
                if (line == null) return 2;
                game = line.Trim().Trim('"');
            }
            Console.WriteLine("Game folder: " + game);

            string mod = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mod");
            if (Directory.Exists(mod)) { int n = CopyTree(mod, game); Console.WriteLine("Mod files installed (" + n + " files)."); }
            else Console.WriteLine("(no 'mod' folder next to the setup: only the font is processed)");

            Console.WriteLine(PatchFont(Path.Combine(Path.Combine(game, "Lost Frontier_Data"), "sharedassets2.assets")));
            Console.WriteLine();
            Console.WriteLine("Done. Start the game; the language can be changed in Options > Language.");
            return 0;
        }

        // ------------------------------------------------------------------ locating the game
        static string FindGame()
        {
            var steamRoots = new List<string>();
            foreach (var v in new[] {
                ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                ReadReg(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
                if (v != null) steamRoots.Add(v.Replace('/', Path.DirectorySeparatorChar));

            var libraries = new List<string>(steamRoots);
            foreach (var root in steamRoots)
            {
                string vdf = Path.Combine(Path.Combine(root, "steamapps"), "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            foreach (var lib in libraries)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                // the app manifest tells the install folder name
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string txt = File.ReadAllText(acf);
                    if (!Regex.IsMatch(txt, "\"name\"\\s*\"Lost Frontier\"")) continue;
                    var dir = Regex.Match(txt, "\"installdir\"\\s*\"([^\"]+)\"");
                    if (dir.Success)
                    {
                        string p = Path.Combine(Path.Combine(apps, "common"), dir.Groups[1].Value);
                        if (File.Exists(Path.Combine(p, GameExe))) return p;
                    }
                }
                string guess = Path.Combine(Path.Combine(apps, "common"), "Lost Frontier");
                if (File.Exists(Path.Combine(guess, GameExe))) return guess;
            }
            return null;
        }

        static string ReadReg(RegistryKey hive, string key, string value)
        {
            try { using (var k = hive.OpenSubKey(key)) return k == null ? null : k.GetValue(value) as string; }
            catch { return null; }
        }

        static int CopyTree(string src, string dst)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); n++; }
            foreach (var d in Directory.GetDirectories(src)) n += CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
            return n;
        }

        // ------------------------------------------------------------------ font
        public static string PatchFont(string assetPath)
        {
            if (!File.Exists(assetPath)) return "Font: " + assetPath + " not found, skipped (accents will be stripped in game).";
            string original = assetPath + ".original";
            byte[] src = File.ReadAllBytes(assetPath);
            var assets = new UnityAssets(src);
            var fonts = assets.Fonts();
            if (!fonts.ContainsKey("Baar Zeitgeist") || !fonts.ContainsKey("CFOldMilwaukee-Regular"))
                return "Font: game fonts not found (different game version?), skipped.";
            var current = TrueTypeFont.Read(assets.FontData(fonts["CFOldMilwaukee-Regular"]));
            var e = current.GlyphFor('\u00E9');
            bool alreadyPatched = e != null && !e.IsEmpty;
            if (alreadyPatched)
            {
                // run again from the untouched copy, so the result is always the same
                if (!File.Exists(original)) return "Font: accented letters already present.";
                src = File.ReadAllBytes(original);
                assets = new UnityAssets(src); fonts = assets.Fonts();
            }
            else File.Copy(assetPath, original, true);     // fresh backup of the untouched file

            var donor = TrueTypeFont.Read(assets.FontData(fonts["Baar Zeitgeist"]));
            var cf = TrueTypeFont.Read(assets.FontData(fonts["CFOldMilwaukee-Regular"]));
            string added = AccentGlyphs.Build(cf, donor);
            if (added.Length == 0) return "Font: nothing to add.";
            byte[] ttf = cf.Write();
            foreach (var kv in fonts)
                if (kv.Key.StartsWith("CFOldMilwaukee")) assets.ReplaceFontData(kv.Value, ttf);
            byte[] result = assets.Save();

            File.WriteAllBytes(assetPath, result);
            return "Font: " + added.Length + " accented letters and symbols added to the game font.";
        }
    }
}
