---
title: Localization
uid: Uniform.Matrix.Localization.Article
---
# Localization

The apps show their texts in English or Finnish. Each project with texts people see keeps them in .resx files under its `Localization` folder: `Strings.resx` holds English, the neutral language, and `Strings.fi.resx` beside it holds Finnish. The build generates a typed class `Strings` from the English file, with a property per text, and compiles each other language into a satellite assembly, such as `fi/Uniform.Matrix.Client.resources.dll`. A text missing from a language falls back to English.

| Project | Texts | Class |
| --- | --- | --- |
| Uniform.Matrix.Client | Event descriptions, room names, connection states, send failures | internal `Strings`, `Texts.Format` |
| Uniform.Matrix.Gui.Terminal | Settings and log windows, dialog buttons | internal `Strings`, `Texts.Format` |
| Uniform.Chat.Terminal | Menus, dialogs, statuses, the command help | internal `Strings`, `Texts.Format` and `Texts.Apply` |
| Uniform.Chat.Avalonia | Views, view models, notifications; also used by Uniform.Chat.Android | public `Strings`, [Translations](xref:Uniform.Chat.Avalonia.Localization.Translations) |

The project file generates the class:

```xml
<EmbeddedResource Update="Localization/Strings.resx" StronglyTypedLanguage="CSharp" StronglyTypedClassName="Strings" StronglyTypedNamespace="Uniform.Matrix.Client.Localization" StronglyTypedFileName="$(IntermediateOutputPath)Strings.Designer.cs" />
```

The namespace must be the project's root namespace followed by `.Localization`, as the resource's manifest name is made the same way and the class looks it up by its own name.

## Choosing the language

The texts follow [CultureInfo.CurrentUICulture](xref:System.Globalization.CultureInfo.CurrentUICulture), and numbers and dates [CultureInfo.CurrentCulture](xref:System.Globalization.CultureInfo.CurrentCulture). By default both are the system's.

- The Avalonia apps save the choice of *Settings → Language* as `Ui:Language` in their settings, empty for the system's. [Translations.Apply](xref:Uniform.Chat.Avalonia.Localization.Translations.Apply(System.String)) sets the cultures of every thread and tells the open views to read their texts again, so the switch needs no restart; [ChatViewModel](xref:Uniform.Chat.Avalonia.ViewModels.ChatViewModel) then describes the room list and the open room again.
- The terminal client reads `Ui:Language` at start, before it opens the window; a change takes effect at the next start.

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
- Where English puts a count before a noun, Finnish puts it after a partitive, e.g. `{0} devices` and `laitteita {0}`, which reads right for any count.
- A word that is both a noun and a verb in English gets a key per meaning, such as `Chat` (a call's chat view) and `StartChat` (a button that starts one), as Finnish needs different words.
- Menu and button texts keep a hotkey: Terminal.Gui marks it with `_`, as in `_Liity`, and each menu needs different hotkeys in each language.
- Slash commands, such as `/join`, setting names and log messages stay in English.

## Writing texts in views

Avalonia views take a text from [TrExtension](xref:Uniform.Chat.Avalonia.Localization.TrExtension) by its key. The binding follows a language switch:

```xml
<UserControl xmlns:l="using:Uniform.Chat.Avalonia.Localization">
  <Button Content="{l:Tr Send}" />
</UserControl>
```

A view model's text that depends on state, such as a status line, is a property that the view model raises again when the language changes.

## Adding a language

1. Copy each `Strings.fi.resx` to the new language's culture name, e.g. `Strings.sv.resx`, and translate its values; keep the keys and placeholders.
2. Add the culture name to [Translations.Languages](xref:Uniform.Chat.Avalonia.Localization.Translations.Languages), its name in that language to `Translations.NameOf`, and an item to the language list of `SettingsView.axaml`.
3. Add the culture to the `TranslateEveryText` tests.

## Tests

Each test project with localized texts has a `TranslateEveryText` test: every English text has a translation, not empty, with the same placeholders, and no translation lacks its English text. The terminal tests also check that every button text keeps its hotkey. `LocalizationTests.SwitchLanguage` in Uniform.Chat.Avalonia.Tests switches the sign-in page to Finnish and back while it is open.

The other tests compare English texts, so a module initializer in each test project sets the cultures to `en-US` before any test runs, whatever the machine's language.
