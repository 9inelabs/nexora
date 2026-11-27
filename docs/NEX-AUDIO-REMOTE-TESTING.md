# NEX-Audio: surfaces and remote control, test guide

This covers Phase 6 of making NEX-Audio a complete desk. Everything here is NEX-Audio's own design, with its own names and look.
Most of it is on the new **Remote** tab. The headphones are on the **Groups** tab, next to Solo.

## What is new

| Where | What it does |
|---|---|
| **The X32 as a control surface** (Remote tab) | Lets you mix NEX-Audio from the X32's own faders. Its faders, mutes, pans and sends work NEX-Audio, and so do its SELECT and SOLO buttons. Its motor faders follow what you do on the screen. A fader you are holding is left alone for a moment, so the motor never fights your hand. Channels 1–32, mix buses 1–16 and the main fader are NEX-Audio's. |
| **MIDI fader box** (Remote tab) | Works with any USB box that speaks Mackie Control, such as the Behringer X-Touch, iCON or PreSonus FaderPort. You get 8 motor faders and the master. Each strip has MUTE, SOLO and SELECT, and its knob sets pan (press the knob to centre it). The bank buttons move 8 strips at a time and the channel buttons move 1. Names and levels show on the box's screen, and level meters on its strips. Choose the box in both lists ("It sends on" and "It listens on"). |
| **Tablets and phones** (Remote tab) | A web page on the church network at `http://<sound PC>:7410`. There is nothing to install. |
| **Musicians' mixes** | You tick which mixes musicians may use. A musician picks their mix and turns up what they need in it, and can set its overall level. They cannot change the room, anyone else's mix, or any EQ. Their phone remembers the mix for next time. |
| **The whole desk, for the engineer** | Type the PIN (4 to 8 digits) to get faders, mutes, pans, solos, sends and the GO button for the next cue. Leave the PIN empty to allow musicians only. After five wrong PINs, that phone has to wait a minute. |
| **Headphones** (Groups tab) | Your own headphone feed with its own level. It can play solo bus 1, solo bus 2, the main mix, the stream, or any mix, matrix or group. You choose whether a solo takes over the headphones. Patch **Headphones left / right** on the Outputs tab. Scenes never change it. |
| **Follow the screens (NEXORA)** (Remote tab) | Changes the mix when something goes up on the main screen. A trigger can be "when a song goes up" (any song, or part of a title), "when Scripture, an announcement, the countdown or a video goes up", or "when the screen is cleared". It then recalls a snapshot, goes to a cue, goes to the next cue, or runs a macro. Each trigger fires at most once every 3 seconds, and never while offline. Triggers are saved with the show. |

## 1. The X32 as a surface

1. Link the X32 (Setup > X32 link). On the Remote tab, switch on **The X32 as a control surface**.
2. Move channel 5 on the X32: it should move in NEX-Audio. Move it in NEX-Audio: the X32's motor fader follows.
3. Hold a fader on the X32 and move it slowly. It must not twitch or jump back under your finger.
4. Press SELECT on X32 channel 12: NEX-Audio selects channel 12. Select channel 3 on the screen: the X32's select light moves to 3.
5. Press SOLO on the X32: the channel solos in NEX-Audio. Clear the solo in NEX-Audio: the X32 light goes out.

## 2. A MIDI fader box

1. Plug the box in by USB and set it to Mackie Control (MCU) mode. On the Remote tab press **Look again**, then pick it in both lists.
2. Faders 1–8 should jump to channels 1–8 and show their names. Move a fader: the channel moves on the screen.
3. Press bank right: the faders now show channels 9–16. Press MUTE, SOLO and SELECT, and turn a knob for pan.
4. Move a fader on the screen: the motor fader follows.

## 3. Tablets and phones

1. On the Remote tab, switch on **Tablets and phones**, tick two mixes (for example the vocal wedges and the drummer's in-ears), and set a PIN.
2. Press **Let phones in through Windows Firewall** once. Windows asks for permission.
3. On a phone on the church Wi-Fi, open the address shown. Pick **Vocal wedges** and turn up the lead vocal. The send moves in NEX-Audio and the room does not change.
4. On a tablet, type the PIN. Every channel, effect, group and mix is there with MUTE and SOLO, plus the **GO** button for the next cue.
5. Untick a mix on the Remote tab. The phone using it goes back to the start.

## 4. Headphones

1. Patch Outputs > **Headphones left / right** to your headphone amp.
2. On the Groups tab, set **Headphones play** to the main mix and turn **Headphone level** up and down. Solo bus 1 and 2 levels stay put.
3. Solo a channel: the headphones switch to it. Switch off **A solo takes over the headphones**, and they stay on the main mix.

## 5. Following NEXORA

1. Save a snapshot "Choir". On the Remote tab, add the trigger "A song goes up", song title "Amazing Grace", then "Recall a snapshot", "Choir". Switch on **Follow the screens**.
2. On a NEXORA terminal, put Amazing Grace live on the main screen. NEX-Audio recalls Choir, and the Remote tab says what it did and when.
3. Put Scripture live with a "Scripture goes up" trigger set to go to the Sermon cue. The cue fires.

NEX-Audio hears about the screens through the Live Output PC, so someone must be signed in to NEX-Audio with the link to the Live Output PC up.

Nothing in this phase has been tried on the real X32, a real fader box or the church network yet. The X32 SELECT and SOLO addresses come from the X32's published control protocol. They need checking on the real desk.
