---
title: Room administration
uid: Uniform.Chat.Terminal.Rooms.Article
---
# Room administration

Admins and moderators can change the room and its members from the Room menu or with commands; the server refuses what your power level does not allow.

- **Set name**, **Set topic** and **Set avatar** (an image file; `/avatar none` removes it).
- **Kick** removes a member, who may join again; **Ban** also stops them joining again, and **Unban** lets them back. A reason is optional.
- **Power level** makes a member an admin (100), a moderator (50) or an ordinary member (0), or gives any level. Lowering your own level asks first, as you cannot raise it back yourself.

Members can be given by user ID or, when it is unique in the room, by display name.

## Public rooms and addresses

A public room is one anyone can join, also from other servers. **Room → New public room**, or `/create #room Room name`, creates one with the address `#room:yourserver` and lists it in your server's room directory. Public rooms are not encrypted, as anyone can join and read them.

- **Add alias**, or `/alias #room`, gives the current room another address on your server. The first address becomes the room's main address, which other clients show. `/unalias #room` removes an address.
- **Directory listing**, or `/publish` and `/unpublish`, lists the room in your server's public room directory, or removes it. Listing does not change who may join; the room's join rule decides that.

Addresses can only be made on your own server. People on other servers find listed rooms with their client's room directory by choosing your server; see [Joining rooms](xref:Uniform.Chat.Terminal.Joining.Article).
