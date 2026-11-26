# NEX-Audio Phase 1: test guide for the X32

Phase 1 proves the link between NEXORA and the X32. All 32 channels go from the X32 into NEX-Audio over USB and come straight back. Along the way you can see every level, choose what each output plays, measure the real delay, and read the X32's channel names over the network.

There is no mixing yet. Buses, effects and the full failsafe come in Phase 2. **Do not use NEX-Audio for a real service yet.** Test it in a rehearsal or on a weekday.

Allow about 2 hours, plus one full rehearsal for the long test.

---

## What you need

- The church PC that will run NEX-Audio, with the new NEXORA installed (Settings > Updates > Update NEXORA).
- The X32's USB cable (USB-B on the X32 card, USB-A on the PC). Plug it into a port **on the back of the PC**, not a hub or a front port.
- The X32's network cable going to the church switch, the same network as the PC.
- The **X-USB ASIO driver** from Behringer's X32 downloads page, installed on the PC. Restart the PC after installing.
- One short XLR cable (for the latency test).
- Headphones, or a small speaker you can plug into one X32 output, to listen.
- A user account with the **NEX-Audio (sound engineer)** role. An admin can tick it in Settings > Users.

## Keep your normal X32 setup safe

Before changing anything on the X32, **save the current scene** (Scenes > Save). At the end of the test you load it again and everything is back as it was.

---

## 1. Open NEX-Audio

1. Sign in on the NEX-Audio PC and add the **NEX-Audio** section.
2. Open the **PC check** tab. Every line says OK, Improve or Fix.
   - Fix any **Fix** line before going further.
   - Note any **Improve** lines on the sheet at the end. Common ones: set the power plan to High performance, and turn USB selective suspend off.

## 2. Start the sound

1. Open the **Driver** tab. Choose the X32's driver (its name contains X-USB). Leave the buffer at **64**.
2. Press **Start sound with these settings**.
3. At the top, the engine should say **Running**, **48 kHz**, **32 in · 32 out**, **64 samples · 1.3 ms**.

If it says Problem, read the message. It usually means the X32 is off, the USB cable is out, or the driver is not installed.

## 3. Link to the X32 over the network

1. Open the **X32 link** tab and press **Find X32 on the network**. If nothing is found, type the address shown on the X32 (Setup > Network) and press **Link**.
2. The X32's name, model and firmware appear, and the 32 channel names and colours fill in.
3. Rename a channel on the X32 desk. Within about 10 seconds the new name shows in NEX-Audio.

## 4. Check every input arrives

On the X32, open **Routing > Card Out** and set the four blocks to the local inputs: **1–8 to Local 1–8**, **9–16 to Local 9–16**, **17–24 to Local 17–24**, **25–32 to Local 25–32**. (If your inputs come from a stage box, choose the AES50 blocks it uses instead.)

1. Open the **Meters** tab.
2. Speak into each mic, or play each instrument. The meter with the same number moves in the top row ("From the X32"), with the X32's channel name under it.
3. The bottom row ("Back to the X32") should match the top row, because every output plays the input with the same number.

## 5. Listen to the sound coming back

On the X32, open **Routing > XLR Out** (also called Out 1–16) and set **outputs 13–16** to **Card 13–16**. If your X32 names the option slightly differently, pick the Card option for that block of four. Outputs 13 to 16 now play what NEX-Audio sends back, untouched by the X32's faders and effects.

1. Plug headphones (through a headphone amp) or a small speaker into **output 13**.
2. You should hear input 13 clearly, with no clicks or crackles.
3. Open the **Outputs** tab and set **output 14** to **Test tone**. Output 14 plays a steady 1 kHz tone at a safe level. Set it back to **Input 14** afterwards.

## 6. Measure the real delay

1. Plug the short XLR cable from **X32 output 16** into **X32 input 32**. Make sure output 16 is not connected to any speaker: the clicks are loud.
2. Set input 32's gain to about the middle.
3. Open the **Latency test** tab. Choose **output 16** into **input 32** and press **Measure**.
4. Write down the result. Under 6 ms is excellent, and up to 10 ms is fine for the stream, mains and side fills.
5. In the Driver tab, change the buffer to **128**, start the sound again and measure again. Write that down too. Then go back to 64.

## 7. Can it take a knock?

1. **Close NEXORA's window** while the tone plays on output 14. The tone must carry on. Open NEXORA again and NEX-Audio shows the same state.
2. **Pull the X32's USB cable** for 5 seconds, then plug it back. NEX-Audio shows Problem, then goes back to Running by itself within about 10 seconds.
3. **Restart the PC.** Open NEXORA. The sound starts again by itself, without pressing Start.

## 8. The long test

Leave NEX-Audio running through a full rehearsal, with your headphones on output 13 from time to time.

At the end, write down the **Clicks** number and the **Engine load** (and its peak) from the top of the screen. The Clicks count should be 0, or very close to it.

## 9. Put the X32 back

Load the scene you saved at the start. NEX-Audio can stay open; with the X32's normal routing back, nothing it sends is heard.

---

## Results sheet

| Check | Result | Notes |
| --- | --- | --- |
| PC check: any Fix or Improve lines? | | |
| Driver opened at 64 samples, 48 kHz, 32 in and 32 out | Pass / Fail | |
| X32 found and linked, names and colours correct | Pass / Fail | |
| A rename on the desk showed in NEX-Audio | Pass / Fail | |
| All 32 inputs move their meter | Pass / Fail | Which ones did not? |
| Clean sound on output 13 | Pass / Fail | |
| Test tone on output 14 | Pass / Fail | |
| Measured delay at 64 samples | ____ ms | |
| Measured delay at 128 samples | ____ ms | |
| Sound kept going with NEXORA closed | Pass / Fail | |
| Came back by itself after the USB cable was pulled | Pass / Fail | How many seconds? |
| Came back by itself after a PC restart | Pass / Fail | |
| Clicks after a full rehearsal | ____ | |
| Engine load (average and peak) | ____ % / ____ % | |

## What to send back

- The results sheet.
- A photo or screenshot of the NEX-Audio screen after the long test.
- The log files from `%LOCALAPPDATA%\NexoraData\logs` on the NEX-Audio PC (paste that into File Explorer's address bar).
