---
title: Files
uid: Uniform.Chat.Server.Files.Article
---
# Files

Everything the server keeps is in the data folder, `~/.local/share/uniform-chat-server` by default (under `$XDG_DATA_HOME` when set):

| File | Contents |
|---|---|
| `server.db` | The SQLite database: accounts, devices, rooms and messages. The server updates it to its current layout at start. |
| `server.db-wal`, `server.db-shm` | The database's write-ahead log and its index, while the server runs; at a clean stop the log is written into `server.db`. Copy them with the database. |
| `media/` | Uploaded files, and the thumbnails made from them in `media/thumbnails/`. |
| `signing.key` | The server's private signing key, which signs every event it creates. It is created when first needed and readable only by your user. |
| `logs/` | One log file per session. |

To back up the server, stop it and copy the whole folder. Keep `signing.key` with the database and keep it private: it is the server's identity.
