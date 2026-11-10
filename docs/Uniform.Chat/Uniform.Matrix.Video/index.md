---
uid: Uniform.Matrix.Video.Article
title: Uniform.Matrix.Video
---
# Uniform.Matrix.Video

Video for calls, in pure C# with no native libraries: a VP8 decoder that turns the frames of a video call into pictures. Nothing here knows about Matrix or the network: [Uniform.Matrix.WebRtc](xref:Uniform.Matrix.WebRtc.Article) puts the frames together from RTP, and [Uniform.Matrix.Calls](xref:Uniform.Matrix.Calls.Article) decodes them and gives the pictures to the app.

## Decoding

[`Vp8Decoder`](xref:Uniform.Matrix.Video.Vp8.Vp8Decoder) decodes one frame at a time, in order, starting with a keyframe. [`Decode`](xref:Uniform.Matrix.Video.Vp8.Vp8Decoder.Decode*) returns the picture of a shown frame, or null for a frame kept only as a reference, such as an alternate reference frame. A frame that cannot be decoded throws [InvalidDataException](xref:System.IO.InvalidDataException); after that the decoder waits for the next keyframe.

To decode a stream frame by frame:

```csharp
Vp8Decoder decoder = new();
foreach (byte[] frame in frames)
{
    if (decoder.Decode(frame) is VideoFrame picture) Show(picture);
}
```

A [`VideoFrame`](xref:Uniform.Matrix.Video.VideoFrame) is I420: full-size luma, and both chroma planes at half the width and height, rounded up, each tightly packed. [`CopyToBgra`](xref:Uniform.Matrix.Video.VideoFrame.CopyToBgra*) converts it to 32-bit BGRA for display, from the BT.601 limited range that WebRTC video uses; e.g. into a bitmap's rows:

```csharp
picture.CopyToBgra(pixels, stride);
```

## How the decoder is built

The decoder is a port of the reference decoder in [RFC 6386](https://www.rfc-editor.org/rfc/rfc6386), Attachment One, with arrays and indices in place of pointers. Its constant tables in `Vp8Tables.cs` are generated from the reference sources. It works as the reference does, one macroblock row at a time:

1. Modes and motion vectors, from the first partition.
2. Coefficients, from the row's partition, dequantized.
3. Prediction, intra or from a reference frame with the six-tap filter, plus the inverse transforms of the residual.
4. The loop filter, one row behind, so that intra prediction sees unfiltered neighbours.

Where the reference decoder and libvpx differ, it follows libvpx, which produced the test vectors' checksums. The tests decode all 17 VP8 test vectors of libvpx and compare the MD5 of every frame. The licence of the reference decoder and the test vectors is in THIRD-PARTY-NOTICES.md.

On a desktop computer it decodes about 150 to 250 frames a second at 640×480.
