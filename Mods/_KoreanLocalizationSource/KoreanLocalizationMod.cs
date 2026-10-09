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
                if (item.Key != null && data.ui.TryGetValue(item.Value, out var translated) && item.Key.text == translated)
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
        if (component == null || string.IsNullOrEmpty(value) || !data.ui.TryGetValue(value, out var translated))
            return value;
        if (!uiComponents.TryGetValue(component, out var allowed))
        {
            allowed = component.GetComponentInParent<TMP_InputField>() == null && component.GetComponentInParent<InputField>() == null;
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
        if (koreanFont != null && addedKoreanFallback != null)
        {
            koreanFont.fallbackFontAssetTable.Remove(addedKoreanFallback);
            foreach (var texture in addedKoreanFallback.atlasTextures)
                Destroy(texture);
            Destroy(addedKoreanFallback.material);
            Destroy(addedKoreanFallback);
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
