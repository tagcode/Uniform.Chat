---
title: Accounts
uid: Uniform.Matrix.Server.Host.Accounts.Article
---
# Accounts

The server starts with registration disabled, so nobody can create an account by themselves. The simplest way to add people is the dashboard: Server → Users → New (see [Users window](xref:Uniform.Matrix.Server.Host.Dashboard.Article#users-window)).

To let people register in their own account:

1. Start the server with registration open, or with tokens:

   ```bash
   ./Uniform.Matrix.Server.Host --Server:Registration=Open
   ./Uniform.Matrix.Server.Host --Server:Registration=Token --Server:RegistrationTokens=let-me-in
   ```

   Or change `Server:Registration` in the settings window and Apply, as above.
2. Register in a client that lets you type the homeserver address, such as Element Desktop: choose "Create account", edit the homeserver, and enter `http://localhost:8008`. With `Token`, the client asks for the token.
3. Close registration again when you are done.

Registration also works from the command line:

```bash
curl -X POST http://localhost:8008/_matrix/client/v3/register \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"choose-a-password","auth":{"type":"m.login.dummy"}}'
```

For `Token` registration, use `"auth":{"type":"m.login.registration_token","token":"let-me-in"}`.

The new user is `@alice:localhost`, or `@alice:` followed by the configured server name. Their display name starts out as their user name.

# Confogure from dashboard

When the server runs as a [system service](xref:Uniform.Matrix.Server.Host.Service.Article), stop the service and open the dashboard as the service's user with the same folders, in a terminal:

```bash
sudo systemctl stop uniform-matrix
sudo -u uniform-matrix env XDG_CONFIG_HOME=/etc/uniform-matrix XDG_DATA_HOME=/var/lib/uniform-matrix \
  /opt/uniform-matrix/Uniform.Matrix.Server.Host --Host:Ui=Dashboard
sudo systemctl start uniform-matrix
```
