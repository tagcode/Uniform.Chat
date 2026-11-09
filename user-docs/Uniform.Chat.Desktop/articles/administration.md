---
title: Server administration
uid: Uniform.Chat.Desktop.Administration.Article
---
# Server administration

If you are an admin of your homeserver, *⋮ → Server administration…* manages it from the app. The menu item shows only for admins. It works with servers that have Synapse's admin API, as Uniform Chat Server does.

- **Reports** lists what users reported, newest first, with the reason and, for unencrypted messages, the message. Delete a report once you have dealt with it, suspend the reported user, or shut the room down.
- **Accounts** finds accounts by part of their user ID or name. Choose one to see its devices and where they last connected from, and to:
  - **Suspend** it: the user can still read and leave rooms, but not send messages, join rooms or invite. Their app tells them.
  - **Lock** it: the server refuses their devices until you unlock it; they stay signed in.
  - **Make admin**, or take the rights away.
  - Set a **new password**, which logs out their devices.
  - Send a **server notice**, which arrives in their own *Server notices* room.
  - **Deactivate** it for good.
- **Registration tokens** lists the tokens new accounts register with. Make one with a number of uses or days, copy it and share it; delete one to stop it working.

Shutting a room down and deactivating an account cannot be undone, so the app asks first. You cannot suspend, lock, deactivate or take admin rights from your own account.

## Server notices

Messages from your server's admins arrive in a room named *Server notices*, listed first and marked *From your server*. You cannot reply there.
