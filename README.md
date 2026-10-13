# Lost Frontier – translation patch

Unofficial translation of **Lost Frontier** (Mika Mobile, Steam) into French, Spanish, German,
Italian and Portuguese: story dialogue, tutorials, Challenge Mode, units, legends, perks and the whole interface.

- The language is chosen in the game's **Options** menu. By default it follows the Windows language.
- Translated labels are scaled down automatically so they never overflow the interface.
- The accented letters missing from the game font are generated on the player's own copy of the game.
  No game file or font is distributed.

## Installing

1. Download the release zip and unzip it anywhere.
2. Run `LostFrontierTranslation_Setup.exe`.
   - It finds the game through Steam (registry and Steam libraries).
   - If it can't, it asks for the game folder. You can drag and drop the folder into the window.
3. Start the game.

To update, run the new setup again.

## What is where

```
README.md
FontGenerator/     the setup exe: installs the mod and adds the missing accented letters to the game font
Injector/          the BepInEx plugin: replaces the game texts at runtime
translations/      the texts, one file per kind of game data
```

### FontGenerator/ → `LostFrontierTranslation_Setup.exe`

| File | Role |
|---|---|
| `Setup.cs` | finds the game, copies the `mod` folder next to the exe into it, then patches the font |
| `UnityAssets.cs` | reads and rewrites `Lost Frontier_Data/sharedassets2.assets` (only the two font objects change) |
| `TrueType.cs` | reads and writes TrueType fonts |
| `AccentGlyphs.cs` | builds é è à ç ñ ü ß ¿ ¡ « » … for the game font "CF Old Milwaukee" |

The accented letters are built from the game's own fonts:
- the base letters and the diaeresis dots come from CF Old Milwaukee;
- the other accents come from the game's second font, "Baar Zeitgeist".

The untouched file is kept as `sharedassets2.assets.original`. Running the setup again always starts
from that copy, so the result is the same every time.

### Injector/ → `BepInEx/plugins/LostFrontierTranslation/LostFrontierTranslation.dll`

| File | Role |
|---|---|
| `TranslationCore.cs` | loads `translations/`, looks texts up, strips accents the font can't draw (no Unity code) |
| `LostFrontierTranslation.cs` | hooks into the game: dialogues, labels, the Options row, auto-fit |

- **Dialogues and descriptions** go through the game's `TextPrint.printText(string)`. A Harmony prefix
  swaps the English text before it is laid out. Long translations are slightly scaled down to fit the 3-row boxes.
- **Labels** (buttons, unit names, objectives) are `TextMesh` components. Each one is checked just
  before rendering, and translated labels wider than the English text are scaled down.
- **The Language option** is a clone of the game's "Screen Mode" row in the Options menu.

### translations/

Every file has the same format:
- UTF-8, tab-separated;
- header `en fr es de it pt`;
- one row per text, with the exact English text of the game followed by its translations.

The injector treats every file the same way, except two:

| File | Content |
|---|---|
| `interface.tsv` | menus, buttons, objectives, messages |
| `missions.tsv` | mission and challenge map descriptions |
| `dialogues.tsv` | story and tutorial dialogue |
| `units.tsv` | unit names and descriptions |
| `legends.tsv` | legend (hero) names and descriptions |
| `perks.tsv` | perk and ability descriptions |
| `dynamic.tsv` | **special:** texts built by the game at runtime (`Chapter 3`, `HP: 97/100`…). `en` is a .NET regular expression; translations use `{1}` for a captured value, or `{T1}` for a captured value that is translated too |
| `languages.tsv` | **special:** one row, the name of each language in the Options menu |

Inside cells:
- `\n` is a new line;
- `@b` starts highlighted text, `@r` starts red text, `@n` goes back to normal. Keep these codes.

An empty cell means "not translated", and the game shows the English text. In `interface.tsv` and `units.tsv`,
translations should not be longer than the English.

**Fixing a translation:**
1. Edit the cell.
2. Copy the file into `BepInEx/plugins/LostFrontierTranslation/translations/`.
3. Restart the game.

Texts the injector does not know are written to `BepInEx/LFT_untranslated_<lang>.tsv` in the same format.

**Adding a language:**
1. Add a column with its code (e.g. `pl`) to every file, and its name to `languages.tsv`.
2. To make it the default for that Windows language, add the language to `ResolveCode()` in
   `LostFrontierTranslation.cs`.
3. Letters the font lacks must be added in `AccentGlyphs.cs`. A non-Latin script would need another font.

## Building

Requirements: Mono (`mcs`), and a copy of the game with BepInEx 5.4.23 (x86) installed.

```bash
GAME="/path/to/Lost Frontier"; M="$GAME/Lost Frontier_Data/Managed"; B="$GAME/BepInEx/core"

# injector
mcs -target:library -nostdlib -noconfig -langversion:7 -optimize+ -out:LostFrontierTranslation.dll \
  -r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$M/UnityEngine.dll" \
  -r:"$M/UnityEngine.CoreModule.dll" -r:"$M/UnityEngine.TextRenderingModule.dll" \
  -r:"$M/UnityEngine.IMGUIModule.dll" -r:"$M/UnityEngine.PhysicsModule.dll" \
  -r:"$M/Assembly-UnityScript.dll" -r:"$B/BepInEx.dll" -r:"$B/0Harmony.dll" Injector/*.cs

# setup
mcs -sdk:4.5 -target:exe -optimize+ -out:LostFrontierTranslation_Setup.exe FontGenerator/*.cs
```

The release zip contains `LostFrontierTranslation_Setup.exe` and a `mod/` folder that is copied as is
into the game folder:
- BepInEx 5.4.23 (`BepInEx/core`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`);
- `BepInEx/plugins/LostFrontierTranslation/` with the DLL and `translations/`.

## Legal

- Lost Frontier and its English texts are © Mika Mobile. This is an unofficial fan project, not
  affiliated with Mika Mobile. The English texts are included so that translators can work line by line.
  They will be removed if the rights holder asks.
- No game file, DLL or font is stored here or in the release. The font is modified on the player's own copy.
- BepInEx (LGPL-2.1) and HarmonyX (MIT) are bundled unmodified in the release zip.
- Code in this repository: MIT licence.
