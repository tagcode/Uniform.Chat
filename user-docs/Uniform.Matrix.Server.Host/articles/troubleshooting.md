# Troubleshooting

| Message | Meaning |
|---|---|
| `Cannot listen on http://localhost:8008: the address is already in use` | Another program, perhaps another copy of the server, uses the port. Stop it, or choose another port with `--Urls=http://localhost:8009`. |
| `M_FORBIDDEN: Registration is disabled.` | Set `Server:Registration` to `Open` or `Token`. |
| `M_USER_IN_USE` | The user name is taken; choose another. |
| `... is not an ed25519 signing key.` | `signing.key` was changed or damaged. Restore it from a backup. |
| `Running without dashboard: input is redirected; --Host:Ui=Dashboard forces it` | The server was started without a terminal, for example from an IDE's run button or with redirected input or output. Run it from a terminal, or use `--Host:Ui=Dashboard` to force the dashboard. |
