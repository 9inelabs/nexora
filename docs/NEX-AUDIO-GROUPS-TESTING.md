# NEX-Audio: groups, mute and solo, test guide

This covers Phase 2 of making NEX-Audio a complete desk. It adds a new **Groups** tab and a **Groups** bank on the Mixer.
Try it on the sound PC with the X32 connected. Everything also works on the test device (Setup > Driver > Test device).

## What is new

| Where | What it does |
|---|---|
| **Groups** tab | Make up to 24 control groups. Each one is one of three kinds: **Control group (VCA)**, **Linked faders** or **Mute group**. Name it, pick a colour, tap strips to put them in. |
| Control group (VCA) | A master fader that turns every member up or down together and keeps their balance. It never mixes their sound together, so each one keeps its own EQ, sends and routing. It also has a mute. |
| Linked faders | Move any member's fader and the others follow by the same amount. The master moves them all. |
| Mute group | One button mutes every member, for example all the band mics during the sermon. Mute groups also show as buttons above the faders on the Mixer. |
| Safe members | The lock on a member keeps it in the group but makes it ignore the group for now. Tap the lock again to bring it back. |
| **Mixer > Groups** bank | The group masters as faders, with **MUTE** and **SHOW**. **SHOW** puts only that group's members on the faders (spill). The × on the grey label shows everything again. |
| Group chips on faders | Each fader shows small coloured chips for the groups it is in. A dimmed chip means that member is safe. When a VCA master moves a fader, the real level shows under it (for example "→ -4.5"). |
| **Hard mute** | On the Channel page, under MUTE. See the next section. |
| **Solo** (Groups tab) | Two solo buses. Choose where the solo buttons go (bus 1, bus 2 or both). Each bus listens before or after the fader and has its own level. **One solo at a time** clears the other solos when you press a new one. **Selecting a strip solos it** gives auto-solo. **Solo safe** (Channel page) keeps a strip soloed when solos are cleared. |
| **Stereo pairs** (Groups tab, or the Channel page) | Link channel 1 with 2, 3 with 4, and so on. The right channel takes the left one's settings, they pan hard left and right, and after that any change to either goes to both (pan works as a mirror). Each side keeps its own name, input, X32 preamp and direct out. |
| Outputs | Two new sources: **Solo bus 2 left** and **Solo bus 2 right**. |

## Mute and hard mute: the exact difference

- **Mute** silences the strip in the main mix, groups, every send (wedges and in-ears too) and the stream. This is how mute worked before.
  A **direct out** taken before the fader (IN, PRE EQ, POST EQ or PRE) keeps going, so a recording or broadcast feed of that mic carries on.
  This is a change from Phase 1, where a mute also silenced every direct out.
- **Hard mute** silences the strip completely, the direct out too. Nothing else can bring it back: scenes, mute groups, control groups and Undo all leave it alone. Only pressing **HARD MUTE** again does. Use it for a mic that buzzes, or a channel that must stay dead for the whole service.
- On the faders, a hard-muted strip's button reads **HARD** in dark red. A strip muted by a group shows a red outline on its MUTE button, while its own mute stays off.

## 1. Control group (VCA)

1. Open **Groups**. Press **+ Control group (VCA)**. Name it "Drums" and tap Kick, Snare, Hi-hat, Toms and OH.
2. Open **Mixer > Groups**. Pull the Drums master down to -6.
   - [ ] The drums get quieter together and keep their balance. Each channel's own fader stays where it was, with "→ -6.0" under it.
3. Pull the master to the bottom.
   - [ ] All the drums go silent. Bring the master back to 0 dB (tap the number) and they come back.
4. Press the master's **MUTE**.
   - [ ] Every drum mic mutes. Each drum fader's MUTE button shows a red outline but is not lit.
5. On the Groups tab, tap the lock on Kick. Move the master.
   - [ ] Kick no longer follows. Tap the lock again and it does.

## 2. Linked faders and mute groups

1. Make **+ Linked faders** with Choir L and Choir R. Move Choir L's fader.
   - [ ] Choir R moves by the same amount.
2. Make **+ Mute group** called "Band mics" with the band channels.
   - [ ] A **Band mics** button appears above the faders on the Mixer.
3. Press it.
   - [ ] Every band mic mutes, in the main mix and in the wedges. Press it again and they come back.
4. Remove a group with the × on the Groups tab while it is muted or its master is down.
   - [ ] The sound does not change: its members keep the level and mute they had.

## 3. Spill

1. On **Mixer > Groups**, press **SHOW** on Drums.
   - [ ] Only the drum channels are on the faders, and the label says "Showing the members of Drums".
2. Press the × on that label, or a bank button.
   - [ ] All the faders come back.

## 4. Mute and hard mute

1. Give the Pastor's channel a direct out at **PRE** to the recorder (Routing > Inputs and direct outs), and a pre-fader send to the Pastor IEM.
2. Press **MUTE** on the Pastor.
   - [ ] The PA and the IEM go silent. The recorder still gets the mic.
3. Unmute, then press **HARD MUTE** on the Channel page.
   - [ ] The PA, IEM and recorder all go silent. The fader button reads HARD.
4. Recall a scene, press a mute group the Pastor is in and off again, and press Undo.
   - [ ] The Pastor stays hard muted through all of them.

## 5. Solo

1. On **Outputs**, set an output to **Solo bus 1 / headphones left** and another to **Solo bus 2 left**.
2. On **Groups > Solo**, set **Solo buttons go to** to **Both solo buses**, bus 1 **Before the fader** and bus 2 **After the fader**.
3. Solo the Lead vox and pull its fader down.
   - [ ] Bus 1 stays the same. Bus 2 gets quieter with the fader.
4. Turn on **One solo at a time**. Solo Snare, then Kick.
   - [ ] Only Kick is soloed.
5. Mark Talkback **Solo safe** (Channel page, Routing), solo it, then press **Clear solo**.
   - [ ] Every solo clears except Talkback.
6. Turn on **Selecting a strip solos it**. Tap a channel's name.
   - [ ] That channel is soloed on its own.

## 6. Stereo pairs

1. On **Groups > Stereo pairs**, link **Ch 15 + 16** (Keys L and Keys R).
   - [ ] Keys R takes Keys L's EQ, dynamics, fader and sends. Keys L pans hard left and Keys R hard right. The faders show "15 · L" and "16 · R".
2. Change the EQ on Keys R.
   - [ ] Keys L changes too. Renaming one side does not rename the other.
3. Put Keys L in a control group.
   - [ ] Keys R joins it too.

## 7. X32 failsafe

1. With the X32 connected, pull a VCA master to -10 and mute a mute group.
   - [ ] The X32's own faders and mutes for those channels follow, so a failsafe handover sounds the same. The channels' own faders in NEX-Audio do not move.

## Known limits

- Not yet tried on the real X32 or the real interface: the 260 automatic tests run against the built-in test device.
- Stereo pairs copy effects in the rack when they are linked, but later changes to rack effects are not copied across. Change the rack on both sides.
- The two compressors in a stereo pair have the same settings but listen separately, so they may not always turn down together.
- The X32 shows the level after the VCA masters. It has no matching control groups of its own.
- Next in the plan: Phase 3 (bus and main processing, with the left-centre-right main).
