# View models

`MainViewModel` switches `Page` between `LoginViewModel`, `ConnectingViewModel` and `ChatViewModel`; `MainView` picks the view by data template.

`ChatViewModel` follows the engine's events on a background task: it reads `SyncProcessor.Changes` and posts each batch to the UI thread through `IUiDispatcher`, where room state is read under `SyncProcessor.Read`. `ConnectionStatus.Changed`, `EventsDecrypted` and `OutboxSender.RoomChanged` are posted the same way. Each posted change runs inside a try/catch that logs, as an exception on the UI thread would end the app.

`MessageList` turns events into `MessageViewModel` items. Edits, deletions and reactions change the item they refer to instead of adding one, also when older history arrives after them. Unsent outbox messages are shown last; their list is reloaded on every outbox change, and a version counter drops loads that finish out of order, so a sent message never comes back as pending.

Room creation, invites, deletions, typing notices, read markers and file uploads call `MatrixClient` directly; messages, replies, edits and reactions go through the outbox. Files to encrypted rooms are encrypted with `UploadEncryptedAsync` and their message is sent through the outbox, which encrypts it.

`SecurityViewModel` shows `GetSecurityStatusAsync` and offers set-up, recovery or verification by what is missing; it waits for `CryptoService.WhenStarted`, as encryption starts after sign-in. Setting up recovery asks for the password only when the server answers with a user-interactive auth challenge. `ChatViewModel` follows `CryptoService.VerificationChanged` and shows one `VerificationViewModel` at a time, so incoming requests and our own requests go through the same panel.

`ChatViewModel` also decides on notifications: new messages in direct chats, messages whose `m.mentions` name the user or the room, and invites that were not in the list before, at most three per sync and none from history or the first sync. They are passed to `GuiPlatform.Notifier` unless the window has the focus on that room. The desktop app sets `FreeDesktopNotifier` on Linux, which calls `org.freedesktop.Notifications.Notify` over the session bus with Tmds.DBus.Protocol, the D-Bus library Avalonia already uses.
