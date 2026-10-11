using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;
using ProjectArrhythmia.Modding;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Metadata;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using VGFunctions;

public sealed class KoreanLocalizationMod : ProjectArrhythmiaModBehaviour, ITableProvider, ITablePostprocessor
{
    private sealed class TranslationData
    {
        public Dictionary<string, Dictionary<long, string>> tables { get; set; }
        public Dictionary<string, string> assets { get; set; }
        public Dictionary<string, string> legacy { get; set; }
        public Dictionary<string, string> ui { get; set; }
        public HashSet<string> uiPaths { get; set; }
    }

    private static KoreanLocalizationMod instance;
    private TranslationData data;
    private Harmony harmony;
    private ITableProvider previousProvider;
    private ITablePostprocessor previousPostprocessor;
    private readonly Dictionary<string, StringTable> tables = new Dictionary<string, StringTable>();
    private readonly Dictionary<string, TextAsset> assets = new Dictionary<string, TextAsset>();
    private readonly Dictionary<Component, bool> uiComponents = new Dictionary<Component, bool>();
    private readonly Dictionary<TMP_Text, string> originalText = new Dictionary<TMP_Text, string>();
    private TMP_FontAsset koreanFont;
    private TMP_FontAsset addedKoreanFallback;
    private TMP_FontAsset addedSymbolFallback;
    private string languagePath;
    private bool ready;

    private static bool IsKorean => instance != null && instance.ready &&
        LocalizationSettings.SelectedLocale != null && LocalizationSettings.SelectedLocale.Identifier.Code == "ko";

    protected override void OnInitialize()
    {
        languagePath = Path.Combine(Context.ModRootPath, "activated.txt");
        data = JsonConvert.DeserializeObject<TranslationData>(File.ReadAllText(Path.Combine(Context.ModRootPath, "translations.json")));
        if (data == null || data.tables == null || data.assets == null || data.legacy == null || data.ui == null || data.uiPaths == null)
            throw new InvalidDataException("Korean translation data is incomplete.");
        instance = this;
        harmony = new Harmony("afterbeat.korean.localization");
        harmony.PatchAll(typeof(KoreanLocalizationMod).Assembly);
        StartCoroutine(InitializeKorean());
    }

    private IEnumerator InitializeKorean()
    {
        yield return LocalizationSettings.InitializationOperation;
        while (DataManager.inst == null || DataManager.inst.interfaceSettings == null ||
               SingletonBase<SettingsManager>.Inst == null ||
               !(bool)AccessTools.Field(typeof(SettingsManager), "settingsLoaded").GetValue(SingletonBase<SettingsManager>.Inst))
            yield return null;

        var locales = LocalizationSettings.AvailableLocales;
        var english = locales.GetLocale("en");
        var korean = locales.GetLocale("ko");
        if (korean == null)
        {
            korean = Locale.CreateLocale("ko");
            korean.LocaleName = "한국어";
            korean.SortOrder = ushort.MaxValue;
            korean.Metadata.AddMetadata(new FallbackLocale { Locale = english });
        }

        foreach (var translation in data.tables)
        {
            var operation = LocalizationSettings.StringDatabase.GetTableAsync(translation.Key, english);
            yield return operation;
            if (operation.Status != AsyncOperationStatus.Succeeded || operation.Result == null)
                throw new InvalidDataException("Could not load source table: " + translation.Key);
            var table = Instantiate(operation.Result);
            table.name = translation.Key + "_ko";
            table.hideFlags = HideFlags.DontUnloadUnusedAsset;
            table.LocaleIdentifier = korean.Identifier;
            foreach (var entry in translation.Value)
            {
                var target = table.GetEntry(entry.Key);
                if (target == null)
                    throw new InvalidDataException("Unknown translation key: " + translation.Key + "/" + entry.Key);
                target.Value = entry.Value;
            }
            tables.Add(translation.Key, table);
        }

        previousProvider = LocalizationSettings.StringDatabase.TableProvider;
        previousPostprocessor = LocalizationSettings.StringDatabase.TablePostprocessor;
        LocalizationSettings.StringDatabase.TableProvider = this;
        LocalizationSettings.StringDatabase.TablePostprocessor = this;
        // Startup may have cached the game's empty Korean tables before this provider was ready.
        foreach (string tableName in tables.Keys)
            LocalizationSettings.StringDatabase.ReleaseTable(tableName, korean);
        locales.AddLocale(korean);
        if (!DataManager.inst.GetSettingEnumValuesAll("Language").Contains("ko"))
        {
            var option = new SimpleJSON.JSONObject();
            option["name"] = "한국어";
            option["values"] = "ko";
            DataManager.inst.interfaceSettings["Language"].Add(option);
        }
        // The game's video menu indexes both lists directly; keep their order identical.
        var languageCodes = DataManager.inst.GetSettingEnumValuesAll("Language");
        locales.Locales.Sort((left, right) => languageCodes.IndexOf(left.Identifier.Code).CompareTo(languageCodes.IndexOf(right.Identifier.Code)));

        var fontRequest = Resources.LoadAsync<TMP_FontAsset>("LocaleFonts/kor_NotoSansKR SDF");
        yield return fontRequest;
        if (fontRequest.asset == null)
            throw new InvalidDataException("The game's Korean font is missing.");

        // The Korean atlas has complete syllables but lacks standalone consonants such as ㅋ and ㅎ.
        koreanFont = (TMP_FontAsset)fontRequest.asset;
        var sourceFont = Resources.Load<TMP_FontAsset>("Fonts & Materials/PoorStory");
        if (sourceFont == null || sourceFont.sourceFontFile == null)
            throw new InvalidDataException("The game's supplemental Korean font is missing.");
        addedKoreanFallback = TMP_FontAsset.CreateFontAsset(sourceFont.sourceFontFile);
        addedKoreanFallback.name = "Korean Jamo Fallback";
        addedKoreanFallback.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (koreanFont.fallbackFontAssetTable == null)
            koreanFont.fallbackFontAssetTable = new List<TMP_FontAsset>();
        koreanFont.fallbackFontAssetTable.Add(addedKoreanFallback);

        addedSymbolFallback = CreateSymbolFallback(sourceFont.sourceFontFile);
        if (addedSymbolFallback != null)
            koreanFont.fallbackFontAssetTable.Add(addedSymbolFallback);

        ready = true;
        LocalizationSettings.SelectedLocaleChanged += OnLocaleChanged;
        var settings = SingletonBase<SettingsManager>.Inst;
        // The game can normalize an unknown saved language before mods finish loading.
        string language = File.Exists(languagePath) ? File.ReadAllText(languagePath).Trim() : "ko";
        settings.GetSettings.Video.General.Language = languageCodes.Contains(language) ? language : "ko";
        settings.ApplyLanguageSettings();
        OnLocaleChanged(LocalizationSettings.SelectedLocale);
        settings.UpdateSettingsFile();
        Context.Log("Ready: " + data.tables.Values.Sum(table => table.Count) + " table entries, " +
                    data.assets.Count + " story/document assets, " + data.ui.Count + " UI translations.");
    }

    private TMP_FontAsset CreateSymbolFallback(Font templateFont)
    {
        // Unity's built-in Arial omits font data. Read this one symbol from Windows instead.
        TMP_FontAsset font = null;
        try
        {
            string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            if (!File.Exists(fontPath))
                throw new FileNotFoundException("Windows Arial font is missing.", fontPath);
            font = TMP_FontAsset.CreateFontAsset(templateFont, 90, 9, GlyphRenderMode.SDFAA,
                256, 256, AtlasPopulationMode.Static, false);
            if (FontEngine.LoadFontFace(fontPath, 90) != FontEngineError.Success ||
                !FontEngine.TryGetGlyphIndex('∑', out uint glyphIndex))
                throw new InvalidDataException("The summation glyph could not be loaded.");
            font.faceInfo = FontEngine.GetFaceInfo();
            var atlas = font.atlasTextures[0];
            atlas.Resize(256, 256);
            atlas.LoadRawTextureData(new byte[256 * 256]);
            atlas.Apply(false, false);

            // This Unity version exposes glyph loading publicly, but atlas rendering is internal.
            var renderGlyph = typeof(FontEngine).GetMethod("TryAddGlyphToTexture", BindingFlags.Static | BindingFlags.NonPublic);
            object[] arguments = { glyphIndex, 9, GlyphPackingMode.BestShortSideFit,
                new List<GlyphRect> { new GlyphRect(0, 0, 255, 255) }, new List<GlyphRect>(),
                GlyphRenderMode.SDFAA, atlas, null };
            if (renderGlyph == null || !(bool)renderGlyph.Invoke(null, arguments))
                throw new InvalidDataException("The summation glyph could not be rendered.");
            var glyph = (Glyph)arguments[7];
            font.glyphTable.Add(glyph);
            font.characterTable.Add(new TMP_Character('∑', font, glyph));
            font.ReadFontAssetDefinition();
            font.name = "Korean Glitch Symbol Fallback";
            font.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return font;
        }
        catch (Exception error)
        {
            Context.Log("Symbol fallback unavailable: " + error.Message);
            if (font != null)
            {
                foreach (var texture in font.atlasTextures)
                    Destroy(texture);
                Destroy(font.material);
                Destroy(font);
            }
            return null;
        }
    }

    public AsyncOperationHandle<TTable> ProvideTableAsync<TTable>(string tableCollectionName, Locale locale)
        where TTable : LocalizationTable
    {
        StringTable table;
        if (locale.Identifier.Code == "ko" && tables.TryGetValue(tableCollectionName, out table))
            return Addressables.ResourceManager.CreateCompletedOperation(table as TTable, null);
        return previousProvider != null ? previousProvider.ProvideTableAsync<TTable>(tableCollectionName, locale) : default;
    }

    public void PostprocessTable(LocalizationTable table)
    {
        previousPostprocessor?.PostprocessTable(table);
        // Unity's locale preloader can load bundled tables without calling the custom provider.
        if (table is StringTable strings && table.LocaleIdentifier.Code == "ko" &&
            tables.TryGetValue(table.TableCollectionName, out var translated) && strings != translated)
        {
            foreach (var entry in translated)
                strings.AddEntry(entry.Key, entry.Value.Value).IsSmart = entry.Value.IsSmart;
        }
    }

    private void OnLocaleChanged(Locale locale)
    {
        if (locale != null)
            File.WriteAllText(languagePath, locale.Identifier.Code);
        uiComponents.Clear();
        if (SingletonBase<UIStateManager>.Inst != null)
            SingletonBase<UIStateManager>.Inst.RefreshTextCacheAll();
        if (!IsKorean)
        {
            foreach (var item in originalText)
                if (item.Key != null && item.Key.text == TranslateDisplayText(item.Key, item.Value))
                    item.Key.text = item.Value;
            originalText.Clear();
            return;
        }
        foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            if (text.gameObject.scene.IsValid())
                text.text = TranslateUi(text, text.text);
    }

    private string TranslateUi(Component component, string value)
    {
        if (component == null || string.IsNullOrEmpty(value))
            return value;
        string translated = TranslateDisplayText(component, value);
        if (translated == value)
            return value;
        if (!uiComponents.TryGetValue(component, out var allowed))
        {
            allowed = component.GetComponentInParent<TMP_InputField>(true) == null && component.GetComponentInParent<InputField>(true) == null;
            var parts = new List<string>();
            for (Transform node = component.transform; node != null; node = node.parent)
                parts.Add(node.name.Replace("(Clone)", ""));
            parts.Reverse();
            string path = string.Join("/", parts);
            allowed = allowed && data.uiPaths.Any(known => path == known || path.EndsWith("/" + known, StringComparison.Ordinal));
            uiComponents[component] = allowed;
        }
        if (!allowed)
            return value;
        if (component is TMP_Text tmp)
            originalText[tmp] = value;
        return translated;
    }

    private string TranslateDisplayText(Component component, string value)
    {
        if (data.ui.TryGetValue(value, out var translated))
            return translated;
        // Match only authored UI envelopes; names, search terms and timestamps stay untouched.
        if (component.GetComponentInParent<Tooltip>(true) != null)
        {
            const string themePrefix = "Select ";
            const string themeSuffix = " Theme";
            const string descriptionSuffix = " ]\nSelect this theme for the theme keyframe";
            if (value.Length >= themePrefix.Length + themeSuffix.Length &&
                value.StartsWith(themePrefix, StringComparison.Ordinal) && value.EndsWith(themeSuffix, StringComparison.Ordinal))
                return data.ui[themePrefix] + value.Substring(themePrefix.Length, value.Length - themePrefix.Length - themeSuffix.Length) + data.ui[themeSuffix];
            if (value.EndsWith(descriptionSuffix, StringComparison.Ordinal))
                return value.Substring(0, value.Length - descriptionSuffix.Length) + data.ui[descriptionSuffix];
        }
        if (component.GetComponentInParent<QuickActionCellView>(true) != null)
        {
            int lineBreak = value.IndexOf('\n');
            string title = lineBreak < 0 ? value : value.Substring(0, lineBreak);
            string details = lineBreak < 0 ? string.Empty : value.Substring(lineBreak);
            if (data.ui.TryGetValue(title, out translated))
                return translated + details;
            const string containsPrefix = "Select objects containing \"";
            if (title.StartsWith(containsPrefix, StringComparison.Ordinal) && title.EndsWith("\"", StringComparison.Ordinal))
                return data.ui[containsPrefix] + title.Substring(containsPrefix.Length) + details;
            if (title.Length >= 17 && title.StartsWith("Select \"", StringComparison.Ordinal) && title.EndsWith("\" objects", StringComparison.Ordinal))
                return data.ui["Select \""] + title.Substring(8, title.Length - 8 - 9) + data.ui["\" objects"] + details;
            foreach (string suffix in new[] { " objects", " shapes" })
                if (title.Length >= 7 + suffix.Length && title.StartsWith("Select ", StringComparison.Ordinal) && title.EndsWith(suffix, StringComparison.Ordinal))
                    return data.ui["Select "] + title.Substring(7, title.Length - 7 - suffix.Length) + data.ui[suffix] + details;
            if (title.StartsWith("Jump to ", StringComparison.Ordinal))
                return data.ui["Jump to "] + title.Substring(8) + details;
        }
        return value;
    }

    private TextAsset TranslateAsset(TextAsset original)
    {
        if (original == null || !data.assets.TryGetValue(original.name, out var translated))
            return original;
        if (!assets.TryGetValue(original.name, out var asset))
        {
            asset = new TextAsset(translated) { name = original.name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            assets.Add(original.name, asset);
        }
        return asset;
    }

    private static string TranslateLiteral(string source)
    {
        return IsKorean && instance.data.ui.TryGetValue(source, out var translated) ? translated : source;
    }

    private static string FormatStopMotionSubtitle(string format, object[] arguments)
    {
        string translated = TranslateLiteral(format);
        if (translated != format)
            arguments[1] = string.Empty; // Korean frame counts do not use the English plural suffix.
        return string.Format(translated, arguments);
    }

    protected override void OnShutdown()
    {
        LocalizationSettings.SelectedLocaleChanged -= OnLocaleChanged;
        foreach (var fallback in new[] { addedKoreanFallback, addedSymbolFallback })
        {
            if (fallback == null)
                continue;
            if (koreanFont != null)
                koreanFont.fallbackFontAssetTable.Remove(fallback);
            foreach (var texture in fallback.atlasTextures)
                Destroy(texture);
            Destroy(fallback.material);
            Destroy(fallback);
        }
        if (ReferenceEquals(LocalizationSettings.StringDatabase.TableProvider, this))
            LocalizationSettings.StringDatabase.TableProvider = previousProvider;
        if (ReferenceEquals(LocalizationSettings.StringDatabase.TablePostprocessor, this))
            LocalizationSettings.StringDatabase.TablePostprocessor = previousPostprocessor;
        harmony?.UnpatchAll("afterbeat.korean.localization");
        ready = false;
        if (instance == this)
            instance = null;
    }

    [HarmonyPatch(typeof(VNLogScriptable), nameof(VNLogScriptable.GetLogWithLanguage))]
    private static class DialoguePatch
    {
        private static bool Prefix(VNLogScriptable __instance, ref TextAsset __result)
        {
            if (!IsKorean)
                return true;
            var english = __instance.VGLogs.Find(entry => entry.Lang == VNLogScriptable.VNLogEntry.Language.en).Asset;
            if (english == null)
                return true;
            __result = instance.TranslateAsset(english);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChatLogCollection.LogEntry), nameof(ChatLogCollection.LogEntry.GetMarkdownWithLanguage))]
    private static class ChatLogPatch
    {
        private static void Postfix(ref TextAsset __result)
        {
            if (IsKorean)
                __result = instance.TranslateAsset(__result);
        }
    }

    [HarmonyPatch(typeof(NewsArticleCollection.Entry), nameof(NewsArticleCollection.Entry.GetMarkdownWithLanguage))]
    private static class NewsPatch
    {
        private static void Postfix(ref TextAsset __result)
        {
            if (IsKorean)
                __result = instance.TranslateAsset(__result);
        }
    }

    [HarmonyPatch(typeof(LocalisationSystem), nameof(LocalisationSystem.GetLocalisedValue))]
    private static class LegacyPatch
    {
        private static bool Prefix(string _key, string _lang, ref string __result)
        {
            if (instance == null || !instance.ready || (_lang != "ko" && (!string.IsNullOrEmpty(_lang) || !IsKorean)))
                return true;
            if (instance.data.legacy.TryGetValue(_key, out var translation))
                __result = translation.Replace("[nl]", Environment.NewLine);
            else if (instance.data.ui.TryGetValue(_key, out translation))
                __result = translation;
            else if (_key.StartsWith("$", StringComparison.Ordinal))
                __result = GeneralLoc.GetForLocale(_key.Substring(1), _key.Substring(1), "ko");
            else
                __result = _key;
            return false;
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "text", MethodType.Setter)]
    private static class TmpTextPatch
    {
        private static void Prefix(TMP_Text __instance, ref string value)
        {
            if (IsKorean && __instance is TextMeshProUGUI)
                value = instance.TranslateUi(__instance, value);
        }
    }

    [HarmonyPatch(typeof(UIStateManager), nameof(UIStateManager.RefreshTextCache))]
    private static class TextCachePatch
    {
        private static void Prefix(TextMeshProUGUI _textRef, ref string _value)
        {
            if (IsKorean)
                _value = instance.TranslateUi(_textRef, _value);
        }
    }

    [HarmonyPatch(typeof(LSText.ASCII), nameof(LSText.ASCII.Get2HighASCII))]
    private static class AsciiTitlePatch
    {
        private static bool Prefix(string _text, ref string __result)
        {
            if (IsKorean && _text != null && _text.Any(character => character >= '\uac00' && character <= '\ud7a3'))
            {
                __result = _text;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DebugController), "OnGUI")]
    private static class CommandSearchPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Ldstr && (string)instruction.operand == "No command matches [")
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(KoreanLocalizationMod), nameof(TranslateLiteral)));
            }
        }
    }

    [HarmonyPatch]
    private static class StopMotionSubtitlePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("StopMotionQuickActionModule"), "AddExecutionSuggestion");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object[]) });
            var replacement = AccessTools.Method(typeof(KoreanLocalizationMod), nameof(FormatStopMotionSubtitle));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original))
                    instruction.operand = replacement;
                yield return instruction;
            }
        }
    }
}
