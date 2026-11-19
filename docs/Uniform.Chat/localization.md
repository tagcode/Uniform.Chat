---
title: Localization
uid: Uniform.Matrix.Localization.Article
---
# Localization

The apps show their texts in 28 languages. Each project with texts people see keeps them in .resx files under its `Localization` folder: `Strings.resx` holds English, the neutral language, and a file per language beside it, such as `Strings.fi.resx`. The build generates a typed class `Strings` from the English file, with a property per text, and compiles each other language into a satellite assembly, such as `fi/Uniform.Matrix.Client.resources.dll`. A text missing from a language falls back through its parent cultures to English, so `pt-BR` finds `pt` and `zh-CN` finds `zh-Hans`.

| Project | Texts | Class |
| --- | --- | --- |
| Uniform.Matrix.Client | Event descriptions, room names, connection states, send failures | internal `Strings`, `Texts.Format`, `Texts.Plural` |
| Uniform.Matrix.Gui.Terminal | Settings and log windows, dialog buttons | internal `Strings`, `Texts.Format`, `Texts.Plural` |
| Uniform.Chat.Terminal | Menus, dialogs, statuses, the command help | internal `Strings`, `Texts.Format`, `Texts.Plural` and `Texts.Apply` |
| Uniform.Chat.Avalonia | Views, view models, notifications; also used by Uniform.Chat.Android | public `Strings`, [Translations](xref:Uniform.Chat.Avalonia.Localization.Translations) |

The project file generates the class:

```xml
<EmbeddedResource Update="Localization/Strings.resx" StronglyTypedLanguage="CSharp" StronglyTypedClassName="Strings" StronglyTypedNamespace="Uniform.Matrix.Client.Localization" StronglyTypedFileName="$(IntermediateOutputPath)Strings.Designer.cs" />
```

The namespace must be the project's root namespace followed by `.Localization`, as the resource's manifest name is made the same way and the class looks it up by its own name.

## Languages

[Languages.All](xref:Uniform.Matrix.Hosting.Localization.Languages.All) in Uniform.Matrix.Hosting lists the languages, by culture name and the name people know each by, in the order the language list shows them:

| Script | Languages |
| --- | --- |
| Latin | Indonesian `id`, Czech `cs`, Danish `da`, German `de`, English `en`, French `fr`, Hungarian `hu`, Dutch `nl`, Norwegian Bokmål `nb`, Polish `pl`, Portuguese `pt`, Romanian `ro`, Finnish `fi`, Swedish `sv`, Vietnamese `vi`, Turkish `tr` |
| Greek, Cyrillic | Greek `el`, Bulgarian `bg`, Russian `ru`, Ukrainian `uk` |
| Arabic, right to left | Urdu `ur`, Arabic `ar` |
| Indic | Marathi `mr`, Hindi `hi`, Bengali `bn`, Telugu `te` |
| East Asian | Chinese, Simplified `zh-Hans`; Japanese `ja` |

The translations other than English and Finnish are machine-written and not yet reviewed by native speakers; each file says so in its header comment.

Some cultures don't reach the file a language is written in by their parent chain: `nb` has the invariant culture as its parent, not `no`. [Languages.Copies](xref:Uniform.Matrix.Hosting.Localization.Languages.Copies) names the other culture names a translation is also saved under, and the tests check that each copy equals its source.

## Choosing the language

The texts follow [CultureInfo.CurrentUICulture](xref:System.Globalization.CultureInfo.CurrentUICulture), and numbers and dates [CultureInfo.CurrentCulture](xref:System.Globalization.CultureInfo.CurrentCulture). By default both are the system's.

- The Avalonia apps save the choice of *Settings → Language* as `Ui:Language` in their settings, empty for the system's. [Translations.Apply](xref:Uniform.Chat.Avalonia.Localization.Translations.Apply(System.String)) sets the cultures of every thread and tells the open views to read their texts again, so the switch needs no restart; [ChatViewModel](xref:Uniform.Chat.Avalonia.ViewModels.ChatViewModel) then describes the room list and the open room again.
- The terminal client reads `Ui:Language` at start, before it opens the window; a change takes effect at the next start.
- On Android 13 and later, `Resources/xml/locales_config.xml` lists the languages, so the system's per-app language setting offers them too.

## Writing texts in code

A text without values is the property of its key:

```csharp
ShowStatus(Strings.Connecting);
```

A text with values has numbered placeholders, filled in with the formats of the current culture:

```csharp
// Strings.resx: "Joined {0}"; Strings.fi.resx: "Liityttiin huoneeseen {0}"
ShowStatus(Texts.Format(Strings.JoinedRoom, room));
```

The Avalonia app uses [Translations.Format](xref:Uniform.Chat.Avalonia.Localization.Translations.Format(System.String,System.Object[])) the same way.

A few rules keep the texts translatable:

- Give the whole sentence one key with placeholders, rather than joining pieces, as word order differs between languages.
- A text with a count has a key per plural form; see [Plurals](#plurals).
- A word that is both a noun and a verb in English gets a key per meaning, such as `Chat` (a call's chat view) and `StartChat` (a button that starts one), as other languages need different words.
- Menu and button texts keep a hotkey: Terminal.Gui marks it with `_`, as in `_Liity`, and each menu needs different hotkeys in each language. Scripts without Latin letters add the English hotkey in brackets, as in `文件(_F)`.
- Slash commands, such as `/join`, setting names and log messages stay in English.

## Plurals

Languages differ in how many forms a counted noun takes: Japanese has one, English two, Russian four, Arabic six. A text with a count has a key per form its languages use, named after the [CLDR plural categories](https://cldr.unicode.org/index/cldr-spec/plural-rules): `_zero`, `_one`, `_two`, `_few`, `_many` and `_other`. English has `_one` and `_other`:

```xml
<data name="DeviceCount_one"><value>{0} device</value></data>
<data name="DeviceCount_other"><value>{0} devices</value></data>
```

Code names the `_other` key and gives the count; [PluralRules.Format](xref:Uniform.Matrix.Hosting.Localization.PluralRules.Format(System.Resources.ResourceManager,System.String,System.Int64,System.Object[])) picks the form of the current language, falls back to `_other`, and fills in the placeholders:

```csharp
string devices = Translations.Plural(nameof(Strings.DeviceCount_other), count, count);
```

The libraries' `Texts.Plural` does the same. [PluralRules.Forms](xref:Uniform.Matrix.Hosting.Localization.PluralRules.Forms(System.Globalization.CultureInfo)) returns the forms a language uses, and a translation has exactly those keys. The rules cover integer counts only, as every count in the apps counts whole things.

## Writing texts in views

Avalonia views take a text from [TrExtension](xref:Uniform.Chat.Avalonia.Localization.TrExtension) by its key. The binding follows a language switch:

```xml
<UserControl xmlns:l="using:Uniform.Chat.Avalonia.Localization">
  <Button Content="{l:Tr Send}" />
</UserControl>
```

A view model's text that depends on state, such as a status line, is a property that the view model raises again when the language changes.

## Right-to-left languages

[Languages.IsRightToLeft](xref:Uniform.Matrix.Hosting.Localization.Languages.IsRightToLeft(System.Globalization.CultureInfo)) tells Arabic and Urdu apart. In the Avalonia apps, [Translations.FlowDirection](xref:Uniform.Chat.Avalonia.Localization.Translations.FlowDirection) follows the language and the main view binds its `FlowDirection` to it, so the whole layout mirrors when the language changes.

Terminal.Gui has no bidirectional text, so the terminal client uses English for right-to-left languages, and the terminal projects have no files for them.

## Fonts

The Avalonia apps use the Inter font, which covers Latin, Greek and Cyrillic. [ScriptFonts.WithScriptFonts](xref:Uniform.Chat.Avalonia.Localization.ScriptFonts.WithScriptFonts(Avalonia.AppBuilder)) adds Noto Sans Arabic, Devanagari, Bengali and Telugu from `Assets/Fonts` as fallbacks, each for its own Unicode ranges, so those scripts show on systems without fonts for them. Chinese and Japanese fonts are too large to bundle and come from the system.

## Adding a language

1. Write each project's `Strings.<culture>.resx`, with a value for every key of `Strings.resx`: the same placeholders, the plural keys that [PluralRules.Forms](xref:Uniform.Matrix.Hosting.Localization.PluralRules.Forms(System.Globalization.CultureInfo)) returns for the language, and the hotkeys of menus and buttons. A right-to-left language needs only the client library and Avalonia files.
2. If the language has plural rules that `PluralRules` doesn't know yet, add them to `Forms` and `Category`.
3. Add the language to [Languages.All](xref:Uniform.Matrix.Hosting.Localization.Languages.All), with `RightToLeft` if it is, and to [Languages.Copies](xref:Uniform.Matrix.Hosting.Localization.Languages.Copies) if systems ask for it by another culture name that doesn't fall back to it.
4. Add the culture to `Resources/xml/locales_config.xml` of the Android app.
5. For a script that Inter and the bundled Noto fonts don't cover, consider adding its Noto font to `ScriptFonts`.

The tests take their cultures from `Languages`, so they check the new language without changes.

## Tests

The shared `LocalizationChecks` in `tests/Shared` is linked into each test project with localized texts:

- `TranslateEveryText`, per culture: every English text has a translation, not empty, with the same placeholders; each plural text has exactly the forms of the language; format strings and day formats are valid; button texts keep their hotkey in the terminal; copies equal their source.
- `UseDistinctHotkeys`, in Uniform.Chat.Terminal.Tests, per culture: the hotkeys of each menu and dialog differ.
- `PluralRuleTests`, in Uniform.Matrix.Hosting.Tests: each language's counts choose the right form, and every form a language lists is chosen by some count.
- `LocalizationTests.SwitchLanguage`, in Uniform.Chat.Avalonia.Tests: the sign-in page switches to Finnish and back while it is open, and Arabic turns the layout right to left.

The other tests compare English texts, so a module initializer in each test project sets the cultures to `en-US` before any test runs, whatever the machine's language.
