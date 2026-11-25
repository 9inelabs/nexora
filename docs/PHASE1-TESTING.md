# NEXORA Phase 1: test at church

Phase 1 proves the foundations: the PCs find each other, you can sign in, terminals send
test slides, and the Live Output PC puts them on two different screens. There is no real
Scripture or Lyrics yet; every section shows **test slides** (John 3:16–21, Amazing Grace,
and so on).

## What you need

- The **Live Output PC** (the one connected to the LED wall / projector). A second screen
  (or the Stage Monitor TV) connected to it is ideal, but one screen is fine for a first try.
- **Two other PCs** to use as terminals.
- All three on the same church network (wired or Wi-Fi), with the network set to
  **Private** in Windows (Settings → Network & internet → your network → Private).
- `NEXORA-Setup.exe` copied to each PC (a USB stick is fine).

## 1. Install (each PC, about 1 minute)

1. Double-click **NEXORA-Setup.exe**.
2. If Windows shows *"Windows protected your PC"*, click **More info → Run anyway**.
   (This appears because the test build is not yet code-signed.)
3. NEXORA installs by itself and opens. No internet is needed.
4. If Windows Firewall asks whether NEXORA may use the network, tick **Private networks**
   and click **Allow**. If you missed it: in NEXORA, *Settings → This PC → Allow NEXORA
   through Windows Firewall*.

## 2. Set up the Live Output PC

1. Click **This PC is the Live Output**.
2. Type the church name and address, your name, and a 4-digit PIN (twice).
3. **Write down the pairing code** shown (6 digits). Terminals need it once.
4. Click **Finish set-up**. Two windows appear: *Main Audience Screen* and *Stage Monitor*.
   They are normal windows for now; step 6 moves them onto the real screens.
5. Sign in with your name and PIN.

## 3. Set up each terminal

1. Click **This PC is a Terminal**.
2. Within a few seconds the Live Output PC appears in the list by itself. ✅ *Check: you did not
   have to type any address.*
3. Name the PC (for example `TERMINAL-01`), type the pairing code, click **Connect**.
4. Sign in with your name and PIN.

## 4. Add a user (on the Live Output PC)

1. Sign in, click **Settings → Users & PINs → Add user**.
2. Type a name (for example *Daniel*), a 4-digit PIN, tick **Scripture** and **Song Lyrics**, **Save user**.
3. ✅ *Check: Daniel's name appears on both terminals' sign-in screens straight away.*

## 5. Send test slides and pin them live

1. **Terminal 1:** sign in as Daniel, tick **Scripture**, click **Start session**.
2. **Terminal 2:** sign in, tick **Song Lyrics**, **Start session**.
3. **Live Output PC:** on the section picker click **Open Live Output**. Both terminals are
   listed on the left.
4. With **Main Audience Screen** selected at the top, click **Pin live** on Scripture.
   Terminal 1 shows a red **ON AIR · Main Audience Screen** bar.
5. **Terminal 1:** press **Enter**. ✅ *Check: John 3:16 appears on the Main Audience Screen at once
   (well under a tenth of a second; it should feel instant).*
6. Press the **down arrow** a few times. ✅ *Check: the screen follows every press, nobody touches
   the Live Output PC.*
7. **Live Output:** click **Take down**. ✅ *Check: the verse leaves the screen, the terminal bar turns
   amber STANDBY, and the terminal can keep moving.* Click **Bring back**: its current verse shows again.
8. Select **Stage Monitor** at the top and **Pin live** the Song Lyrics terminal there.
   On Terminal 2 press Enter. ✅ *Check: the two screens show different things at the same time.*
9. Try **F1 / F2** (pin / take down terminal 1 / 2), **Logo**, **Black** and **Clear text**.
10. On the Main Audience Screen, pin Song Lyrics live too. ✅ *Check: Scripture is taken down by
   itself, so a verse and a lyric never overlap ("One text at a time", changeable in Settings → Screens).*

## 6. Put the screens on the real displays

1. **Settings → Screens & outputs.**
2. For *Main Audience Screen* choose the display connected to the LED wall; for *Stage Monitor*
   the stage TV. **Save screens**. The windows jump full screen onto those displays.
   (Keep one display for the control screen itself.)

## 7. Robustness checks (the Phase 1 pass gate)

| Do this | You should see |
|---|---|
| Pull Terminal 1's network cable for 10 seconds, then plug it back | The audience screen keeps the last verse the whole time. Terminal 1 shows a red "Lost connection" banner, then reconnects **within 5 seconds** of the cable going back, still signed in and still pinned. |
| Close NEXORA on Terminal 1 and open it again | It reconnects by itself; sign in, start Scripture: it is still pinned and the next Enter goes straight to screen. The audience screen never went black. |
| On the Live Output PC, close the NEXORA control window and choose **Close, keep screens on** | The audience screens keep their picture. Open NEXORA again: everything carries on, terminals reconnect by themselves. |
| Restart the Live Output PC's NEXORA while terminals are signed in | Terminals show the banner, then reconnect and stay signed in. |

## Pass before moving on to Phase 2

- [ ] Terminals find the Live Output PC without typing addresses
- [ ] A slide appears on screen in under a tenth of a second (feels instant)
- [ ] Both screens show different content
- [ ] A pulled cable or restarted terminal recovers within 5 seconds
- [ ] The audience screen never goes black during any of the above

## When something goes wrong

Send a photo of the screen or a short note. Helpful extras: the log files in
`%LOCALAPPDATA%\NexoraData\logs` (paste that into the File Explorer address bar).
To start a PC from scratch: *Settings → This PC → Run the set-up again*.
