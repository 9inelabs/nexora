# NEX-Audio: routing and patching, test guide

This covers the new **Routing** tab: Phase 1 of making NEX-Audio a complete desk. Try it on the sound PC with the X32 connected.
You can try everything on the test device too (Setup > Driver > Test device), but you need the X32 to hear the direct outs and alternate inputs.

## What is new

| Where | What it does |
|---|---|
| **Routing > Bus routing** | One grid of every channel and bus against every bus it can feed. Tap a crossing to make or remove a route. Groups can feed groups, mixes can feed mixes, effects can chain, and groups can feed a reverb. Any route that would feed back into itself is blocked and shows a small red cross. |
| **Signal trace** (beside the grid) | Tap any name on the grid to light up everything its sound passes through and everything that feeds it, and which outputs play it. |
| **Delay line-up** (beside the grid) | Shows which plugins and limiters add delay. NEX-Audio now delays every faster path into a bus to match, wherever the slow one is (before, only between channels and between effects and groups). |
| **Routing > Inputs and direct outs** | Each channel picks its X32 card input and an **alternate** (a spare mic or a playback track). Switching keeps the channel's processing. Each channel has a **direct out**, taken from a point you choose (IN, PRE EQ, POST EQ, PRE, POST), and can be sent to any output. **All to alternate** and **All back to main** switch every channel that has an alternate. |
| **Routing > Routing layouts** | Save the routing (buses, inputs, routes, sends, direct outs, output patch) without the sound. Compare one with the desk using the eye, then recall it. **Start again** is a built-in factory layout. Recalling can be undone. |
| **Routing > Test signal** | A sine tone, pink noise or white noise at any level up to -6 dB. It plays on any output set to **Test signal** and, when you start it, into one bus. It asks before it starts, and it is always off when NEX-Audio starts. |
| Delays | Channel, bus and output delays now go up to **1.3 seconds** (for delay speakers in a long hall and lining up video). |

## 1. Bus routing and loop protection

1. Open **Routing > Bus routing**. Hover over crossings and read the line under the grid.
   - [ ] Channels already going to groups, sends to reverbs and mixes show as coloured blocks with their send level.
2. Tap the crossing of **Drums** (a group row) and **Band** (a group column).
   - [ ] Drums now goes through Band. Drums stops going to the main mix by itself, so it is not heard twice.
3. Now tap the crossing of **Band** (row) and **Drums** (column).
   - [ ] It shows a red cross and the message says it would make a feedback loop. Nothing changes.
4. Make **Mix 3** feed **Mix 5** (for example vocal wedges into the side fills). Then try **Mix 5** into **Mix 3**.
   - [ ] The first works, the second is blocked.
5. Tap a send block to switch it off, then tap it again.
   - [ ] It goes to an outline, then comes back at the same level.
6. Make a new send on the grid.
   - [ ] It starts at -10 dB, quiet enough to bring up by ear on the Channel page or bus tag.

## 2. Signal trace

1. Tap the name **Drums** on the grid.
   - [ ] Its column and rows light up, the drum channels feeding it light up, and Band, Main and anything after them light up.
   - [ ] The trace panel lists what feeds it, where it goes, which outputs play it, and its delay.
2. Tap **Drums** again, or **Clear**.
   - [ ] The trace goes away.

## 3. Delay line-up

1. Put a **Limiter** (or a plugin with delay) on the **Drums** group.
   - [ ] **Delay line-up** shows "Drums adds 1.0 ms" and the main mix is 1 ms late.
2. Listen to the overheads with one tom on the main mix through the group and a tom mic straight to the main.
   - [ ] No phasey or comb-filter sound: the straight path waits to match.

## 4. Inputs, alternates and direct outs

1. Open **Routing > Inputs and direct outs**. Set **Pastor** to input 1 and its **Alternate** to the input with a spare handheld.
2. Talk into each mic and flip **Use alt** for the Pastor.
   - [ ] The channel switches mic at once. Its EQ, compressor and fader stay as they were.
3. Give two or three more channels an alternate. Press **All to alternate**, then **All back to main**.
   - [ ] Only channels with an alternate switch. **Undo** brings the last switch back.
4. Set the Pastor's **Direct from** to **IN** and **Direct out to** an output wired to a recorder.
   - [ ] The recorder gets the mic before any EQ or fader. Moving the fader does not change it.
5. Change **Direct from** to **POST**.
   - [ ] Now the fader changes the recorder level. Muting the channel silences the direct out.
6. Set a channel delay to 1000 ms.
   - [ ] You can hear the one-second delay.

## 5. Routing layouts

1. Open **Routing > Routing layouts**. Type "Sunday morning" and press **Save the routing as a layout**.
2. Change some routing: take a channel out of a group, change an input, remove a matrix. Also change some EQ and a fader.
3. Press the **eye** on Sunday morning.
   - [ ] The list shows only routing changes (inputs, groups, sends, buses, outputs). EQ and faders are not in it.
4. Press **Recall now**.
   - [ ] The routing comes back. The EQ and faders you changed stay as you left them.
5. Press **Undo** in the header.
   - [ ] The routing goes back to before the recall.
6. A channel marked **recall safe** (Channel page) keeps its routing when a layout is recalled.
7. **Start again (factory routing)** cannot be deleted. Recalling it puts channel N back on input N, to the main mix, and every output passes its input straight through.

## 6. Test signal

Turn the speakers down first.

1. Open **Routing > Test signal**. Choose **Pink noise**, level -30 dB, **Play into** Side fills. Press **Start**.
   - [ ] A yellow box asks before anything plays. **Cancel** plays nothing.
2. Press **Play it**.
   - [ ] Pink noise comes out of the side fills, through their EQ and limiter. A red dot shows on the Routing tab and **TEST SIGNAL ON** shows on the page.
3. Try to set the level above -6 dB.
   - [ ] It stops at -6 dB.
4. On **Outputs**, set an output to **Test signal**.
   - [ ] It plays the same signal all the time (as the old 1 kHz tone did).
5. Press **Stop the test signal**, then close NEX-Audio fully and open it again with the signal playing.
   - [ ] It never starts by itself.

## Known limits

- Not yet tried on the real X32, the real interface or real plugins: the 243 automatic tests run against the built-in test device.
- The grid sets routes and switches sends on and off. Send levels are still set on the Channel page or with the bus tags.
- The X32 gain and phantom shown on the Channel page still belong to the X32 channel with the same number, not to the chosen input.
- Next in the plan: Phase 2 (groups, mute and solo).
