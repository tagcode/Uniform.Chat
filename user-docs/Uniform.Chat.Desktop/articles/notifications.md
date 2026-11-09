---
title: Notifications
uid: Uniform.Chat.Desktop.Notifications.Article
---
# Notifications

On Android and Linux, the app notifies you while it is in the background. *⋮ → Settings…* chooses what notifies, each on its own:

| Setting | Notifies of | Default |
|---|---|---|
| Incoming calls | A call: the phone rings and vibrates, also on the lock screen. Off, a call shows only in the open app. Android only. | On |
| Messages in direct chats | Each new message in a one-to-one chat | Off |
| Messages that mention you | A message that names you, in any room | Off |
| Invites | An invite to a room or a chat | On |

A newer message in the same chat replaces the previous notification. Windows does not show notifications yet.

On Android 13 and later, the app asks for permission to notify once you have signed in. Allow it: without it, an incoming call rings, but nothing on screen tells which app rings or who calls, and messages don't notify. If you refused, *⋮ → Settings…* says so under *Notifications*, and *Allow notifications…* opens the phone's settings for the app.

The room list shows how many messages, and calls, you have not read in each room, and the window title counts the rooms with unread ones, such as *Uniform Chat (2)*. Opening a room marks them read.

## In the background on Android

To hear of new messages and calls while the app is off screen, the Android app needs one of these, chosen in *⋮ → Settings… → In the background*:

- **Own background connection**, the default: the app keeps its own connection to your server, which a quiet notification, *Connected, for messages and calls*, shows. It works with any server and needs nothing else.
- **UnifiedPush**: a distributor app such as [ntfy](https://ntfy.sh) keeps one connection for all your apps that use it, and wakes this app when there is something new. The app then needs no connection of its own and no notification for it, and uses less battery. Install the distributor first, then choose it here; *Look for distributors again* finds one installed while the settings were open.

With UnifiedPush, your server sends the notice through the distributor's server, or the UnifiedPush project's public gateway when that server has no Matrix gateway of its own: only that there is a new event in a room, never what was written or by whom. The app wakes up and fetches the message from your server itself. A distributor's server also learns when you get messages, so prefer one you trust, or run your own ntfy.

If the distributor is removed or stops, the app goes back to its own connection the next time it is opened.

