// Wires the AudioPlayer component's custom controls to its <audio> element.
// Everything runs in the browser so playback never round-trips through the circuit.
const format = seconds => {
    if (!Number.isFinite(seconds) || seconds < 0) return "0:00";
    const whole = Math.floor(seconds);
    const minutes = Math.floor(whole / 60);
    const rest = String(whole % 60).padStart(2, "0");
    return minutes >= 60 ? `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, "0")}:${rest}` : `${minutes}:${rest}`;
};

export function attach(root) {
    if (!root || root.dataset.attached) return;
    root.dataset.attached = "true";
    const audio = root.querySelector("audio");
    const play = root.querySelector('[data-action="play"]');
    const mute = root.querySelector('[data-action="mute"]');
    const timeline = root.querySelector(".timeline");
    const volume = root.querySelector(".volume");
    const current = root.querySelector('[data-role="current"]');
    const duration = root.querySelector('[data-role="duration"]');
    let seeking = false;

    const setIcon = (button, icon, label, pressed) => {
        button.querySelector(".bi").className = `bi ${icon}`;
        button.setAttribute("aria-label", label);
        button.title = label;
        if (pressed !== undefined) button.setAttribute("aria-pressed", String(pressed));
    };
    const fill = (input, value, max) => input.style.setProperty("--progress", `${max > 0 ? Math.min(100, value / max * 100) : 0}%`);
    const showTime = () => {
        const total = Number.isFinite(audio.duration) ? audio.duration : 0;
        timeline.max = String(total);
        timeline.disabled = total === 0;
        duration.textContent = format(total);
        if (!seeking) timeline.value = String(audio.currentTime);
        current.textContent = format(seeking ? Number(timeline.value) : audio.currentTime);
        fill(timeline, Number(timeline.value), total);
    };
    const showPlayback = () => {
        root.classList.toggle("is-playing", !audio.paused);
        setIcon(play, audio.paused ? "bi-play-fill" : "bi-pause-fill", audio.paused ? "Play" : "Pause");
    };
    const showVolume = () => {
        const silent = audio.muted || audio.volume === 0;
        setIcon(mute, silent ? "bi-volume-mute-fill" : "bi-volume-up-fill", silent ? "Unmute" : "Mute", silent);
        mute.classList.toggle("active", silent);
        volume.value = String(audio.muted ? 0 : audio.volume);
        fill(volume, Number(volume.value), 1);
    };

    play.addEventListener("click", () => audio.paused ? audio.play().catch(() => {}) : audio.pause());
    mute.addEventListener("click", () => {
        if (audio.muted || audio.volume === 0) { audio.muted = false; if (audio.volume === 0) audio.volume = 1; }
        else audio.muted = true;
    });
    timeline.addEventListener("input", () => { seeking = true; showTime(); });
    timeline.addEventListener("change", () => { audio.currentTime = Number(timeline.value); seeking = false; showTime(); });
    volume.addEventListener("input", () => { audio.volume = Number(volume.value); audio.muted = audio.volume === 0; });
    for (const name of ["loadedmetadata", "durationchange", "timeupdate", "emptied"]) audio.addEventListener(name, showTime);
    for (const name of ["play", "pause", "ended", "emptied"]) audio.addEventListener(name, showPlayback);
    audio.addEventListener("volumechange", showVolume);
    showTime();
    showPlayback();
    showVolume();
}
