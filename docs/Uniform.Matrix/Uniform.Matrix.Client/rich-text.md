# Rich text

`MarkdownComposer` turns typed Markdown into `formatted_body` with Markdig (CommonMark, strikethrough, autolinks, raw HTML disabled), unwraps a single paragraph like Element, and turns completed nicks into `matrix.to` mention pills; text without formatting or mentions is sent without HTML. `HtmlText` renders incoming HTML with AngleSharp into terminal text: blocks, lists, quotes, code, links with addresses, mention names, hidden spoilers, and no `mx-reply` fallback.
