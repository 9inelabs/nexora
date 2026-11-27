# NEX-Audio: metering, layouts and workflow, test guide

This covers Phase 5 of making NEX-Audio a complete desk. Everything here is NEX-Audio's own design, with its own names and look.
Try it on the sound PC with the X32 connected. Everything also works on the test device (Setup > Driver > Test device).

## What is new

| Where | What it does |
|---|---|
| **Meter bridge** (tab next to Channel, or Ctrl+B) | Every channel, group, effect return, mix, matrix and output at once, in sections. Stereo strips show two bars. A red light at the top stays on after a clip until you press **Clear peaks and clips**. |
| **Left and right meter** (meter bridge, and the Stream tab) | Shows how alike left and right are. **+1** means safe in mono (phones, a mono sub). **0** means wide. **Below 0** means they fight and the sound thins out in mono. It shows "Too quiet to tell" in silence. |
| **Peak hold and fall speed** (your menu, top right) | Peak hold: off, 1 second, 3 seconds, or until cleared. Meters fall: fast, medium or slow. This changes every meter in NEX-Audio. |
| **My banks** (Mixer, above the faders) | Up to 8 fader banks of your own, each with up to 24 channels and buses in any order. Press **+ Bank**, give it a name and tick the strips. Double-tap a bank, or press its pencil, to change or delete it. Banks are saved for each user, so the next engineer keeps their own. |
| **Utility faders** (Mixer, left of Centre and Main) | Two faders that stay put whatever bank is showing. Press **SET** and choose any strip's fader, or its send to a mix or effect. For example, the pastor's mic, or the lead vocal in the vocal wedges. Saved for each user. |
| **Quick select** (top right, or Ctrl+K or /) | Type a channel number ("12"), a bus ("mix 2", "fx 1", "group 3") or part of a name ("pas"). Press Enter for the first match. The strip is selected and its bank comes up on the faders. |
| **Keyboard shortcuts** (listed in your menu) | ← → previous or next strip. M mutes and S solos the selected strip. Page Up and Page Down change the bank. Ctrl+1 to Ctrl+8 open your banks. Ctrl+Z undoes and Ctrl+Y redoes. Ctrl+M opens the Mixer, Ctrl+B the meter bridge, and Ctrl+E the Channel page. Shortcuts do nothing while you type in a box. |
| **Own window** (meter bridge, Routing, and the screen button on the Channel page) | Opens that page in a window of its own. With a second monitor, it opens there, full screen. Pressing it again brings the window to the front. |
| **Text size and high contrast** (your menu) | Text size: Normal, Large or Extra large (the whole desk grows). High contrast: black panels, white writing and brighter edges, for a bright room. Saved for each user. |
| **Preset library** (Channel page > Presets) | Search by name or folder, show one folder or only your own presets, then Load. Save the open strip with a name and a folder. Move your own presets to another folder, or delete them. The built-in starting points are in Voices, Drums, Instruments, and Music and playback. |
| **Offline show preparation** (Show tab > Prepare next Sunday's show offline) | Pauses the X32 link, the failsafe, the heartbeat and the stream, so nothing you try reaches the room. A blue bar says so on every page. Build the mix, scenes and cues, save the show, then open it on the sound PC. **Go back online** links to the X32 again and brings its mix into step. The X32's channel names are remembered, so they still show offline. |
| **Sample rate** (Setup > Driver) | Choose the rate when the driver offers more than one: 44.1, 48, 88.2 or 96 kHz. At 96 kHz each buffer is half as long (less delay), but it costs about twice the processing. The stream to the Live Output PC always goes out at 48 kHz. **The X32's USB card only runs at 44.1 or 48 kHz**, so 96 kHz is for other interfaces. Use 48 kHz with the X32. |

## 1. The meter bridge

1. Open **Meter bridge**. Every strip should move, grouped as Channels, Groups, Effects, Mixes, Matrix and Outputs.
2. Push a channel until it clips: its red light stays on. Press **Clear peaks and clips**.
3. In your menu (the round button top right), set **Peak hold** to **Until cleared** and **Meters fall** to **Slow**. Watch the meters change.
4. Press **Own window**. With a second monitor connected, the bridge should open there.

## 2. The left and right meter

1. Play a stereo track. The main meter should sit between +0.5 and +1.
2. Flip the polarity of one side of a stereo pair. The meter should go red and below 0. Put it back.
3. On the Stream tab, check the stream meter reads the same way.

## 3. Your own banks and utility faders

1. On the Mixer, press **+ Bank**, name it "Voices", tick the pastor, the lead vocal and the BVs, then press **Save bank**.
2. Press **Ctrl+1**. The Voices bank should come up. Use Page Down to step through the banks.
3. Press **SET** on Utility 1, choose the pastor's mic and **Its own fader**. Change to another bank: Utility 1 still moves the pastor.
4. Press **SET** on Utility 2, choose the lead vocal and **Send to Vocal wedges**.
5. Sign out and sign in as someone else. They should have their own banks (none yet). Sign back in: your banks are there.

## 4. Quick select and shortcuts

1. Press **Ctrl+K**, type **12** and press Enter. Channel 12 is selected and the Ch 1–16 bank shows.
2. Press Ctrl+K, type **wedge**, and pick a mix.
3. With a strip selected, press **M** to mute and **M** again to unmute, then **←** and **→** to move between strips.

## 5. Text size and high contrast

1. In your menu, set **Text size** to **Large**, then **Extra large**. Everything grows. Extra large needs a big screen.
2. Switch on **High contrast**. Panels go black and writing goes white. Switch it off again.

## 6. The preset library

1. Open a channel, press **Presets**, and type **kick**. Only kick presets show.
2. Pick the **Voices** folder in the list on the right.
3. Save the open strip as "Pastor Mike lapel" in a folder called "Guest speakers". It appears in that folder.
4. On your own preset, press **Folder**, move it to "Voices", then delete it.

## 7. Prepare a show offline

1. On a PC with no X32 (an office laptop with NEX-Audio installed), start the test device. Then go to Show and press **Go offline**.
2. Change the mix, save snapshots, add cues, then press **Save show…**.
3. On the sound PC, use **Open show…** to open it.
4. Press **Go back online** on the laptop: the blue bar goes away.

## 8. Sample rate

1. Setup > Driver. With the X32, the list shows 44.1 and 48 kHz. Keep **48 kHz**.
2. On the test device, choose **96 kHz** and press **Start sound with these settings**. RATE at the top shows 96 kHz, and the stream still plays normally on the Live Output PC.

Nothing in this phase has been tried on the real X32 or the church PC yet. The test device shows the load of this PC, so the numbers on
the sound PC will be different.
