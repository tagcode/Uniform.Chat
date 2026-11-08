---
title: Server administration
uid: Uniform.Chat.Terminal.ServerAdmin.Article
---
# Server administration

If you are an admin of your homeserver, the Server menu manages the server remotely: its accounts, registration tokens, reports and rooms. The client uses the admin API that Synapse defined, which Uniform Chat Server also has. For others, the server refuses with "You are not a server admin."

- **Accounts**, or `/accounts [text]`, lists the accounts whose user ID or name contains the text, with their devices, rooms and flags: admin, suspended, locked, deactivated. **+ New account** creates one with a password. Choose an account to:
  - see its devices and the addresses they last connected from (**Who is**, also `/whois @user`);
  - send it a server notice;
  - reset its password, which logs out its devices;
  - suspend it, or end the suspension;
  - lock it, or unlock it;
  - make it a server admin, or take the rights away;
  - deactivate it for good.
- **Registration tokens**, or `/tokens`, lists the tokens new accounts register with, with how often each was used and when it expires. Choose **+ New token** to create one: give its text or leave it empty for a random one, then how many accounts may use it and for how many days. Choose a token to delete it.
- **Reports**, or `/reports`, lists what users reported, newest first. Choose a report to delete it, suspend the reported user, or shut the room down.
- **Rooms**, or `/rooms [text]`, lists the server's rooms, most members first. Choose a room to see its members or shut it down.
- **Server notice**, or `/notice @user text`, sends a message from the server. It arrives in the user's own Server notices room, where only the server writes.
- **Who is** works for anyone on their own account.

You cannot suspend, lock, deactivate or take admin rights from your own account, so you cannot shut yourself out.

## Suspend and lock

- **Suspended:** the user can still read, leave rooms and log out, but cannot send messages, join or create rooms, invite, change their profile or upload files. Use it while you look into a report.
- **Locked:** the server refuses every request of the account until you unlock it. Its devices stay signed in and continue once it is unlocked; this client shows "Retrying: The account is locked by an admin." meanwhile.

`/suspend @user` and `/unsuspend @user`, `/lock @user` and `/unlock @user` do the same from the message line.

## Shutting down a room

Shutting a room down makes every member from your server leave and forget it, and deletes its addresses on your server; the reason you give is shown to them. **Shut down and block** also keeps your server's users from joining it again. Members from other servers stay in the room, and its messages stay on your server.
