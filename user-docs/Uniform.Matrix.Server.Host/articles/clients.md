# Connecting clients

Log in with the full user ID and the homeserver address `http://localhost:8008`. In Element, edit the homeserver on the sign-in page. In the Uniform.Matrix terminal client, give the address with `--Matrix:Homeserver`:

```bash
./Uniform.Matrix.Client.Terminal --Matrix:UserId=@alice:localhost --Matrix:Homeserver=http://localhost:8008
```

Without an address, clients find the server from the user ID: they look for `https://<server name>/.well-known/matrix/client`. That needs a real server name, HTTPS and `Server:PublicBaseUrl`.
