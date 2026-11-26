"""Downloads NEXORA's built-in backgrounds (assets/backgrounds/backgrounds.json) and prepares them.

Pictures are cropped to fill a 1920x1080 screen. Videos lose their sound, and a video that does not loop
by itself gets a one-second cross-fade from its end into its start, so it repeats without a jump.
Each item also gets a small thumbnail for the picker, and every video a still (poster) for previews.

usage: python fetch_backgrounds.py MANIFEST OUT_DIR CACHE_DIR [FFMPEG] [FFPROBE]
"""
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request

manifest_path, out_dir, cache_dir = sys.argv[1:4]
ffmpeg = sys.argv[4] if len(sys.argv) > 4 else "ffmpeg"
ffprobe = sys.argv[5] if len(sys.argv) > 5 else "ffprobe"

raw_dir = os.path.join(cache_dir, "raw")
done_dir = os.path.join(cache_dir, "done")
for d in (raw_dir, done_dir, out_dir):
    os.makedirs(d, exist_ok=True)


def download(url, target):
    if os.path.exists(target) and os.path.getsize(target) > 10_000:
        return
    last = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NexoraBuild/1.0"})
            with urllib.request.urlopen(req, timeout=120) as r, open(target + ".part", "wb") as f:
                shutil.copyfileobj(r, f, 1 << 20)
            os.replace(target + ".part", target)
            return
        except Exception as e:  # network hiccup: wait and try again
            last = e
            time.sleep(3 * (attempt + 1))
    raise RuntimeError(f"Could not download {url}: {last}")


def run(args):
    r = subprocess.run(args, capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError("FFmpeg failed: " + " ".join(args) + "\n" + r.stderr[-2000:])
    return r.stdout


def ff(*args):
    run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *args])


def duration(path):
    return float(run([ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path]).strip())


FILL = "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080"
THUMB = "scale=320:180:force_original_aspect_ratio=increase,crop=320:180"

manifest = json.load(open(manifest_path, encoding="utf-8"))
listed = []
for item in manifest["items"]:
    name = "nexora-" + item["id"]
    thumb = name + "-thumb.jpg"
    if item["kind"] == "picture":
        file, poster = name + ".jpg", None
        outputs = [file, thumb]
    else:
        file, poster = name + ".mp4", name + ".jpg"
        outputs = [file, poster, thumb]

    if not all(os.path.exists(os.path.join(done_dir, o)) for o in outputs):
        ext = ".jpg" if item["kind"] == "picture" else ".mp4"
        raw = os.path.join(raw_dir, item["id"] + ext)
        print(f"{item['id']}: downloading {item['url']}", flush=True)
        download(item["url"], raw)
        if item["kind"] == "picture":
            ff("-i", raw, "-vf", FILL, "-q:v", "2", os.path.join(done_dir, file))
            ff("-i", raw, "-vf", THUMB, "-q:v", "4", os.path.join(done_dir, thumb))
        else:
            target = os.path.join(done_dir, file)
            if item.get("seamless"):
                ff("-i", raw, "-map", "0:v:0", "-c:v", "copy", "-an", "-movflags", "+faststart", target)
            else:
                length = duration(raw)
                fade = min(1.0, length / 4)
                graph = (
                    f"[0:v]split[a][b];"
                    f"[a]trim=start={fade},setpts=PTS-STARTPTS[main];"
                    f"[b]trim=0:{fade},setpts=PTS-STARTPTS[head];"
                    f"[main][head]xfade=transition=fade:duration={fade}:offset={length - 2 * fade:.3f},format=yuv420p[v]"
                )
                ff("-i", raw, "-filter_complex", graph, "-map", "[v]", "-an", "-c:v", "libx264", "-preset", "medium", "-crf", "19",
                   "-movflags", "+faststart", target)
            ff("-ss", "0.5", "-i", target, "-frames:v", "1", "-vf", FILL, "-q:v", "3", os.path.join(done_dir, poster))
            ff("-ss", "0.5", "-i", target, "-frames:v", "1", "-vf", THUMB, "-q:v", "4", os.path.join(done_dir, thumb))
        print(f"{item['id']}: ready", flush=True)

    for o in outputs:
        shutil.copyfile(os.path.join(done_dir, o), os.path.join(out_dir, o))
    entry = {"id": item["id"], "kind": item["kind"], "title": item["title"], "file": file, "thumb": thumb, "page": item["page"]}
    if poster:
        entry["poster"] = poster
    listed.append(entry)

json.dump({"source": manifest["source"], "items": listed}, open(os.path.join(out_dir, "backgrounds.json"), "w", encoding="utf-8"), indent=1)
with open(os.path.join(out_dir, "CREDITS.txt"), "w", encoding="utf-8") as f:
    f.write("NEXORA built-in backgrounds\n")
    f.write("All from Pexels, used under the Pexels licence (https://www.pexels.com/license/).\n")
    f.write("Free to use; attribution is not required, but we thank every creator.\n\n")
    for e in listed:
        f.write(f"{e['id'].upper()}  {e['title']}  {e['page']}\n")
total = sum(os.path.getsize(os.path.join(out_dir, x)) for x in os.listdir(out_dir))
print(f"{len(listed)} backgrounds, {total / 1e6:.0f} MB", flush=True)
