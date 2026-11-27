# NEX-Audio Phases 2 to 5: test guide

This build turns NEX-Audio into a full mixing desk. It covers the mixer and channel strips, the outputs, the stream mix, the failsafe, NEX-Audio's own effects, VST3 plugins, scenes and snapshots, and X32 fader and mute mirroring.

Test it in a rehearsal or on a weekday, never first in a service. Do the steps in order, because later steps build on earlier ones. Allow about half a day, plus one full rehearsal for the long test at the end.

**Do Phase 1 first.** The NEX-Audio Phase 1 guide sets up the USB link, the driver and the X32 network link, and checks the delay. All of that must pass before you start here.

---

## What you need

- The NEX-Audio PC, with this NEXORA installed and Phase 1 passing.
- The Live Output PC, updated to the same version (Settings > Updates > Update NEXORA).
- Both PCs and the X32 on the same church network.
- The X32 with its own stream feed still plugged into the Live Output PC's sound device (capture card or USB sound card). This is the backup for the stream, so keep it plugged in.
- Two people for the failsafe tests: one at the X32 and one at the PCs.
- A few microphones or instruments plugged in, or a playback source, so there is sound to mix.
- Optional: a free VST3 plugin, or your Waves plugins installed with Waves Central.

## Keep your normal X32 setup safe

Before changing anything, **save the current X32 scene** (Scenes > Save) under a name such as "Before NEX-Audio". To undo everything at the end, load that scene again.

---

## 1. Set up the two X32 scenes (once)

The failsafe works by switching the X32 between two scenes. You make them once.

1. On the X32, route the speaker outputs from the card: **Routing > XLR Out**, set them to **Card 1–16**. Save this as a scene called **NEX-Audio desk**.
2. Route the same outputs back to the X32's own buses (Main L/R, Mix 1–16). Save this as a scene called **Console only**.
   - On the X32's **Scenes** screen, use the **Safes** page to protect the channel and bus levels (faders, mutes, sends, EQ and dynamics), so recalling a scene changes the routing only. The exact page names differ a little between X32 firmware versions.
   - This matters. It means that when the X32 takes over, it keeps the levels NEX-Audio has been copying to it, so the sound stays close to what people were hearing. Test it once: change a fader, recall Console only, and check the fader did not move.
3. Assign one X32 **user button** to recall Console only. That is the manual failsafe: one press and the X32 runs the room on its own.
4. Load **NEX-Audio desk** again before you go on.

## 2. Open the mixer

1. On the NEX-Audio PC, open the **NEX-Audio** app (installed with NEX-Audio-Setup.exe), sign in with your NEXORA name and PIN, and start the sound (top right, **Start sound**).
2. The **Mixer** tab opens first. You should see 16 strips with names from the X32, each with a fader, meter, **MUTE** and **SOLO**.
3. Switch between **Channels 1–16**, **Channels 17–32** and **Buses**.

Check each of these:

- [ ] Moving a fader changes the level you hear. Double-click a fader to put it at 0 dB. The mouse wheel moves it 1 dB, and Shift with the wheel moves it finely.
- [ ] **MUTE** silences the strip. **SOLO** sends just that strip to your headphones or solo output. **Clear solo** turns every solo off.
- [ ] The small pan knob moves the sound left and right in the main mix.
- [ ] Under **Sends on faders**, pick a monitor mix. Every fader now shows that strip's send to the mix. Move a few faders, then pick the main mix again. The main faders are as you left them.
- [ ] Close NEXORA and open it again. The mix comes back exactly as it was.

## 3. Shape one channel

Press **EQ**, **DYN** or **FX** on a strip with a voice on it. The **Channel** tab opens for that strip.

- [ ] **Gain** (input level) changes how loud the channel arrives.
- [ ] **Low cut** removes rumble. Set it around 100 Hz on a voice and listen.
- [ ] **EQ**: turn a band up and down. The gold curve moves, and the shaded shape behind it shows the sound playing now. Switch the EQ **Off** and **On** to compare.
- [ ] **Gate**: on a quiet microphone, the gate meter shows when it is turning the sound down between words.
- [ ] **Compressor**: on a singer, the **COMPRESSOR** meter shows how much it is turning down the loud parts.
- [ ] **Previous strip** and **Next strip** move through the channels.
- [ ] **Rename**: give the strip a name on this desk only.
- [ ] **Copy EQ, dynamics and effects to**: pick another strip and press **Copy**. That strip now sounds the same.

## 4. Choose what each output plays

Open the **Outputs** tab. Each line is one card return on the X32.

1. Press **Suggested layout**. This puts the main mix on 1 and 2, mono on 3, the monitor mixes from 5, and the stream on 15 and 16.
2. Give each line a **SPEAKER NAME**, for example Left PA.

- [ ] The main speakers play the main mix.
- [ ] **LEVEL** and **MUTE** work on each output.
- [ ] **DELAY**: on a side fill or delay speaker, add a few milliseconds and listen for the sound lining up with the front speakers.
- [ ] **LOW CUT** and **LIMITER** are on for the speakers. The limiter stops a sudden loud sound from hurting the speakers or people's ears.
- [ ] **Inputs straight through** puts the outputs back to the Phase 1 test (each input straight back out). Press **Suggested layout** again afterwards.

## 5. Stream mix to the Live Output PC

### On the Live Output PC

1. Open **Settings > Recording & streaming** and tick **Use NEX-Audio's stream mix (over the network)**. Save.
2. Keep the X32's stream feed as the sound device in the same settings. It is the backup.

### On the NEX-Audio PC

1. Open the **Failsafe** tab. Under **Live Output PC**, type the Live Output PC's address (for example 192.168.1.20) and press **Save**.
2. Open the **Stream** tab. Use **The stream's own balance** to set each strip's level for the stream. These levels are taken before the room faders, so turning the PA down in the room does not change the stream.
3. Watch **Loudness**. Aim for about **-16 LUFS** for a spoken service and about **-14 LUFS** for music. Press **Start a new measurement** at the start of the service.

Check:

- [ ] On the Live Output PC, the **Service** tab has a **NEX-Audio** card. It says the sound is coming from NEX-Audio.
- [ ] Start a test recording on the Live Output PC. It has NEX-Audio's stream mix in it.
- [ ] Turn the room's main fader down on NEX-Audio. The recording's level does not change.
- [ ] Press **Stop sound** on NEX-Audio during the recording. Within about half a second the recording switches to the X32's own stream feed, and the NEX-Audio card says so. Press **Start sound** again and it switches back.

## 6. NEX-Audio's own effects

On the **Channel** tab, the **Effects rack** holds up to 8 effects, in order, after the compressor. Pick one from the list and press **Add**.

The built-in effects are:

- Compressor, Multiband compressor, De-esser and Limiter
- Reverb (room, hall or plate), Delay and Chorus
- Pitch correction, for a lead vocal

Check:

- [ ] Each effect changes the sound and can be switched **On** and **Off**.
- [ ] **Earlier in the rack** and **Later in the rack** change the order. **Remove** takes one out.
- [ ] A reverb or delay on a bus, sent from several vocals with **Sends on faders**, gives them all the same space.
- [ ] The de-esser softens harsh "s" sounds on a singer.
- [ ] Pitch correction pulls a slightly flat note into tune. Keep it gentle.

## 7. VST3 plugins (optional)

1. Install your plugins the normal way. For Waves, use Waves Central, and make sure your licences are on this PC.
2. Open the **Plugins** tab and press **Look for plugins**. The list fills with what NEX-Audio found. Any plugin that fails to load is listed with a reason.
3. On the **Channel** tab, add a plugin to the effects rack. Press **Open plugin window** to see its own controls.

Check:

- [ ] The plugin changes the sound.
- [ ] Every strip stays in time with each other when a plugin is added. NEX-Audio lines up the delay across the whole mix by itself. Listen to a drum kit or a choir across two strips, one with a plugin and one without. They should not sound hollow or doubled.
- [ ] If a plugin crashes, the sound carries on without it, and NEX-Audio starts the plugin again. After three crashes in a minute it stops trying and shows **Restart**.
- [ ] Plugin settings come back after you recall a scene (next step).

## 8. Scenes and snapshots

Open the **Scenes** tab.

**Scenes** are the whole desk for one kind of service.

- [ ] Type a name such as "Sunday 9am" and press **Save the desk as a new scene**.
- [ ] Change a lot of things, then press **Recall** on the scene. Everything goes back, including the outputs and plugin settings.
- [ ] **Save over** replaces a scene with the desk as it is now. **Delete** removes one.

**Snapshots** are moments within a service, such as worship, sermon and altar call.

- [ ] Save three snapshots in order. Tick **Levels only** on one so it changes just the faders and mutes.
- [ ] Set **Fade in over** to 2 seconds on one. Recalling it moves the faders smoothly instead of jumping.
- [ ] Use the big **Previous** and **Next snapshot** buttons to step through them in order.

## 9. X32 faders and mutes

On the **Failsafe** tab, under **Keeping the X32 in step**:

- [ ] With **Shadow mix** on, move faders and mutes in NEX-Audio. The X32's own faders follow within a moment. (The X32's sound does not change, because its outputs come from the card.)
- [ ] Turn on **Follow the desk**. Move a fader or press a mute on the X32 itself. NEX-Audio follows. This lets the engineer mix from the X32's own faders.

## 10. The failsafe

This is the most important test. Do it with one person at the X32 and one at the PCs, with sound playing in the room.

1. On the **Failsafe** tab, choose the two scenes: **X32 scene: NEX-Audio desk** and **X32 scene: Console only**. If the lists are empty, press **Read the scene names from the X32 again**.
2. Leave **Hand over after silence of** at 0.5 seconds.
3. Tick **Arm the failsafe**. The page shows **Armed**, and so does the NEX-Audio card on the Live Output PC.

Run each test, then press **Return to NEX-Audio** to go back:

- [ ] **By hand**: press **Hand the room to the X32 now**. The X32 recalls Console only and the room keeps playing through the X32. The page shows **On the X32**.
- [ ] **Sound stops**: press **Stop sound**. Within about half a second, the X32 takes over.
- [ ] **NEXORA closes**: close NEXORA on the NEX-Audio PC, or end it in Task Manager. The Live Output PC notices the missing heartbeat and recalls Console only on the X32.
- [ ] **Power cut**: pull the NEX-Audio PC's power cable (or hold the power button). The Live Output PC hands the room to the X32 within about a second.
- [ ] **USB cable**: pull the X32's USB cable from the NEX-Audio PC. The X32 takes over.
- [ ] **The user button**: press the X32 user button you set in step 1. The X32 takes over at once.
- [ ] After each test, **Return to NEX-Audio** loads the NEX-Audio desk scene and the room is back on NEX-Audio, with no loud pop.

Note how long each handover took and how different the sound was. If the X32's sound is very different from NEX-Audio's, check that Shadow mix is on and that the X32's scene safes protect the levels (step 1).

## 11. Who can do what

In **Settings > Users** on any PC:

- **NEX-Audio (sound engineer)** can use the whole desk.
- **NEX-Audio stream mix only** can open NEX-Audio but only change the **Stream** tab. Give this to a volunteer who looks after the online sound.

Check:

- [ ] A stream-only user can move the stream balance. If they try to change anything else, it springs back and a note says "You can change the stream mix only."

## 12. The long test

Run one full rehearsal on NEX-Audio from start to finish, with the failsafe armed and the stream recording on the Live Output PC.

- [ ] No clicks or dropouts. The **CLICKS** count at the top stays at 0.
- [ ] **ENGINE LOAD** stays well under 70%.
- [ ] The recording sounds even, and its loudness ends near your target.

---

## Results sheet

Copy this into a message back to me, with a note for anything that did not work.

| Step | OK / Problem | Notes |
| --- | --- | --- |
| 1. Two X32 scenes |  |  |
| 2. Mixer |  |  |
| 3. Channel |  |  |
| 4. Outputs |  |  |
| 5. Stream mix and backup |  |  |
| 6. Built-in effects |  |  |
| 7. VST3 plugins |  |  |
| 8. Scenes and snapshots |  |  |
| 9. X32 faders and mutes |  |  |
| 10. Failsafe (each of the 7 tests, with times) |  |  |
| 11. Permissions |  |  |
| 12. Long test (clicks, load, loudness) |  |  |
