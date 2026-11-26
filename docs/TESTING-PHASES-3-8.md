# NEXORA Phases 3 to 8: test everything at church

Install **NEXORA-Setup.exe** on every PC (it installs over the old version and keeps your users,
songs and settings). Pair the PCs as in [PHASE1-TESTING.md](PHASE1-TESTING.md).

Before you start:

- **Network cameras (NDI):** install the free *NDI Tools* (or *NDI Runtime*) from ndi.video on
  the Live Output PC and on the Camera Switcher PC. Without it NEXORA still works, but NDI
  cameras do not appear.
- **Sound for recordings:** plug the sound desk into the Live Output PC (USB audio interface
  or the capture card's audio).
- Write down anything that looks wrong, with the time. Each PC keeps a log in
  `%LocalAppData%\NEXORA\logs` that helps find the cause.

## 1. Song Lyrics (Phase 3)

1. On a terminal, tick **Song Lyrics** and press **Start session**.
2. **Import:** press **Import** and choose your EasyWorship `Songs.db`, SongSelect `.txt`
   files, OpenLyrics / OpenSong files, or a `.zip` of them. Check the count it reports.
3. **New song:** press **New**, paste the words with labels (`Verse 1`, `Chorus`, `Bridge` …),
   set the order (for example `V1 C V2 C B C`), and save.
4. Search by title or any words of a line. Add songs to **Today's songs** with **+**.
5. On the Live Output PC press **Pin live** on the Lyrics terminal for the Main screen
   **and** for the Stage Monitor.
6. Double-click the first slide. Then use **→ / ←**, and the jump keys: `V` then `2`
   (verse 2), `C` chorus, `B` bridge, `E` ending, **F1** blank, **F2** logo, **Esc** clear,
   **Ctrl+↓** next song in today's list.
7. Check: the animation, the copyright line on the first slide, and the Stage Monitor showing
   the **current lines and the next lines** with the clock.
8. Try **Lines per slide** (2 or 3) and **Over a camera** (lower third look).

## 2. Announcements, Lower Thirds, Countdown (Phase 4)

**Announcements:** add slides (title, text, picture, an end date), pick a template, then
**Run the loop**. It moves on by itself; stop it with **Stop**.

**Lower Thirds:** add people (name, title, photo). Tick who is ministering today. On the
terminal, **F1 to F8** show today's people. They hide after the time you set.

**Countdown:** pick a preset or type minutes / a time like `09:00`. Try pause, resume,
**+1 min / -1 min** and stop. Choose where it shows (Stage only, a badge, or full screen).
Add the pastor's sermon points; they appear on the Stage Monitor.

## 3. Videos and pictures (Phase 5), on the Live Output PC

Open **Live Output** and look at the lower half: **Video & pictures**.

1. **Add videos or pictures.** Each file is copied onto the Live Output PC and checked; you
   see its length, size and a picture of it. A file with a problem shows a yellow note.
2. Pick a video and press **Cue** (first frame, silent), then **Play**. The time left counts
   down in the corner; it turns red in the last 10 seconds.
3. Try **Pause**, **Play** again (it carries on), **Restart**, **Stop**, **Loop**, the volume
   slider, and **Sound to** (choose the speakers).
4. While a video plays, send a verse from the Scripture terminal: it waits behind the
   video. Tick **Show words over video** to show it on top.
5. Let a video play to the end: the screen goes back to what was there before.

## 4. Cameras (Phase 6)

**Camera Switcher terminal:** tick **Camera Switcher**. Add your cameras (NDI cameras,
capture cards, or a video file to practise with). Click a camera for Preview, press
**Cut** (Space) or **Auto** (Enter), or drag the **T-bar**. Keys: **1 to 8** preview,
**Shift+number** straight to Program. The Program is sent to the Live Output PC over NDI
as *NEXORA Program*.

**Live Output PC, Cameras tab:** pick a capture card or NDI camera directly when the
switcher terminal is not running. **Take camera off** clears it.

Check that Scripture and Lyrics sit over the camera as a lower third.

## 5. Recording and streaming (Phase 7)

1. **Settings › Recording & streaming** (on the Live Output PC): choose the folder, the
   sound input, size (1080), frames (30), quality, and paste the YouTube or Facebook stream
   address and key. Save.
2. **Live Output › Record & stream:** set the service name, press **Start recording**. The
   health lights show frames a second, CPU and free disk space.
3. Record for 5 minutes with a video, a camera and some lyrics, then **Stop recording**.
   Open the file from the recordings folder: picture, sound and timing should be right. If
   the sound is ahead of the lips, set *Delay the sound by* (try 150 ms).
4. **Start streaming** (use an unlisted YouTube stream first). Pull the network cable for
   10 seconds: it shows *Reconnecting* and carries on by itself.
5. **Settings › Church branding › Small logo in the corner** puts the logo on the recording
   and stream.

## 6. Service, Rehearsal, backups, updates, take-over (Phase 8)

**Service tab (Live Output):**

- **Rehearsal mode** on: operators can practise; the audience screens show only the logo,
  and recording and streaming are blocked. Turn it off and everything returns.
- **Service mode** on: settings, imports and updates are locked on every PC.
- **Message to the stage:** type or press *5 minutes*; it shows in gold on the Stage Monitor.

**Settings › Backups & standby:**

- **Save a backup…** then **Restore from a file…** (every PC reconnects after a few seconds).
- The Live Output PC saves a nightly backup after 2 AM and keeps 30.
- On a terminal: after about 30 minutes connected it holds a standby copy. To test the
  take-over, **turn off the Live Output PC**, then on the terminal press **Make this PC the
  Live Output PC…** and confirm. NEXORA restarts as the Live Output PC with all your songs,
  users and settings. (Afterwards, set the old Live Output PC back up from Settings › This PC.)

**Settings › Updates:** when a new version comes, copy the **NEXORA-Update** folder onto the
Live Output PC, press **Choose the update files…** and pick `releases.win.json`. Every PC then
sees *Install the offered version*. **Go back to …** offers the previous version again if the
new one has a problem.

**Settings › Lyrics & CCLI:** enter your CCLI licence number. The song usage report counts
each song once per day it went live; **Save as a spreadsheet** for CCLI reporting.

## Minister Timer

1. On a terminal, sign in and tick **Minister Timer**. Set *Guest Minister · Sermon* and
   **10** minutes, then press **Start and send live**.
   - The Stage Monitor shows the big timer at once with a yellow border (10:00 is yellow).
   - The Main Audience Screen and the recording show nothing new.
2. Press **+1 minute** twice: it turns green. Press **−1 minute** until under 5:00: red.
   Under 3:00 it says *Please start to close*.
3. Keep pressing **−1 minute** past zero: dark red screen, **TIME IS UP**, minus time, and
   the digits flash gently.
4. Pick **Bar over the lyrics** and press **Apply changes to the running timer**. Put a song
   on the Stage Monitor: the band sits on top and the words show underneath.
5. Unplug the terminal's network cable for a minute: the timer keeps counting on the
   Stage Monitor. Plug it back: the terminal shows the same time.
6. **Pause**, **Resume**, **R** (restart) and **Esc** (stop and take off) all work.
7. Add three ministers to today's list, sign in on a second terminal: the same list shows.

## What was not tested before this build

These need real church equipment and were not tried on the build machine:

- NDI cameras, capture cards and the graphics card's hardware encoder.
- Sound devices (the build machine has no sound card).
- Streaming to YouTube or Facebook.
- Two or more real displays on the Live Output PC.
