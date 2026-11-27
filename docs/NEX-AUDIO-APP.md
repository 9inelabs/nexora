# NEX-Audio is its own app

NEX-Audio, the mixing desk for the X32, no longer sits inside NEXORA. It has its own installer
(**NEX-Audio-Setup.exe**) and its own updates, and it is built from the same code as NEXORA.

## On the sound PC

1. Install **NEX-Audio-Setup.exe** from the same release as NEXORA.
2. Open **NEX-Audio**. If NEXORA was already set up on this PC, it finds the Live Output PC by itself.
   Otherwise, open **Connection to the Live Output PC** and type the pairing code from NEXORA's
   Settings on the Live Output PC.
3. Sign in with your NEXORA name and 4-digit PIN. Only people with the NEX-Audio or stream-mix
   permission (and admins) are listed.
4. If NEXORA already ran the desk on this PC, NEX-Audio brings that desk across the first time
   (mix, scenes, presets, patch, plugins). If the sound was on, it comes back on.

## Good to know

- **The sound does not depend on the network or on anyone being signed in.** The sound engine runs on
  its own. Signing out, or closing the window, leaves it running.
- **Offline sign-in.** Anyone who has signed in on the sound PC before can sign in again when the
  Live Output PC cannot be reached. Their PIN is kept there only as a scrambled (hashed) copy.
- **Updates.** When the Live Output PC updates NEXORA from the internet, it also fetches the matching
  NEX-Audio. On the sound PC: the round initials button at the top right › **Check for updates** ›
  stop the sound › **Update**. NEX-Audio never updates by itself and refuses while the sound is on.
- **What stays in NEXORA.** The Live Output PC still receives NEX-Audio's stream mix for recording and
  streaming (Settings › Recording and streaming › "Use NEX-Audio's stream mix"), still runs the X32
  failsafe watcher, and still shows the NEX-Audio card on the Service tab. The NEX-Audio section is
  gone from NEXORA's section picker.
- NEX-Audio keeps its files in its own data folder (`%LocalAppData%\NexAudioData`), apart from NEXORA's.
