# Scripture listening: test it at church

NEXORA can now listen to the pastor, write down what is said, and offer the verses it hears as
cards in the Scripture section. It runs on each PC with Whisper (free, offline, no account and no
credits). Nothing goes to the screen by itself unless you turn **Auto** on.

Write down anything that looks wrong, with the time. Each PC keeps a log in
`%LocalAppData%\NEXORA\logs`; lines that start with `Whisper:` or `Listening` help find the cause.

## 0. Before you start (once)

1. **Update NEX-Audio on the sound PC first** (its **Check for updates** and **Update** buttons),
   then NEXORA on the Live Output PC, then the terminals, as usual.
2. **Get a speech model on the Live Output PC.** Open Scripture there, press **Set up** on the
   *Listening* card and press **Download** on one model. This is the only step that needs the
   internet (148 to 574 MB, once). Which one:

   | Model | Size | For |
   |---|---|---|
   | Fast (base.en) | 148 MB | Older PCs with 4 cores |
   | Recommended (small.en) | 488 MB | PCs without a graphics card, 6 cores or more |
   | More accurate (medium.en) | 539 MB | 8 cores or more, or a modest graphics card |
   | Best (large-v3-turbo) | 574 MB | PCs with a graphics card (NVIDIA, AMD or Intel) |

   The card marked **Suits this PC** is a good first choice. No internet on that PC? Download the
   file on any computer from `huggingface.co/ggerganov/whisper.cpp` (keep the name, for example
   `ggml-small.en.bin`) and use **Import a model file…**.
3. **On the Scripture PC**, open **Set up**: the model now says *On the Live Output PC*. Press
   **Copy from Live Output** (a minute or so over the church network, no internet).
4. Press **Test this PC's speed**. *Comfortable* or *Fine* is good. *Slow* or *Too slow*: pick
   a smaller model, or keep **Use the graphics card** ticked if the PC has one (restart NEXORA
   after changing that box).

## 1. The speech feed from NEX-Audio (Phase 1)

1. On the sound PC open **NEX-Audio > Stream**. Under **Speech feed for Scripture**, choose the
   pastor's channel in **Pastor's mic**. Leave **Only while the mic is up (after the fader)** on
   (turn it off to hear the mic even when the fader is down).
2. The line under it says *Sending <channel> to every Scripture PC on the network*, with a
   packet count that keeps rising.
3. On the Scripture PC, in **Set up**, leave **NEX-Audio feed** chosen and close the panel.
4. Press **Start listening** on the *Listening* card. Within a few seconds:
   - the small bar moves with the pastor's voice, and the line beside it says
     *Hearing SOUND-PC · <channel>*;
   - the words appear in the grey box. The newest words are in *italics* until the sentence ends.
5. Check the delay shown on the right (*1.2 s to write down*). Above 4 s, try a smaller model.
6. Unplug the sound PC's network cable for 10 seconds, then plug it back. Listening carries on
   by itself.
7. If nothing comes through: check Windows did not block NEXORA in the firewall (a prompt may
   have appeared the first time), and that both PCs are on the same network.

Other sources (in **Set up**): **Sound input on this PC** (a USB sound card fed from an aux send
on the desk; tick **Reduce noise** if the room is noisy) and **NDI sound** (for example a vMix or
OBS output carrying the pastor's mic).

## 2. References and spoken navigation (Phase 2)

Read these out near the pastor's mic (or have the pastor say them), and check a card appears:

| Say | Card |
|---|---|
| "Turn with me to John chapter three verse sixteen" | **John 3:16** |
| "First Corinthians thirteen, four to seven" | **1 Corinthians 13:4-7** |
| "Psalm twenty-three" | **Psalms 23** (book and chapter only) |
| "Next verse" | **Next verse → John 3:17** |
| "Go back one verse" / "previous verse" | **Previous verse** |
| "Look at verse twenty" | **Verse 20** of the open chapter |
| "Read on to verse twenty-one" | **On to verse 21** (from the open verse) |
| "Chapter four" / "next chapter" | the same book, chapter 4 / the next chapter |
| "In the Amplified" / "New King James" | **Translation: AMP / NKJV** |

1. **Tap a card**: it loads into the preview (nothing reaches the screen yet). Press **Enter** or
   **Send to screen**.
2. The small **→** button on a card loads it and sends it at once. **×** removes the card.
3. "Next verse" cards follow what is open: open John 3:20 and the card changes to John 3:21.
4. Say a reference twice in a sentence: one card, not two.
5. **Pause** (during prayer or a song) stops new cards without stopping; **Resume** carries on.
6. A partly heard reference ("John 3…") waits for the next words before a card appears, so
   "John 3:16" does not first show as "John 3".

### Auto (turn it on only after a few Sundays of cards)

1. In **Set up > Going to the screen**, switch on **Auto**. Leave the hold at 1 second.
2. Say "John three sixteen". A red line says *John 3:16: going to the screen in 1.0 s* and the
   card has a red edge. After the hold the verse is on the screen.
3. Do it again and press **Esc** during the hold: nothing is sent. Press **Enter** during the hold:
   it goes at once.
4. Say "Psalm twenty-three" (no verse): it stays a card. Only full references (book, chapter
   and verse), spoken navigation and translations go by themselves.
5. Suggestions by meaning (below) never go by themselves.

## 3. Stories and verses by what is said (Phase 3)

Say these, and check the **Maybe** list under the cards:

| Say | Expect |
|---|---|
| "Remember the prodigal son who came back to his father" | Luke 15:11-32, *The prodigal son* |
| "Like David facing the giant with just a sling and a stone" | 1 Samuel 17 |
| "The woman at the well, and the living water" | John 4:1-42 |
| "For God so loved the world that he gave his only son" (quoting) | John 3:16, *Quoted* |
| "The Lord is my shepherd, I shall not want" | Psalm 23:1, *Quoted* |
| "Good morning church, please be seated" | nothing |

1. The first time you start listening, NEXORA prepares the search (a few seconds), then works out
   the verses' meanings in the background once (a few minutes on an older PC; *Learning the
   verses' meanings… 40%* in **Set up**). It is kept for next time.
2. Tap a suggestion to load it; **→** sends it.
3. **How sure** in **Set up**: move left for more suggestions, right for fewer.
4. Untick **Suggest stories and verses** to turn the list off.

## 4. Tuning with a recorded sermon (Phase 4)

1. **Set up > Try it with a recording > Replay a recording…**: choose last Sunday's recording
   (the Live Output PC keeps them; MP3, WAV, M4A or a video file). It plays into NEXORA as if live,
   so cards and Auto behave as on Sunday. **Stop** ends it.
2. **Scan a recording…** reads a whole sermon as fast as the PC can (a 45-minute sermon takes
   about 5 to 15 minutes) and never sends anything to the screen. When it finishes, **Open the
   last report**: every sentence with its time, `>>` for references and commands heard, `??` for
   suggestions. Compare with what the pastor really said and send us the report if something is
   missed often.
3. **Words to expect**: add the pastor's name, the church's name, places and phrases that come
   out wrong in the words box (separate with commas). Scan again and compare.
4. Run the first live Sunday with cards only (Auto off), and watch the delay and the cards.

## What to send back

- The speed test line for each Scripture PC.
- One scan report of a full sermon.
- Anything heard wrong, with the time, and whether it came from NEX-Audio, a sound input or NDI.
