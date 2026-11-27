# NEX-Audio: show control and virtual soundcheck, test guide

This covers the new **Show** and **Soundcheck** tabs. Try them on the sound PC with the X32 connected.
You can also try everything except the recording on the test device (Setup > Driver > Test device).

## What is new

| Where | What it does |
|---|---|
| **Show** tab | The **order of service** (cue list) with a big **GO** button, **macros** (one button for several actions), **show files** and **backups**. |
| **Soundcheck** tab | Records every channel to its own WAV file. It can then play the recording back into the channels instead of the microphones (**virtual soundcheck**). |
| Top right, on every page | The next cue with its own **GO** button, and a red **REC** light while recording. |
| Yellow bar under the tabs | Shows whenever the channels are playing a recording instead of the microphones. It has a **Back to live inputs** button. |
| Scenes tab | Each scene and snapshot now has an **eye** button. It shows what a recall would change before it changes anything. |
| Keyboard | **F1 to F12** run the macros given those keys. |

## 1. Cue list

1. On **Scenes**, save three snapshots with different faders (for example Worship, Sermon, Altar call).
2. On **Show**, press **Add cue**. It suggests the next snapshot that is not in the list yet. Add a note, then press **Save cue**. Do the same for the other two.
3. Press **GO**. The first cue fires, **NOW** shows it and **NEXT** shows the second.
   - [ ] The faders move to the first snapshot.
4. Press the **eye** next to Preview (or on any cue).
   - [ ] A list shows each channel that would change, with its old and new value.
   - [ ] Nothing moves until you press **Go to this cue**.
5. Press **GO** again, then the back arrow.
   - [ ] The back arrow goes back one cue and fires it again.
6. Press GO from the top bar while on the **Mixer** tab.
   - [ ] It works from any page.
7. Change a cue (pencil), move it up or down, and delete one.
   - [ ] **What it changes** and **Its own fade** affect only that cue, not the snapshot.

## 2. Macros

1. On **Show**, press **New macro**. Name it "Choir off", pick a colour and the key **F1**.
2. Add steps: Mute CH19, Mute CH20, then Set a fader on the pulpit to 0 dB. Save it.
3. Press the macro button.
   - [ ] Both choir channels mute and the pulpit fader moves.
4. Press **F1** on the keyboard, with no text box selected.
   - [ ] It runs again.
5. Give a cue that macro (Change cue > And runs) and GO to it.
   - [ ] The snapshot recalls and the macro runs.
6. Add a step for a channel, then delete a bus the macro uses, and run it.
   - [ ] A message says which step could not be done, and the other steps still run.

Other steps you can use:
- turn a send on or off;
- recall a snapshot or scene;
- go to the next cue;
- start or stop recording;
- play the recording into the channels, or go back to the live inputs;
- clear every solo;
- wait a few seconds.

## 3. Show files and backups

1. On **Show**, press **Save show…** and save to a USB stick, for example "Easter Sunday.nexshow".
   - [ ] The show card shows the name and where it was saved.
2. Change a few faders, then press **Open show…** and pick that file.
   - [ ] A card lists what is inside: the mix, scenes, snapshots, cues, macros and presets.
3. Press **Keep the live mix**.
   - [ ] The faders do not move. The scenes, cues and macros come from the file.
4. Open it again and press **Open with its mix**.
   - [ ] The faders go back to how they were when the show was saved.
5. Press **Backups**. Every open, every 10 minutes of changes and every start of NEX-Audio is there.
   - [ ] **Go back to this** on an older backup brings the desk back to that point. A new backup of the desk as it was is saved first.
6. Try to open any other file renamed to .nexshow.
   - [ ] NEX-Audio refuses it and nothing changes.

What a show file holds:
- **Included:** the mix, scenes, snapshots, presets, cue list and macros.
- **Not included:** this PC's own settings (the X32's address, the failsafe and the recorder folder).

So a show file can go to another church's NEX-Audio.

## 4. Recording

1. On **Soundcheck**, check the disk line: it says how much room there is and about how many hours that is.
   - A second drive is best for long services. Use the **…** button under Recorder settings to pick its folder.
2. Leave all 32 channels armed (red **REC** on each). Keep **Also record the main mix** ticked.
3. Type a name (optional) and press **Record**. Play or sing into a few mics for a minute.
   - [ ] The clock runs and the red **REC** light shows at the top of every page.
4. Press **Stop**.
   - [ ] The recording appears on the right with its length and size.
   - [ ] The folder has one WAV file per channel, named after the X32's channel names, plus "Main mix.wav". They open in Reaper, Audacity or any recording program.
5. Unplug the USB cable for a second while recording (only in a test, never in a service).
   - [ ] What was recorded up to then is kept.

**Taken:** leave it on **Straight from the X32**. Only those recordings can be played back into the desk. **After EQ and dynamics** is for a finished-sounding multitrack.

## 5. Virtual soundcheck

1. Press the play button on a recording.
   - [ ] The yellow bar appears: the channels are playing the recording, not the microphones.
   - [ ] Each channel chip says **PLAYBACK**. The meters move with the recording.
2. Mix as if the band were playing: EQ, compressors, effects, monitor mixes. Save a snapshot when it sounds right.
3. On the pastor's channel, press **KEEP LIVE**.
   - [ ] That channel hears its microphone again while the rest play the recording (useful for talkback).
4. Use −10, +10, the slider and the restart button to move around. Tick **Play the recording round and round** to loop it.
5. Press **Back to live inputs** in the yellow bar.
   - [ ] Every channel hears its microphone again and the yellow bar goes.

**Before every service, check the yellow bar is gone.** A macro with the step "Back to the live inputs" on a key is a good safety button.

Recordings from another program (for example Reaper on the X32's card) can be played too:
1. Press **Bring in…** and pick the folder of WAV files.
2. They are taken in channel order by file name: the first file plays into channel 1.
3. They must be at the same sample rate as the desk (48 kHz).

## Write down

| Check | Result |
|---|---|
| Longest recording made (minutes) | |
| Any "the disk fell behind" or "gaps" message? | |
| Clicks counter before and after a 30-minute recording | |
| Files open in another program? | |
| Show file opened on a second PC? | |
