---
title: Remote administration
uid: Uniform.Chat.Server.RemoteAdmin.Article
---
# Remote administration

Besides the dashboard on the server's own machine, admins can manage the server from a client, over the same address the clients use. Only accounts with server admin rights may; give them in the dashboard's Users window, or create an account with them there. Admins can then give the rights to others remotely.

The Uniform Chat terminal client has these in its Server menu; see its guide. The server answers at the addresses of Synapse's admin API, with the same requests and answers, so tools made for Synapse can use what is listed below; they are not tested with it, and what is not listed is missing.

## What admins can do

| Area | What | Address |
|---|---|---|
| Accounts | List, search, show | `GET /_synapse/admin/v2/users`, `GET /_synapse/admin/v2/users/{userId}` |
| | Create, or change the password, name, admin rights, lock or deactivation | `PUT /_synapse/admin/v2/users/{userId}` |
| | Reset the password, logging out the devices | `POST /_synapse/admin/v1/reset_password/{userId}` |
| | Admin rights | `GET`/`PUT /_synapse/admin/v1/users/{userId}/admin` |
| | Deactivate: leave every room and never log in again | `POST /_synapse/admin/v1/deactivate/{userId}` |
| | Devices, and where they last connected from | `GET /_matrix/client/v3/admin/whois/{userId}`, also at `/_synapse/admin/v1/whois/{userId}` |
| | Suspend: may read, not send | `GET`/`PUT /_matrix/client/v1/admin/suspend/{userId}`, also `PUT /_synapse/admin/v1/suspend/{userId}` |
| | Lock: every request refused until unlocked | `GET`/`PUT /_matrix/client/v1/admin/lock/{userId}` |
| | Joined rooms, uploaded files | `GET /_synapse/admin/v1/users/{userId}/joined_rooms`, `…/media` |
| Registration tokens | List, show, create, change, delete | `/_synapse/admin/v1/registration_tokens`, `…/new`, `…/{token}` |
| Reports | List, show, delete | `/_synapse/admin/v1/event_reports`, `…/{id}` |
| Rooms | List, search, show, members | `GET /_synapse/admin/v1/rooms`, `…/{roomId}`, `…/{roomId}/members` |
| | Block, so local users cannot join | `GET`/`PUT /_synapse/admin/v1/rooms/{roomId}/block` |
| | Shut down | `DELETE /_synapse/admin/v1/rooms/{roomId}` |
| Files | Delete an uploaded file | `DELETE /_synapse/admin/v1/media/{serverName}/{mediaId}` |
| Notices | Send a server notice | `POST /_synapse/admin/v1/send_server_notice` |

Every change is written to the server's log, so it shows which admin did what.

Admins cannot suspend, lock, deactivate or take admin rights from their own account, so they cannot shut themselves out.

## Suspending and locking

The Matrix specification defines these two ways to stop an account without deleting it:

- **Suspended**: the user can still read, leave rooms, delete their own messages and log out. The server refuses sending, joining or creating rooms, inviting, changing the profile and uploading, with the error `M_USER_SUSPENDED`. Use it while you look into a report.
- **Locked**: the server refuses every request of the account, and logging in, with `M_USER_LOCKED`, except logging out. Clients keep their session, so once you unlock the account they continue where they were.

## Registration tokens

With `Server:Registration` set to `Token`, a new account needs a registration token. The tokens of `Server:RegistrationTokens` in the settings can be used any number of times and do not expire. Tokens that admins create remotely are kept in the database. Each can have a number of uses and an expiry time, and stops working when either runs out. A use is counted when a registration gets past the token step, also if the user does not finish registering.

## Shutting down a room

Shutting a room down makes every member from this server leave and forget it, takes it out of the public room directory and deletes its addresses on this server. Blocking it also keeps this server's users from joining or being invited again. Members from other servers stay in the room, and its events stay in the database.

## Server notices

A server notice is a message from the server to one of its users, e.g. about maintenance or changed terms. The first notice to a user creates a room named "Server notices" for them, tagged so that clients can show it apart, and only the server writes there. Later notices go to the same room while the user is in it. The notices are sent by the account `@notices:` followed by your server name, which the server creates without a password on the first notice. To use another name, set `Server:ServerNoticesLocalpart`.
