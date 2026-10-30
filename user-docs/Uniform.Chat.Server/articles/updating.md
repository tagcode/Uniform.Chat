---
title: Updating
uid: Uniform.Chat.Server.Updating.Article
---
# Updating

The server updates its database to the new version's layout when it starts. Back up the data folder before updating: it holds the database and the signing key, which other servers know your server by.

## As a system service

Stop the server, back up its data, unpack the new release over the program, and start it:

```bash
sudo systemctl stop uniform-chat-server
sudo tar -czf ~/uniform-chat-server-backup.tar.gz -C /var/lib uniform-chat-server
sudo tar -xzf uniform-chat-server-<version>-linux-x64.tar.gz -C /opt/uniform-chat-server --strip-components=1
sudo systemctl start uniform-chat-server
```

## Run by hand or as a user service

Stop the server, copy `~/.local/share/uniform-chat-server` somewhere safe, unpack the new release over the program's folder, and start the server again.

## From a version before the name Uniform Chat

Before Uniform Chat, the program was `Uniform.Matrix.Server.Host` and its folders were named `uniform-matrix-server-host`. The new version does not look in the old folders: started as it is, it would begin with an empty database and a new signing key. Move the folders first.

### Run by hand or as a user service

Stop the server, then rename its settings and data folders:

```bash
mv ~/.config/uniform-matrix-server-host ~/.config/uniform-chat-server
mv ~/.local/share/uniform-matrix-server-host ~/.local/share/uniform-chat-server
```

Unpack the new release, and start `uniform-chat-server` where you started `Uniform.Matrix.Server.Host`, e.g. in the `ExecStart` of a user unit. Settings that name files in the old folders, such as `Tls:CertificatePath`, need the new folder too.

### As a system service

A system service set up with the earlier guide is named `uniform-matrix`, runs as the user `uniform-matrix`, and keeps its program in `/opt/uniform-matrix`, its settings in `/etc/uniform-matrix/uniform-matrix-server-host` and its data in `/var/lib/uniform-matrix/uniform-matrix-server-host`. To move it to the names of [Run without the dashboard](xref:Uniform.Chat.Server.Service.Article):

1. Stop and disable the old service, and back up its data:

   ```bash
   sudo systemctl disable --now uniform-matrix
   sudo tar -czf ~/uniform-matrix-backup.tar.gz -C /var/lib uniform-matrix -C /etc uniform-matrix
   ```

2. Rename the user and its group, which keeps the owner of the files:

   ```bash
   sudo usermod -l uniform-chat-server -d /var/lib/uniform-chat-server uniform-matrix
   sudo groupmod -n uniform-chat-server uniform-matrix
   ```

3. Move the settings and the data; TLS files too, if the server reads them itself:

   ```bash
   sudo mv /etc/uniform-matrix/uniform-matrix-server-host /etc/uniform-chat-server
   sudo mv /var/lib/uniform-matrix/uniform-matrix-server-host /var/lib/uniform-chat-server
   sudo mv /etc/uniform-matrix/tls /etc/uniform-chat-server/tls
   ```

   Then change paths in `/etc/uniform-chat-server/settings.json` that point into the old folders, such as `Tls:CertificatePath` and `Tls:KeyPath`, and the certbot deploy hook, if there is one.

4. Unpack the new release in `/opt/uniform-chat-server`:

   ```bash
   sudo mkdir -p /opt/uniform-chat-server
   sudo tar -xzf uniform-chat-server-<version>-linux-x64.tar.gz -C /opt/uniform-chat-server --strip-components=1
   ```

5. Replace the unit file: remove `/etc/systemd/system/uniform-matrix.service`, write `/etc/systemd/system/uniform-chat-server.service` as in [Run without the dashboard](xref:Uniform.Chat.Server.Service.Article), and start it:

   ```bash
   sudo rm /etc/systemd/system/uniform-matrix.service
   sudo systemctl daemon-reload
   sudo systemctl enable --now uniform-chat-server
   sudo systemctl status uniform-chat-server
   ```

6. Once it runs with your accounts and rooms, remove what is left of the old layout:

   ```bash
   sudo rm -r /opt/uniform-matrix /etc/uniform-matrix /var/lib/uniform-matrix
   ```

The nginx site needs no change, as long as the server listens at the same address.
