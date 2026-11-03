"""Generate original, tiny test tones. Requires FFmpeg on PATH."""
from pathlib import Path
import subprocess

destination = Path(__file__).parent / "fixtures"
destination.mkdir(exist_ok=True)
codecs = {"mp3": "libmp3lame", "flac": "flac", "m4a": "aac", "ogg": "libvorbis",
          "opus": "libopus", "wma": "wmav2", "wav": "pcm_s16le", "aiff": "pcm_s16be", "wv": "wavpack"}
for extension, codec in codecs.items():
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i",
                    "sine=frequency=440:sample_rate=44100:duration=0.3", "-c:a", codec,
                    str(destination / f"tone.{extension}")], check=True)
