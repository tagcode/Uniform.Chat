---
title: Voice and video calls
uid: Uniform.Chat.Desktop.Calls.Article
---
# Voice and video calls

You can voice call the other person of a direct chat. Calls work with other apps that make Matrix 1:1 calls, such as Element on the desktop and Element Classic on Android. Element X makes group-style calls, which this app does not support yet.

## Calling

Open the direct chat, and press 📞 in its header. A bar at the top of the window shows the call: *Calling…* while it rings on the other side, then the call's length once you are connected.

## Answering

An incoming call shows the bar at the top, whichever room is open, with **Answer** and **Decline**. When the app is in the background, a notification tells about the call. If you have the app on several devices, they all ring; answering on one stops the others.

## Video

On a device with a camera, a phone or a Linux computer with a webcam, press 📹 in a direct chat's header to start a video call. When someone calls you with video, the bar says *Incoming video call*; **Answer with video** also sends your camera, and **Answer** answers with voice only. The app asks for the camera permission the first time.

During a video call the video fills the screen; **Chat** shows the rooms and messages under a smaller call bar, and **Video** brings the full screen back. Your own camera shows small in a corner. **Switch camera**, shown when there is more than one camera, changes between them, e.g. the front and back cameras of a phone; and **Camera off** stops your camera until you press **Camera on**; the call goes on with voice.

The picture turns upright when either phone is held sideways. A phone that cannot keep up with the other's video asks it for a smaller picture, 320x240 at 10 frames per second, and for full quality again once it can. The desktop app sends video from a webcam on Linux; your user needs access to it, usually as a member of the `video` group. On Windows it shows the other person's video, but cannot send video yet.

## During a call

- **Mute** stops sending your voice; the bar says *muted*.
- **Speaker** and **Earpiece** switch phones between the loudspeaker and the earpiece.
- **Hang up** ends the call, or cancels one that still rings.

The bar says *relayed* when the call goes through your server's relay (TURN), e.g. when a firewall stands between you, and *Reconnecting…* while the network is down for a moment. After the call ends, the bar tells why for a few seconds.

## Phones

The first call asks for the microphone permission, and on Android 13 and later also the permission for notifications. While a call goes on, a notification stays in the status bar, so that the call keeps going when you switch to another app.

## When there is no sound

Try *Test audio…* in the settings: you hear yourself a second later. If you hear nothing there, choose the microphone and speaker in the system's sound settings.
