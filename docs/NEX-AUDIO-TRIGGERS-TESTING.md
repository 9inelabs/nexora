# NEX-Audio: drum triggers

This adds drum triggers to NEX-Audio. A drum mic listens, and every hit plays a sample (a WAV of a drum) as hard as the
drummer hit. The sample can replace the real drum or be blended with it. It is built for a kit that sounds thin, boomy
or full of spill in the room, so the stream and the room get a steady, tight drum sound every Sunday.

Tick each box as you go. If a box fails, note the time: Setup > Diagnostics and its event log will show what happened then.

## What is new

- **Trigger page** on every channel (Channel > Trigger, in green). It has an **On** button, a hit light, an
  **A/B: natural mic** button to hear the real drum alone, and the **hit history**: the last 32 hits as bars, as loud as
  each hit. Solid bars fired a sample, with the layer number on them. Hollow bars were too soft and did not fire, so a
  missed ghost note is easy to see. The dashed line is the threshold.
- **Threshold and Hold** set how hard a hit must be to fire, and how long after a hit new hits are ignored (so one hit
  never fires twice). **Learn from 8 hits** sets both for you: play eight hits, the softest included, and it is done.
- **Spill reject.** The trigger listens only between **Listen from** and **Listen to** (for a snare, 150 Hz to 4 kHz;
  for a kick, 40 Hz to 3 kHz), so the kick does not fire the snare and the hi-hat does not either. **Listens to** can
  pick another channel (a snare-bottom mic or a trigger pad) to do the listening.
- **Samples, up to 4 layers.** Soft hits play the first layer, the hardest hits the last. Each layer can have up to 4
  takes, played in turn (**Round robin**), so the drum never sounds like a machine gun.
- **Blend and feel.** **Blend** (0% natural mic, 100% sample), **Dynamics** (how much hit strength changes the sample's
  level), **Sample level**, **Tune** (in semitones) and **Natural gate**: with some natural mic in, the mic opens only for
  each hit (for **Mic open** ms), so cymbal spill between hits stays out.
- **Starter kit.** Kick, Snare, Rack tom and Floor tom, 4 layers each, 2 takes per layer, made by NEX-Audio and on every
  PC. One press loads a drum. **Presets > Drums** also has Kick, Snare, Rack tom and Floor tom trigger presets.
- **Your own samples.** **Add WAV files...** copies WAVs into the drum library under a kit name. They are saved inside
  the show file, so a show opened on another PC sounds the same.
- **Safe when a sample is missing.** If a sample file is not on this PC, the trigger plays the natural mic at full level
  instead (never silence), the page shows an amber warning, and the event log says which channel. A file deleted
  while the desk is running keeps playing from memory until the next start, so a service is never cut short.
- **On the mixer**, each triggered channel has a green **T** lamp that flashes on every hit. The channel's Input tile
  has a **TRIG** button.
- Scenes can recall **triggers** on their own (Custom scope). Copy and paste has a **Drum trigger** part.

Everything after the trigger (EQ, gate, compressor, sends, stream) hears the result, so the stream gets the
triggered drum too.

## Before you start

- [ ] Install the new NEX-Audio on the sound PC and open it. NEXORA does not need updating for this.
- [ ] Setup > Driver: start the X32 driver (or the test device for Part A).
- [ ] Setup > Diagnostics shows **Everything is healthy**.

## Part A: on the test device (no drummer needed)

### A1. The page

1. [ ] Mixer: pick the snare channel. Open **Channel**, then **Trigger** in the left rail.
2. [ ] Press **On**. The amber note says to pick a sample.
3. [ ] Under **Starter kit**, press **Snare**. Four layers fill with "Snare soft", "medium", "hard" and "full", two takes each.
4. [ ] The amber note goes away. The rail shows "Snare soft 1 · 70% sample".
5. [ ] Press **1 layer**, then **4 layers** again. No sample is lost (the samples of removed layers join the layers that stay).

### A2. Your own samples

1. [ ] Type a kit name (for example "Sunday kit") and press **Add WAV files...**. Pick two WAVs.
2. [ ] They appear under **Your samples** with their length.
3. [ ] In **Hardest hits**, use **Add a sample...** to add one of them. Remove it again with its **×**.
4. [ ] Try to remove a starter sample from **Your samples**: it is not offered (the starter kit stays).
5. [ ] Show > Save show. Open that show file on another PC with NEX-Audio: the same samples are there and play.

### A3. Missing sample

1. [ ] Add one of your samples to a layer. Close NEX-Audio.
2. [ ] Rename that WAV in `audio/drums/<kit>` (in the NEX-Audio data folder). Open NEX-Audio.
3. [ ] The Trigger page shows "1 sample is missing on this PC, so the natural mic is playing".
4. [ ] Setup > Diagnostics: the event log has a PROBLEM line naming the channel.
5. [ ] Rename the file back and press **Add WAV files...** (or restart): the log says the trigger has all its samples again.

## Part B: with the drummer, at a rehearsal (never first in a service)

Do the kick first, then the snare, then the toms. Keep the main fader low the first time a trigger goes on.

### B1. Set it up

1. [ ] Kick channel: Trigger page, **On**, **Starter kit > Kick** (or Presets > Drums > "Kick drum, triggered").
2. [ ] Press **Learn from 8 hits**. Ask the drummer for eight kick hits, including the softest they will play. The page counts them, then sets
   Threshold and Hold, and the event log says what it learned.
3. [ ] Ask for soft, medium and loud hits. The hit history shows bars at different heights, and the layer number
   goes up as the hits get harder.
4. [ ] Ask for very soft ghost notes on the snare. If they show as hollow bars but should play, lower **Threshold**
   by 2 to 3 dB until they fire.
5. [ ] If one hit ever shows two bars close together (look at "last gap" under the history), raise **Hold** a little.

### B2. Spill

1. [ ] Snare trigger on. Ask for kick only, then hi-hat only, then cymbals only. **The snare's hit history must stay
   still.** If the kick fires it, raise **Listen from**; if the hi-hat fires it, lower **Listen to**.
2. [ ] Toms: ask for loud snare and cymbals. The tom triggers stay still. Raise the threshold if they do not.
3. [ ] With **Natural gate** on and Blend at 70%, solo the snare in headphones: between hits you hear little cymbal.

### B3. Sound

1. [ ] Blend at 0%: only the real drum. At 100%: only the sample. Pick the amount that sounds best (60 to 80% is usual).
2. [ ] Press **A/B: natural mic** to hear the real drum alone, then off again. It is easy to tell them apart.
3. [ ] **Tune** the sample to suit the room (a semitone or two). **Sample level** matches it to the other drums.
4. [ ] Fast fills and rolls: every hit is heard, with no flams added and no machine-gun sound (round robin).
5. [ ] Listen for delay: the triggered drum must sit exactly with the real one (the trigger fires within 2 ms).
6. [ ] Mixer: the green **T** lamps flash with the hits on each triggered channel.

### B4. Scenes and presets

1. [ ] Save the triggers in a scene. Change the snare threshold. Recall the scene with a Custom scope of only
   **triggers**: the threshold comes back and nothing else changes.
2. [ ] Save the snare channel as a preset, and load it on another snare channel.

## Part C: the first services

1. [ ] **Stream only first.** For the first service, use the triggers only on the stream mix (or with Blend low in the
   room). Listen to the recording afterwards: no missed hits, no double hits, no spill hits.
2. [ ] Second service: if the recording was clean, use the triggers in the room as well.
3. [ ] After each service, check Setup > Diagnostics: no trigger warnings.

If anything sounds wrong mid-service, press **On** to turn the trigger off. The real mic plays at once.
