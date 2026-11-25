# NEXORA Phase 2: test Scripture at church

Phase 2 makes the Scripture section real. Install and pair the PCs as in
[PHASE1-TESTING.md](PHASE1-TESTING.md) first (installing the new version over the old one
keeps your users and settings).

## 1. Find verses fast

On a Terminal, sign in, tick **Scripture**, and press **Start session**. The cursor is
already in the reference box. Try each of these and press **Enter** after each one:

| Type | You should see |
|---|---|
| `jn 3 16` | John 3:16 |
| `1co 13 4-7` | 1 Corinthians 13:4-7 |
| `ps 23` | the whole of Psalm 23, split over a few slides |
| `rom 8:28, 31` | Romans 8:28 and 8:31 |
| `gen 50 26` | the last verse of Genesis |
| `rev 22 21` | the last verse of the Bible |
| `isa 53 5` | Isaiah 53:5 |
| `jude 4` | Jude 1:4 |

The small number under the box (for example `4 ms`) is how long the lookup took. It
should always be well under 250 ms.

Type words instead of a reference (for example `love is patient`) to search the whole
Bible. Click a result to use it.

## 2. Step through a passage

1. On the Live Output PC, press **Pin live** on your Scripture terminal.
2. On the terminal, type `jn 3 16` and press **Enter**. It is on the screen.
3. Press **↓**: John 3:17 goes out. Keep pressing; it carries on past the end of what you
   typed. **↑** goes back. **Esc** clears the screen.
4. **Alt+1 … Alt+9** switch translation while showing.
5. Check that the preview on the terminal and the real screen look exactly the same.

## 3. Pastor's list

1. Press **Add** under *Pastor's list* and paste the pastor's notes (for example
   *"Read John 3:16, then Romans 8:28 and 1 Cor 13:4-7"*). NEXORA finds the references.
2. Press **Page Down** to go to the next one in the list, **Page Up** for the one before.
3. The list is still there if NEXORA is restarted the same day.

## 4. Themes

In the *Style* panel on the right:

1. Pick **Sunday Blue**, **Lower third** and **Youth Night** in turn while a verse is
   showing; the screen follows within a moment.
2. Change the background to **Picture** and choose a photo. It is copied to the Live
   Output PC and every other PC by itself.
3. Change the font, size, colours, outline, shadow and the box behind the text.
4. **Save as new** with a name; the theme appears on every Scripture terminal.
5. **Export…** saves a theme to a file; **Import…** loads it on another church's NEXORA.

## 5. Side by side

Under the preview, pick a second translation next to *Side by side with* (for example
ESV). Both translations show next to each other. Pick **Off** to go back.

## 6. Add a translation (admin)

*Settings → Bibles & Scripture → Choose file…* takes Zefania or OSIS XML, USFM, MyBible or
other SQLite Bibles, JSON, CSV, or a zip with any of these. NEXORA checks every verse and
tells you what it found before you press **Add to every PC**. **Remove** (pressed twice)
takes an added translation off every PC. The built-in ones cannot be removed.

Also here: the translation and theme a new Scripture section starts with.

## Please check

- **NLT 2 Samuel 19:41.** This verse was damaged in the NLT file you sent and was
  rebuilt. Please compare it with a printed NLT and tell us if it differs.
- Anything that looks different between the preview and the real screen.
- Fonts: fonts installed on the PC work, but the Live Output PC must have the font too.
