# Uniform.Matrix.Video

Video for Matrix calls in pure C#, with no native libraries: a VP8 decoder ported from the reference decoder of [RFC 6386](https://www.rfc-editor.org/rfc/rfc6386), exact to libvpx on the VP8 test vectors.

```csharp
Vp8Decoder decoder = new();
VideoFrame? picture = decoder.Decode(frame);   // I420, or null for a frame that is not shown
```

It also reads WebM files, as browsers record them, into tracks and frames:

```csharp
WebmFile file = WebmFile.Parse(File.ReadAllBytes("clip.webm"));
WebmTrack video = file.Tracks.First(t => t.Kind == WebmTrackKind.Video);   // e.g. V_VP8
foreach (WebmFrame frame in file.Frames.Where(f => f.Track == video.Number)) decoder.Decode(frame.Data.Span);
```

The reference decoder is Copyright (c) 2010, 2011, Google Inc., under the BSD licence in THIRD-PARTY-NOTICES.md.
