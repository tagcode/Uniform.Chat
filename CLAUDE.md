# Coding style

## C#
- Use file-scoped namespaces; place `namespace xx;` before `using xx;`
- Use type name `MyClass x = new();`, avoid `var x`.
- For classes that have lot of properties, implement as 'record' and use property initializers and no-args constructor.
- Use never-nesting. Use lot of continue; break; return; E.g. `if (!TryGetValue(out Type instance)) return false;`
  - Prefer organizing function so that  successful branch remains on the last line, and failing branches return or throw in earlier lines.
- Add comment right before top-level statement, but keep very short. 
  - E.g. `// Assert arguments`
  - You can explain *why* or *what* depending what is relevant. 
  - On last line of a function that returns a value, add comment  `// Done` or `// Return xx` or `// On <error description here>` even if it is obvious from code itself.
- Keep `<summary></summary>` and `<param name="serverName"></param>` comments in one line if line width is less than 250 characters.
- Give concise `<summary>` comments for every public and private class, public and private method, public property and public field.
- You can use `<see href="">` for urls to give more relevant information, such as specifications.
- xUnit v3 for tests, with method names like `DoX()` 
- For simple if-statements, use one-liners without linefeed after condition; E.g. `if (condition) do();`
- Refer other parts of the code with `<see cref="">`, `<paramref name="">`, `<typeparamref>`. Do not refer other parts of code with <!CDATA[]>.
- Use `<example><code><![CDATA[]]></code></example>` at an occasion to add code snippets, but if snippet is just reference to other parts of code, then use `<see>` and `<paramref>`.
- Partial classes: put the one `<summary>` and a single `<remarks>` listing the partial files in the main file. Describe the other partial files with a short `//` comment, because the compiler merges `///` comments of all parts and DocFX shows only the first `<remarks>`.
- In property and method comments, use imperative verbs, e.g. `Add an element`, not `Adds an element`.
- Write long and indicative parameter names, e.g. `CancellationToken cancellationToken`, not `CancellationToken ct`.

## General
- Small focused commits; don't commit unless I ask
