# Accounts

The server starts with registration disabled, so nobody can create an account by themselves. The simplest way to add people is the dashboard: Server → Users → New (see [Users window](dashboard.md#users-window)).

To let people register in their own client instead:

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
