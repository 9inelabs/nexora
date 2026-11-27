# NEX-Audio: extra processing and the resource page, test guide

This covers Phase 4 of making NEX-Audio a complete desk. Everything here is NEX-Audio's own design, with its own names and look.
Nothing copies another maker's strips, racks or algorithms. Try it on the sound PC with the X32 connected. Everything also works on
the test device (Setup > Driver > Test device).

## What is new

| Where | What it does |
|---|---|
| **Compressor characters** (Channel tab > Compressor, and the rack compressor) | Two new characters next to Clean, Opto, FET and Glue. **Vintage** is a classic VCA sound: firm, it listens to the average level, it lets go quicker after deep reduction, and it adds a touch of warmth. **Limit** is a fast FET-style limiter: at least 20:1, very quick, with colour as it works. |
| **Hears: Auto, Peaks, Average** (Compressor page, next to Listens to) | Peaks catch every hit quickly. Average (RMS) follows how loud it sounds, which is smoother on voices and mixes. Auto lets the character choose. |
| **8-band EQ on buses** | Every mix, group, effect return, matrix, the main, the centre, mono and stream now has 8 EQ bands (channels keep 6). An older show's 6-band bus EQ gains two flat bands (160 Hz and 6.3 kHz), so it sounds exactly as before. The dynamic EQ (up to 4 bands) is still there under it. |
| **Colour strip** (rack: Add > Colour strip) | A whole extra strip as one insert: low and high cut, a **Tube**, **Tape** or **Iron** (transformer) stage with drive, a 4-band EQ, a compressor in any character, and a gate or ducker that can listen to any channel. Up to **36** in the show. |
| **Effects rack** (rack: Effects rack: Dynamic EQ (6 bands) / Multiband (6 bands)) | A 6-band dynamic EQ, and a 6-band multiband that **compresses** or **expands** (expand turns quiet parts down further, for noise or spill). The rack has **8 slots**: a mono strip takes one, a stereo one (main, groups, effect returns) takes two, so 8 mono or 4 stereo. |
| **Processing nodes** (rack: EQ node, Gate / ducker node, and the compressor, multiband, de-esser and limiter) | Drop an extra EQ, compressor, multiband, de-esser, limiter, gate or ducker into any strip, before or after its EQ (the **Before EQ** button). The gate / ducker node can listen to any channel. Up to **64** in the show. |
| **Pitch shifter** | Moves the whole sound up or down by up to 12 semitones, with a fine control in cents. Use it for an octave, a harmony, or a few cents mixed in for width. |
| **Enhancer** | Brings speech forward. It lifts the top of a voice above the tune frequency and adds a little edge, so words stay clear without turning the voice up. **Body** adds warmth at 180 Hz. |
| **More reverbs** | Chamber (dense and smooth on voices), Church (big stone room with a long, dark tail) and Ambience (just a sense of space), as well as room, hall and plate. |
| **TAP** (on a delay in the rack) | Tap two or more times in time with the song and the delay follows the tempo. A pause of over 2 seconds starts the count again. |
| **Setup > Resources** | Shows how much processing headroom is left, with a plain verdict and advice, the shared pools (colour strips, rack slots, nodes, effects, plugins), and which strips use the most of the processor. |

When a pool is full, NEX-Audio says so and does not add the item. Remove one first.

## 1. Try the compressor characters

1. Channel tab, pick a vocal, open **Compressor**, switch it on.
2. Try **Vintage** on the pastor's mic with **Hears: Average**: it should sound smooth and even.
3. Try **Limit** on a playback channel: peaks should stop at the threshold whatever the ratio says.

## 2. The 8-band bus EQ

1. Pick a wedge mix and open **EQ**. You should see 8 bands.
2. Open an older show file. Its bus EQ should sound the same as before, with two extra flat bands.

## 3. A colour strip on the lead vocal

1. Pick the lead vocal, open **Effects**, choose **Colour strip** at the bottom and press **Add**.
2. Try **Tube**, **Tape** and **Iron** with the drive at about 8 dB. Each one should add a different colour without making the voice much louder.
3. Set **Compressor** to **Vintage** and **Gate / ducker** to **Gate**. The graph's top line shows what is switched on.
4. Add colour strips until the 37th is refused. The message should say all 36 are in use.

## 4. The effects rack

1. On a wedge mix, add **Effects rack: Dynamic EQ (6 bands)**. Give band 4 a range of -6 dB at about 2 kHz: it should only dip when the singer is harsh.
2. On the main, add **Effects rack: Multiband (6 bands)**. With no band over its line, the sound should not change at all.
3. On the main again, set **Mode** to **Expand**. The quiet noise between songs should drop.
4. Fill the rack (for example four stereo items) and check that a fifth is refused.

## 5. A ducker node under the pastor

1. On the playback channel, add **Gate / ducker node**, set **Mode** to **Ducker** and **Listens to** the pastor's mic.
2. Set the threshold to about -35 dB and the range to about 12 dB.
3. When the pastor speaks, the playback should step back by about 12 dB, then return.

## 6. The new effects

1. Put a **Pitch shifter** on an effect return at +12 semitones, mixed at 25%: you should hear a soft octave above.
2. Put an **Enhancer** on the pastor's mic at 3 kHz and 30%: the words should be clearer without the voice getting louder.
3. Add **Reverb: church** to an effect return and compare it with **ambience** and **chamber**.
4. On the delay, press **TAP** in time with a song. The tempo knob should follow.

## 7. The resource page

1. Setup > **Resources**. With the usual service mix, the verdict should say **Plenty of headroom**.
2. Add several church reverbs and colour strips. **What uses the most** should list those strips first.
3. If the verdict says **Getting close** or **Very close**, bypass or remove the heaviest items, or raise the buffer on the Driver page, before the service.

Nothing in this phase has been tried on the real X32 or the church PC yet. The test device shows the load of this PC, so the numbers on
the sound PC will be different.
