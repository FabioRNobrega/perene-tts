from pathlib import Path
import struct
import wave


def write_wav(path: Path, duration_seconds: float = 3.1, sample: int = 1000):
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16000)
        frames = int(16000 * duration_seconds)
        audio.writeframes(struct.pack('<h', sample) * frames)
