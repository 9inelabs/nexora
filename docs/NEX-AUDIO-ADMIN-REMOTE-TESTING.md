# NEX-Audio: the admin remote (the whole desk from a laptop), test guide

NEX-Audio on another laptop on the church network can now run the sound PC's **whole desk**, every page,
exactly as at the sound PC. The sound itself never leaves the sound PC: the laptop is another screen on the same engine.

The musicians' phone and tablet remote is not changed. It keeps its own page, its own PIN and its mixes.

## What is new

| Where | What it does |
|---|---|
| **Laptops can run the whole desk** (Remote tab, on the sound PC) | Switch it on and set a **laptop PIN** (4 to 8 digits, not the same as the phones' PIN). Laptops can then connect on port 7411. A laptop gets everything this PC's own screen gets: mixer, channel strips, routing, groups, scenes and snapshots, show, cues and macros, recorder and virtual soundcheck, plugins, failsafe, meter bridge and setup. |
| **Laptops on the desk now** | Lists each laptop with its name, who signed in on it, its address and when it joined. **Send away** closes that laptop's link, and it has to connect again with the PIN. |
| **Run a sound PC from this laptop** (Remote tab, on the laptop) | Press **Find** and the sound PC shows up by name, or type its address. Type the laptop PIN and press **Connect**. The whole NEX-Audio window now runs the sound PC's desk, and a gold **ADMIN REMOTE · SOUND-PC** tag sits at the top. **Disconnect** takes the laptop back to its own sound. |
| **Both screens stay in step** | A change on the laptop happens on the sound PC straight away, and the sound PC's own screen follows. A change at the sound PC shows on the laptop. |
| **Shows and drum samples** | **Save the show** on the laptop saves it on the sound PC (a copy stays there) and brings it back to the laptop. **Open a show** and **Add WAV files** send the file from the laptop to the sound PC first. |
| **Wi-Fi drops** | The sound carries on at the sound PC. The laptop shows **Not linked**, with dashes in place of the levels, and links again by itself when the network comes back. It remembers the sound PC, so it links again the next time NEX-Audio opens on it too. |

### Safety

- **Nothing reaches a laptop without the laptop PIN.** The PIN itself never crosses the network: the sound PC sends a random number and the laptop proves it knows the PIN by signing it.
- **Five wrong PINs** from one laptop and it has to wait a minute. A laptop with a wrong PIN stops trying until someone presses Connect again, so it cannot lock itself out.
- **Changing the laptop PIN, or switching the admin remote off, sends every laptop away.**
- A laptop **cannot shut down the sound engine** (only the sound PC can). Start sound and Stop sound do work from the laptop.
- The sound PC's event log (Setup > Diagnostics) notes each laptop that joins or leaves, and each wrong PIN.

### Things that stay at the sound PC

- The **driver's own settings window** and a **plugin's own window** open on the sound PC's screen. The plugin's knobs on the Channel page work from the laptop.
- **Recording folders** and **importing a folder of WAVs** into the virtual soundcheck: type the folder on the sound PC, or pick it at the sound PC. The "…" buttons are hidden on a laptop.
- **Open the log folder** and **Shut down the engine** are hidden on a laptop.
- **PC check** checks the laptop it runs on, and says so.
- **Follow the screens (NEXORA)** triggers are run by the sound PC. The laptop does not pass the screens on too, so a trigger never fires twice.

## Before you start

- Update **both** PCs to the same NEX-Audio version. If they differ, the laptop still connects and shows a warning.
- The laptop signs in to NEX-Audio as usual (it is a NEXORA terminal, so it needs the pairing code from the Live Output PC the first time).

## 1. Switch it on at the sound PC

1. On the sound PC, open the **Remote** tab. Switch on **Laptops can run the whole desk**.
2. Type a laptop PIN, for example 2468, and press **Save**. The chip says **On** and the line under it shows this PC's address.
3. Press **Let laptops in through Windows Firewall** once. Windows asks for permission.
4. Try to save the same PIN as the phones' engineer PIN. NEX-Audio refuses it.

## 2. Connect a laptop

1. Install NEX-Audio on the laptop and sign in.
2. Open the **Remote** tab. Under **Run a sound PC from this laptop**, press **Find**. The sound PC shows up by name. (If it does not, type the address shown on the sound PC.)
3. Type a wrong PIN and press **Connect**. It says **That laptop PIN is not right** and stops there. On the sound PC, Setup > Diagnostics shows the wrong PIN.
4. Type the right PIN and press **Connect**. Within a second or two the gold **ADMIN REMOTE** tag shows at the top, and the sound PC's channel names, meters and mix appear.
5. On the sound PC, the laptop shows under **Laptops on the desk now** with your name.

## 3. Run the desk from the laptop

1. Move a fader on the laptop. It moves on the sound PC's screen, and the sound changes in the room.
2. Move a fader on the sound PC. It moves on the laptop.
3. Open a channel on the laptop. Change its EQ, gate, compressor and sends. Add an effect to its rack. Check each change on the sound PC.
4. Recall a snapshot from the laptop. Press GO on a cue. Run a macro.
5. Add a mix bus from the laptop. It appears on both.
6. Check the meters move smoothly on the laptop over Wi-Fi.
7. Walk the room with the laptop on Wi-Fi for 10 minutes while mixing. Nothing should lag more than a moment.

## 4. Files

1. On the laptop, go to Show and press **Save the show**. Pick a folder on the laptop. The file appears on the laptop, and the notice says a copy is kept on the sound PC.
2. Press **Open a show** on the laptop and pick that file. The preview shows its scenes and cues. Open it without the mix.
3. On a channel's **Trigger** page, add a WAV file from the laptop. It appears in the drum library on both.

## 5. When things go wrong

1. Switch off the laptop's Wi-Fi for 20 seconds. The sound in the room carries on. The laptop shows **Not linked** with dashes for the levels. Switch Wi-Fi back on: it links again by itself.
2. Close NEX-Audio on the laptop and open it again. It links to the same sound PC by itself.
3. On the sound PC, press **Send away** on the laptop. The laptop says the sound PC closed its link, and stays away until you press **Connect**.
4. On the sound PC, change the laptop PIN. The laptop is sent away. It gets back in only with the new PIN.
5. On the laptop, go to Setup > Driver and try **Shut down the engine**. It is not there, and the page says only the sound PC can do it.
6. Switch the admin remote off on the sound PC. The laptop is sent away and **Find** no longer finds the sound PC.
7. On the laptop press **Disconnect**. The laptop goes back to its own sound, and the sound PC carries on as it was.

## 6. The musicians' remote is unchanged

1. With a laptop on the desk, open the phone remote (`http://<sound PC>:7410`) on a phone, pick a mix and change it. It works as before.
2. Type the phones' engineer PIN on a tablet. It still gets faders, mutes, pans, solos, sends and GO, and nothing more.
