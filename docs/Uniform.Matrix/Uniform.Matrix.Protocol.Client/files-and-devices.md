# Files and devices

```csharp
await using FileStream file = File.OpenRead("cat.png");
await client.SendFileAsync(roomId, file, "cat.png", "image/png");         // uploads, then sends m.image

using MediaDownload media = await client.DownloadMediaAsync(MxcUri.Parse(message.Url!));
await using FileStream copy = File.Create(media.FileName ?? "download");
await media.Content.CopyToAsync(copy);

IReadOnlyList<Device> devices = await client.GetDevicesAsync();
try
{
    await client.DeleteDevicesAsync(["OLDDEVICE"], password: null);
}
catch (UserInteractiveAuthException ex) when (ex.AcceptsPassword)
{
    await client.DeleteDevicesAsync(["OLDDEVICE"], AskPassword());          // answers the challenge
}
```
