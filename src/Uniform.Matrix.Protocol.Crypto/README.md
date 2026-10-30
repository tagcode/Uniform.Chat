# Uniform.Matrix.Protocol.Crypto

Matrix [end-to-end encryption](https://spec.matrix.org/latest/client-server-api/#end-to-end-encryption) in pure .NET: Olm and Megolm, device keys, cross-signing, secret storage, server-side key backup with a recovery key, key requests between own devices, and emoji (SAS) verification. No native libraries; the curves come from BouncyCastle.

```bash
dotnet add package Uniform.Matrix.Protocol.Crypto
```

## Encrypt a room

`OlmMachine` does the protocol work for one device. It talks to the server through `IKeyTransport`; [Uniform.Matrix.Client](https://www.nuget.org/packages/Uniform.Matrix.Client) has `MatrixKeyTransport` over a logged-in `MatrixClient`.

```csharp
using Uniform.Matrix.Protocol.Crypto;

using OlmMachine machine = await OlmMachine.CreateAsync(new MatrixKeyTransport(client), new InMemoryCryptoStore(), client.UserId!, client.DeviceId!, pickleKey);
await machine.UploadInitialKeysAsync();

// Every sync goes through the machine: to-device messages, device lists, one-time key counts
await machine.ReceiveSyncAsync(sync);

// Send encrypted, and decrypt what arrives
EncryptedContent content = await machine.EncryptRoomEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "secret" }, memberIds);
await client.SendEventAsync(roomId, EventTypes.RoomEncrypted, content);
RoomEventDecryption result = await machine.DecryptRoomEventAsync(roomId, clientEvent);
```

`SetUpRecoveryAsync` creates cross-signing keys, a key backup and the recovery key; `RecoverAsync` restores them on a new login. `RequestVerificationAsync`, `AcceptVerificationAsync` and `ConfirmVerificationAsync` run emoji verification.

## Olm and Megolm directly

```csharp
using MegolmOutboundSession outbound = MegolmOutboundSession.Create();
using MegolmInboundSession inbound = MegolmInboundSession.FromSessionKey(outbound.SessionKey);

string ciphertext = outbound.Encrypt("hello");
MegolmPlaintext plaintext = inbound.Decrypt(ciphertext);
Console.WriteLine(plaintext.Text); // hello
```

`OlmAccount` and `OlmSession` cover Olm. Pickles use vodozemac's format, so state moves between the two implementations.

## Notes

- The Olm and Megolm code is a C# port of [vodozemac](https://github.com/matrix-org/vodozemac) 0.11.1, and is tested against vodozemac itself. Unlike vodozemac, it has not been audited, and .NET cannot guarantee constant-time code or wipe all secrets from memory.
- Licensed `MIT AND Apache-2.0`: the vodozemac-derived files stay under the Apache License 2.0, whose text and the notices are included in the package.

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Chat), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [How it works](https://github.com/tagcode/Uniform.Chat/blob/main/docs/Uniform.Chat/Uniform.Matrix.Protocol.Crypto/index.md)
- [Building](https://github.com/tagcode/Uniform.Chat/blob/main/docs/Uniform.Chat/building.md) and [testing](https://github.com/tagcode/Uniform.Chat/blob/main/docs/Uniform.Chat/testing.md)
