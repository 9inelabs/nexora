# NEX-Audio: bus and main processing, test guide

This covers Phase 3 of making NEX-Audio a complete desk: a **left-centre-right main** with its own centre strip, and the last missing
processing on buses. Try it on the sound PC with the X32 connected. Everything also works on the test device (Setup > Driver > Test device).

Most bus processing was already there before this phase: every mix, group, effect return, matrix and the main already had trim,
polarity, delay, low and high cuts, drive, inserts before and after, the 6-band EQ with up to 4 dynamic bands, the compressor and the fader.
This phase adds what was still missing.

## What is new

| Where | What it does |
|---|---|
| **Main speakers** (Channel tab > Main > Sends page, under Routing) | Choose **Left and right** (as before) or **Left, centre and right (a centre cluster)**. |
| **Centre strip** (Mixer, next to Main) | Shows only in left-centre-right. It has its own gate, EQ, compressor, effects and fader, and its own MUTE. It has no pan, since it is one speaker feed. |
| Panning in left-centre-right | A mono channel, effect return or group pans smoothly across left, centre and right: centred goes to the centre speaker only, hard left to the left only, halfway between goes to both, at the same loudness. |
| **Pan across left, centre and right** (each mono strip's Routing) | On by default. Switch it off for a strip that should stay on the left and right speakers only (for example the band, so the centre cluster carries just the voices). |
| Stereo strips | Always go to left and right only. They never feed the centre. |
| **Routing** tab | A **Centre** column next to Main, and a Centre row. Tap a crossing to put a strip in or out of the centre. A stereo strip shows no crossing there. |
| **Outputs** tab | A new source **Main centre** for the centre cluster's speaker output. |
| **Gate / ducker on every bus** | Every mix, group, effect return, matrix, the main and the centre now has the gate page (it says **Gate / ducker** on buses). The usual use is a **ducker**: dip the music or a wedge mix while the pastor's mic is loud. |
| **Bus trim up to +60 dB** | A bus's trim now reaches +60 dB (channels stay at ±24), for buses fed from very quiet sources. |
| Copy and paste | Pasting a gate now works onto any bus, not just channels. |

## How the centre reaches the other feeds

- **Mono / sub**: gets left, right and the centre after the centre's fader, so the subs hear the whole mix.
- **Stream** (when it follows the main): gets the centre folded equally into its left and right, so the online audience hears the voices
  even though they are not in the left and right speakers.
- **Headphones / solo** with nothing soloed: you hear the centre folded into both ears, the same way.
- **Delay line-up**: if the centre has a plug-in or limiter that adds delay, NEX-Audio delays left and right to match (and the other way round), so the speakers stay in time.

## 1. Turn on the centre cluster

1. Channel tab, pick **Main**, open **Sends**. Under Routing, set **Main speakers** to **Left, centre and right**.
2. On the Mixer, a **Centre** strip appears to the left of Main. On the Outputs tab, set the centre speaker's output to **Main centre**.
3. Talk on a centred mic: it should come only from the centre speaker. Pan it hard left: only the left speaker. Pan it halfway: left and centre together, no louder or quieter overall.
4. Play a stereo track: it should be on left and right only, never the centre.

## 2. Keep the band out of the centre

1. Routing tab: on the **Centre** column, tap the crossings for the band channels (or switch off **Pan across left, centre and right** on each one).
2. The band should now be on left and right only, while voices still go to the centre.

## 3. Process the centre on its own

1. Select the **Centre** strip and add a little EQ or a compressor. Only the centre speaker should change.
2. Pull the centre fader down 3 dB: the centre speaker drops, left and right stay.
3. Add a look-ahead limiter on the centre. A voice that is in all three speakers should still sound like one clean voice, with no echo or comb between them.

## 4. Check the stream and the subs

1. On the Stream tab, with the stream following the main, talk on a centred mic: the stream should carry the voice in both ears.
2. Listen to the mono / sub output: it should carry the whole mix, centre included.

## 5. Duck a bus

1. Pick a wedge mix or the music playback group, open **Gate / ducker**, switch it **On** and pick **Ducker**.
2. Under **Listens to**, pick the pastor's mic. Set threshold around -30 dB and range around 10 dB.
3. When the pastor talks, that mix should dip by about 10 dB, and come back up when they stop.

## 6. Switch back to left and right

1. Set **Main speakers** back to **Left and right**. The Centre strip disappears, any output that played **Main centre** is switched off,
   and the mono channels go back to left and right as before. The centre's settings are kept for next time.
2. Saved scenes and routing layouts remember the main speakers choice. Older show files open as left and right.
