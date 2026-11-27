# NEX-Audio: live hardening and the full acceptance run

This is the last phase of the plan. It makes the desk safe to trust on a Sunday, then walks through every test
on the brief's acceptance checklist, so one person can go through the whole desk with a pen.

Tick each box as you go. If a box fails, note the time: the Diagnostics page and its event log will show what happened then.

## What is new

- **Safe recovery.** If the X32 is switched off or the USB cable comes out, the sound engine does not crash. It says
  so in plain words, keeps trying every few seconds, and the sound comes back by itself when the cable goes back in.
  If anything inside the mix fails for a moment, that buffer plays the plain patch instead, the mix is loaded again,
  and the event is logged. A filter that ever produces a sample that is not a number is silenced before the speakers.
- **X32 link drops.** When the network link to the X32 is lost, it is logged. When it comes back, NEX-Audio sends the
  X32 the whole mix again, so the two agree even if the X32 was restarted.
- **Diagnostics page** (Setup > Diagnostics): the driver, channels, sample rate, measured clock, buffer, latency, load,
  dropouts in the last 10 minutes, device losses, mix faults, the X32 link, the failsafe, the stream, and the hardware
  this desk does not have, shown grey as unavailable. Below that is the **event log**, newest first, with a filter for
  problems only. **Save a report** writes the whole page and the last 500 events to a text file
  (`audio/reports`). Every event is also kept in one file per day (`audio/events`).
- **Pretend unplug.** When the test device is running, the Diagnostics page has **Pull the test device's cable**, so the
  recovery can be tried without an X32. It never touches a real driver.
- **Finer recall scopes.** Scenes, snapshots and cues can now bring back any mix of these sections: input, EQ,
  dynamics, inserts, faders, mutes, sends, effects, routing, outputs and groups. On the Scenes tab, choose
  **Custom** and press the sections you want.
- **Channel colours.** A channel's Sends page has a **Colour** choice in its Routing card, so a colour can be set in NEX-Audio as well as on the X32.
- **Channel safes per section.** On a channel's Sends page, in the Routing card, **Or keep safe only** keeps just its input,
  its EQ and dynamics, its faders and mutes, its sends, or a mix of these, while the rest of the channel is recalled.
  A stereo pair shares its safes. The recall preview lists a partly safe channel under "kept safe".

## Before you start

- [ ] Install the new NEX-Audio on the sound PC and open it.
- [ ] Setup > Driver: start the X32 driver (or the test device if you have no X32 to hand).
- [ ] Setup > Diagnostics shows **Everything is healthy**, or explains each amber or red figure.

## Part A: live hardening

### A1. The USB cable comes out (on the test device)

1. [ ] Start the test device. Open Setup > Diagnostics.
2. [ ] Press **Pull the test device's cable**.
   - [ ] Within about 2 seconds the header says no sound is coming from the driver. NEX-Audio does not close or freeze.
   - [ ] "Device lost" turns red and shows 1×. The event log shows a PROBLEM line.
   - [ ] "Driver restarts" goes up every few seconds while the cable is out.
3. [ ] Press **Plug the test device back in**.
   - [ ] Within about 5 seconds the sound runs again by itself, and the log says "Sound running on Test device".
   - [ ] The mix is exactly as it was (faders, EQ, sends).

### A2. The USB cable comes out (on the real X32, at a rehearsal, never in a service)

1. [ ] With the X32 driver running and the failsafe armed, pull the USB cable from the X32.
   - [ ] NEX-Audio stays open. The header and Diagnostics say no sound is coming from the driver.
   - [ ] After the failsafe time, the X32 recalls its "Console only" scene and the room is heard through the X32.
   - [ ] The event log shows the loss and the failsafe handover as PROBLEM lines.
2. [ ] Plug the USB cable back in.
   - [ ] The sound comes back by itself within about 10 seconds. You may need to press **Return to NEX-Audio** on the Failsafe tab, at a quiet moment.
3. [ ] Switch the X32 off and on again. Repeat 1 and 2. NEX-Audio never crashes.

### A3. The network link to the X32 drops

1. [ ] With the X32 linked (Setup > X32 link), pull the X32's network cable.
   - [ ] Within about 15 seconds the X32 link shows "not answering". "X32 link lost" shows 1×.
   - [ ] The sound through USB carries on; nothing changes in the room.
2. [ ] Move a few faders in NEX-Audio while the cable is out, then plug it back in.
   - [ ] The log says the X32 answers again, and the X32's faders move to match NEX-Audio.

### A4. Dropouts and the clock

1. [ ] Leave the desk running with a typical show for 30 minutes.
   - [ ] "Dropouts, last 10 min" stays at 0. If not, raise the buffer on the Driver page, and check the PC check page.
   - [ ] "Clock" says steady (within 200 ppm of the X32's rate). Anything else means the driver and X32 disagree about the rate.
2. [ ] Press **Save a report**. A message says where the file went. Open it: it lists the same figures and the events.

### A5. Finer recall scopes

1. [ ] Make a scene, then change channel 1's EQ, its fader and its mute.
2. [ ] On the Scenes tab, choose **Custom**, press only **EQ**, and recall the scene.
   - [ ] Channel 1's EQ comes back; its fader and mute stay as you changed them.
3. [ ] Choose Custom again with only **faders**. Recall. The fader comes back; the mute stays.
4. [ ] Save a snapshot with Custom set to **mutes** and **sends**. Recall it later: only mutes and sends change.
5. [ ] On the Show tab, make a cue that recalls with **Faders, Mute** and check it does only that.

### A6. Channel safes per section

1. [ ] On channel 1, Sends page, Routing card, set **Or keep safe only** to **Eq, Dynamics**.
2. [ ] Change channel 1's EQ and fader, then recall a scene with everything.
   - [ ] The fader comes back; the EQ you set stays.
   - [ ] Before recalling, the eye (preview) lists channel 1 under "kept safe".
3. [ ] Link channels 1 and 2 as a stereo pair: channel 2 takes the same safe.
4. [ ] Set channel 1's safe back to None.

## Part B: the brief's acceptance checklist

Each line is one test from the brief, with where to do it in NEX-Audio.

1. **Create, rename, colour and reorder channels; save and reopen the project.**
   - [ ] Channel page: type a new name at the top and press **Rename**. On its Sends page, Routing card, pick a **Colour** (or leave it as on the X32).
   - [ ] Reorder: make your own fader bank (Mixer, **+ Bank**) and put channels in any order there. The 32 input channels keep the X32's numbering on the main banks, so the stage patch never changes by accident.
   - [ ] Show tab: **Save show…**, close NEX-Audio, open it again and **Open show…**. Names, colours and banks are back.
2. **Route one input to LR, multiple aux buses, a subgroup and a matrix output.**
   - [ ] Routing tab grid: give channel 1 to the main, two mix buses, a subgroup, and a matrix fed from the main. The trace shows every path.
3. **Switch a send between pre-fader and post-fader.**
   - [ ] On a mix bus tag, set channel 1's send tap to PRE. Pull the channel fader down: the bus still hears it. Set it to POST: the bus follows the fader.
4. **Verify channel EQ, compressor, gate, de-esser and delay with bypass and parameter changes.**
   - [ ] Channel workspace: turn each of EQ, compressor and gate on and off, and move their controls. The graphs and the sound follow smoothly with no clicks.
   - [ ] Add a de-esser (Effects, Dynamics) and a channel delay (Input section). Bypass each.
   - [ ] Parameter changes over time are done with snapshots and cues, not a timeline (as agreed: no timeline automation).
5. **Confirm stereo linking, pan/balance and channel polarity.**
   - [ ] Link channels 1 and 2 (Sends page, Routing card): they pan hard left and right and move together. Pan on a pair works as balance.
   - [ ] Input section: **Polarity flip** on one of two identical signals cancels them.
6. **Create a monitor mix that is independent of the FOH fader.**
   - [ ] Make a mix bus with PRE sends. Move the main faders: the monitor mix does not change.
7. **Create and recall a snapshot while protecting a channel with a safe.**
   - [ ] Covered by A5 and A6. Also tick **Scenes leave this strip alone** (the whole channel) and check nothing on that channel moves.
8. **Use a VCA-style group without summing audio; test mute-group and moving-fader modes.**
   - [ ] Groups tab: a VCA group moves the members' levels without a new bus. A mute group mutes its members. A linked group moves the members' faders.
9. **Insert an FX processor, route a send and return, and save the FX configuration.**
   - [ ] Add a reverb effect bus, send channels to it, check its return on the mixer. Save a scene and recall it: the reverb and its settings come back.
10. **Detect and block a routing feedback cycle.**
    - [ ] Routing tab: try to send a bus into a bus that already feeds it. It is refused with a red cross and a message.
11. **Simulate an audio device disconnect and confirm safe recovery without a crash.**
    - [ ] Covered by A1 (test device) and A2 (real X32).
12. **Record and replay multiple input channels and use them for virtual soundcheck.**
    - [ ] Soundcheck tab: record 8 channels for a minute, then switch to playback. The channels hear the recording; the yellow banner shows.
13. **Show channel, bus, matrix, FX, CPU/DSP and I/O resource usage.**
    - [ ] Setup > Resources (pools and processor use) and Setup > Diagnostics (load, inputs and outputs).
14. **Confirm 48 kHz and 96 kHz handling where the device supports it.**
    - [ ] Setup > Driver: 48 kHz runs. The X32 USB card offers 44.1 and 48 kHz only, so 96 kHz is shown as unavailable on Diagnostics. On the test device, 96 kHz runs and the stream is still sent at 48 kHz.
15. **Unavailable hardware is clearly marked unavailable.**
    - [ ] Setup > Diagnostics, Hardware: MADI, Dante, AES/EBU, Optocore/SoundGrid, word clock and GPIO show grey as unavailable, never as connected.

## What this phase does not do

- It does not test real gear for you: parts A2, A3 and A4 need the X32 and the sound PC.
- No timeline automation (as agreed). Snapshots and cues change settings at set moments instead.
- The hardware items (more channels, MADI, Dante and so on) need extra hardware and are not part of this plan.
