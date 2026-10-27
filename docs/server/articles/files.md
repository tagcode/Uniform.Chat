# Files

Everything the server keeps is in the data folder, `~/.local/share/uniform-matrix-server-host` by default (under `$XDG_DATA_HOME` when set):

| File | Contents |
|---|---|
| `server.db` | The SQLite database: accounts, devices, rooms and messages. The server updates it to its current layout at start. |
| `media/` | Uploaded files, and the thumbnails made from them in `media/thumbnails/`. |
| `signing.key` | The server's private signing key, which signs every event it creates. It is created when first needed and readable only by your user. |
| `logs/` | One log file per session. |

To back up the server, stop it and copy the whole folder. Keep `signing.key` with the database and keep it private: it is the server's identity.
