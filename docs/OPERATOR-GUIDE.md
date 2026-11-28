# NEXORA operator guide

A one-page guide for Sunday. Keep a printed copy at the media desk.

## Before the service (45 minutes early)

1. Turn on the **Live Output PC** first, then the screens, then the terminals.
2. Sign in on the Live Output PC and open **Live Output**. Check the green
   *Output running* light at the top.
3. **Service tab:** turn **Rehearsal mode** on while the team practises.
4. **Video & pictures:** add today's videos and press each once in rehearsal.
5. **Record & stream:** check the lights are green (frames, CPU, disk space).
6. Each operator signs in on their terminal with their 4-digit PIN and ticks their section.

## When the service starts

1. **Service tab:** turn **Rehearsal mode** off and **Service mode** on.
2. **Record & stream:** press **Start recording** and, if needed, **Start streaming**.
3. Pin terminals live when it is their turn: **F1 to F8** pin or take down terminal 1 to 8 on
   the selected screen. **Tab** moves to the next screen.

## Keys on the terminals

| Section | Keys |
|---|---|
| Scripture | type a reference and **Enter**, **↓ / ↑** next and previous verse, **Esc** clear (or stop a card about to go live) |
| Lyrics | **→ / ←** next and back, **V**+number verse, **C** chorus, **B** bridge, **E** ending, **F1** blank, **F2** logo, **Esc** clear, **Ctrl+↓** next song |
| Lower Thirds | **F1 to F8** today's people |
| Camera Switcher | **1 to 8** preview, **Shift+number** straight to air, **Space** cut, **Enter** auto |
| Minister Timer | **Space** start or pause, **+ / −** a minute, **R** restart, **Esc** stop and take off the screen |

## Scripture listening (the pastor's voice to cards)

1. Once per PC: Scripture > **Listening** > **Set up**, get a speech model (download it once on the
   Live Output PC, then **Copy from Live Output** on the Scripture PC) and press
   **Test this PC's speed**. On the sound PC choose the pastor's channel in
   **NEX-Audio > Stream > Speech feed for Scripture**.
2. When the sermon starts press **Start listening**. The pastor's words appear in the grey box and
   every Bible reference heard becomes a card: **tap** it to preview, **Enter** to send, or the
   small **→** to send at once. "Next verse", "verse 20" and "in the Amplified" become cards too.
3. **Pause** during prayer or songs. **Stop** when the sermon ends.
4. **Maybe** lists stories and verses the pastor quotes or talks about. They are only
   suggestions; check before sending.
5. With **Auto** on (Set up), full references go to the screen after a 1-second hold:
   **Esc** stops it, **Enter** sends it now. Use cards only for the first few Sundays.

## Minister Timer (guest ministers)

1. Open **Minister Timer** on a terminal. Type the minister's name and what they are doing
   (Sermon, Exhortation), and set their minutes with **− / +** or 5, 10, 15, 20, 30, 45.
2. Before the service, press **Add this minister to today** for each one. In the service,
   tap a name on the left to load their time.
3. Press **Start and send live**. The timer goes straight to the Stage Monitor; the
   audience screens never show it.
4. It is **green** at the start, **yellow** from 10 minutes left and **red** from 5 minutes
   left (change both with − / +). From 3 minutes left it says *Please start to close*.
   At zero it shows **TIME IS UP** and counts in minus (−2:14) on a dark red screen.
5. **Full screen** fills the Stage Monitor. **Bar over the lyrics** shows a coloured band
   at the top so the words stay visible. Change it while running with
   **Apply changes to the running timer**.
6. The Live Output operator can take the timer down on the Stage Monitor like any terminal.

## Backgrounds for Scripture and Lyrics

NEXORA comes with 19 pictures and 21 video loops (skies, sunrises, crosses, forests, stars and soft lights).

1. In **Scripture** or **Lyrics**, open **Style**.
2. Under **Background**, press **Picture** or **Video**, then press a small picture to use it.
3. Turn **Darken** up or down so the words stay easy to read.

Video loops play on the screens. Previews on the terminals show a still, so they stay fast. You can still use your own picture with **Use your own picture…**. The credits list is in BACKGROUNDS-CREDITS.txt next to the installer.

## Square or odd-shaped LED walls

Each output can have its own shape, so nothing is squeezed into a 16:9 box.

1. On the Live Output PC: **Settings › Screens & outputs**.
2. Under the screen, pick its **Screen shape**: Wide 16:9, 4:3, Square, Tall (portrait), or **Custom pixel size**. For Custom, type the wall's size from the LED processor, for example 768 × 768.
3. If the LED processor reads only one area of the HDMI picture, tick **Use only part of the display** and type where that area is, in pixels from the top-left corner.
4. If the LED processor squeezes the whole HDMI picture onto the wall, tick **Stretch to fill the display** instead.
5. Turn on **Test pattern** and press **Save screens**. On the wall you should see the red edge line on all four sides and a round circle (not an oval). Turn the test pattern off and save again.

Scripture, Lyrics, lower thirds, backgrounds and videos all lay out for that shape. The recording and the live stream stay 16:9.

## Updating NEXORA

1. On the **Live Output PC**, outside a service: **Settings › Updates › Update NEXORA**. It needs internet on that PC. NEXORA downloads the new version, offers it to every PC and restarts.
2. **Terminals** update by themselves the next time nobody is signed in on them. To update one straight away: **Settings › Updates › Install the offered version**.
3. Users, songs, settings and media are kept. **Go back to …** on the Live Output PC offers the previous version again if a new one misbehaves.

No internet on the Live Output PC? Copy the NEXORA-Update folder onto it and use **No internet? Update from files**.

## If something goes wrong

| What you see | What to do |
|---|---|
| A terminal shows *Reconnecting* | Check its network cable. The screen keeps the last picture. |
| An audience screen froze | Live Output › **Restart output windows** (the control screen stays open). |
| The wrong thing is on screen | Press **Black** or **Logo** on Live Output; it covers everything until pressed again. |
| A video will not play | Stop it and use the backup: a picture or the logo. Check it after the service. |
| The stream says *Reconnecting* | Leave it; it retries by itself. Check the internet. |
| The Live Output PC will not start | On a terminal: Settings › Backups & standby › **Make this PC the Live Output PC**. |

## After the service

1. **Stop recording** and **Stop streaming**.
2. **Service tab:** turn **Service mode** off.
3. Copy the recording off the PC (it is in the *NEXORA Recordings* folder).
4. Sign out on every terminal.
