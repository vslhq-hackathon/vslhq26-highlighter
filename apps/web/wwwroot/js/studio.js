// Studio program-monitor interop. Kept tiny: Blazor owns all state; this file
// only touches the <video> element (and the document keydown listener) where
// the DOM API is the only way in.

export function setPlaying(video, playing) {
  if (!video) return;
  clearReverse(video);
  if (playing) {
    // Reaching the end parks the playhead on the last frame (see onTime), so
    // pressing play there means "replay" — rewind first.
    const state = watchers.get(video);
    const lastEnd = state?.segments?.[state.segments.length - 1]?.end;
    if (Number.isFinite(lastEnd) && video.currentTime >= lastEnd - 0.1) {
      video.currentTime = state.segments[0]?.start ?? 0;
    }
    // Autoplay policy can reject play() outside a user gesture; the UI stays
    // truthful via the element's own play/pause events.
    video.play().catch(() => {});
  } else {
    video.pause();
  }
}

export function seek(video, seconds) {
  if (!video || !Number.isFinite(seconds)) return;
  clearReverse(video);
  video.currentTime = Math.max(0, seconds);
}

export function getCurrentTime(video) {
  return video ? video.currentTime : 0;
}

export function getDuration(video) {
  return video && Number.isFinite(video.duration) ? video.duration : 0;
}

export function setRate(video, rate) {
  if (video) video.playbackRate = rate;
}

export function setVolume(video, volume) {
  if (video) video.volume = Math.min(1, Math.max(0, volume));
}

// EDL-aware playback loop: skips cut regions and applies per-segment speed.
// segments: [{ start, end, speed }] in SOURCE seconds, chronological.
const watchers = new WeakMap();

export function watch(video, dotnet, segments) {
  if (!video) return;
  unwatch(video);
  const state = {
    dotnet,
    segments: segments ?? [],
    last: -1,
    prevT: video.currentTime || 0,
    shuttle: 1,
    reverseTimer: null,
  };

  const report = t => {
    state.last = t;
    state.dotnet.invokeMethodAsync('OnPlayhead', t).catch(() => {});
  };

  const onTime = () => {
    const t = video.currentTime;
    const movingBack = t < state.prevT - 1e-3;
    state.prevT = t;
    if (state.segments.length > 0) {
      const seg = state.segments.find(s => t >= s.start - 0.05 && t < s.end);
      if (!seg) {
        const scrubbingBack = movingBack || state.reverseTimer;
        const wasPaused = video.paused && !state.reverseTimer;
        const next = state.segments.find(s => s.start >= t);
        let prev = null;
        for (const s of state.segments) if (s.end <= t + 0.05) prev = s;
        if (scrubbingBack && prev) {
          // Backward motion into a cut lands at the END of the previous kept
          // region — snapping forward here made backward steps impossible.
          video.currentTime = Math.max(prev.start, prev.end - 0.05);
        } else if (next && !wasPaused && !state.reverseTimer) {
          // Playing across a cut: jump to the next kept region.
          video.currentTime = next.start;
          if (next.speed) video.playbackRate = next.speed * state.shuttle;
        } else if (next) {
          // Paused scrub/step into a cut: settle at the next region's start
          // without changing play state.
          video.currentTime = next.start;
        } else {
          // Past the end — whether scrubbed there, stepped there, or played
          // there. Park on the last frame; rewinding here is what made every
          // skip-to-end button look like it "jumped the video to the front".
          // setPlaying() rewinds when the user actually asks to replay.
          const lastEnd = state.segments[state.segments.length - 1]?.end ?? 0;
          if (!video.paused) video.pause();
          video.currentTime = Math.max(0, lastEnd - 0.05);
        }
        return;
      }
      const speed = (seg.speed || 1) * state.shuttle;
      if (Math.abs(video.playbackRate - speed) > 0.01) video.playbackRate = speed;
    }
    if (Math.abs(t - state.last) >= 0.2) report(t);
  };

  // Frame steps are ~33 ms — far under the playback throttle — so seeks and
  // pauses report immediately or the timecode shows a stale position.
  const onSync = () => report(video.currentTime);

  state.handler = onTime;
  state.syncHandler = onSync;
  video.addEventListener('timeupdate', onTime);
  video.addEventListener('seeked', onSync);
  video.addEventListener('pause', onSync);
  watchers.set(video, state);
}

// J/K/L shuttle. rate >= 1 multiplies forward playback speed (composed with
// per-segment speed). rate < 0 emulates reverse scrub — browsers reject
// negative playbackRate — by stepping currentTime backward on a timer, which
// the (backward-aware) cut resolution above understands. Play state itself
// stays owned by setPlaying.
export function setShuttle(video, rate) {
  const state = video && watchers.get(video);
  if (!state) return;
  clearReverse(video);
  if (rate >= 1) {
    state.shuttle = rate;
    video.playbackRate = rate;
  } else if (rate < 0) {
    state.shuttle = 1;
    video.pause();
    state.reverseTimer = setInterval(() => {
      const first = state.segments[0]?.start ?? 0;
      video.currentTime = Math.max(first, video.currentTime + rate * 0.1);
      if (video.currentTime <= first + 1e-3) clearReverse(video);
    }, 100);
  } else {
    state.shuttle = 1;
  }
}

function clearReverse(video) {
  const state = video && watchers.get(video);
  if (state && state.reverseTimer) {
    clearInterval(state.reverseTimer);
    state.reverseTimer = null;
  }
}

export function updateSegments(video, segments) {
  const state = watchers.get(video);
  if (state) state.segments = segments ?? [];
}

export function unwatch(video) {
  const state = video && watchers.get(video);
  if (state) {
    clearReverse(video);
    video.removeEventListener('timeupdate', state.handler);
    video.removeEventListener('seeked', state.syncHandler);
    video.removeEventListener('pause', state.syncHandler);
    watchers.delete(video);
  }
  clearCaptions(video);
  setMusicBed(video, 0);
}

// Captions render here, not in Blazor: the karaoke sweep has to compare each
// word against video.currentTime every frame, and the playhead Blazor sees is
// a 0.2s-throttled SignalR round-trip away. Blazor pushes the caption list;
// this file owns the per-frame paint.
const captionStates = new WeakMap();

export function setCaptions(video, layer, captions, style, enabled) {
  if (!video || !layer) return;
  let state = captionStates.get(video);
  if (!state) {
    state = { raf: 0, activeId: null, activeStyle: null, words: [] };
    state.onPlay = () => startCaptions(video, state);
    state.onPause = () => { stopCaptions(state); paintCaption(video, state); };
    // rAF gives the smooth per-frame sweep, but it is frozen in a hidden tab
    // while the media keeps playing. timeupdate still fires (~4Hz), so it
    // doubles as both the paused-scrub paint and a coarse playing fallback.
    state.onSync = () => paintCaption(video, state);
    video.addEventListener('play', state.onPlay);
    video.addEventListener('pause', state.onPause);
    video.addEventListener('seeked', state.onSync);
    video.addEventListener('timeupdate', state.onSync);
    captionStates.set(video, state);
  }
  state.layer = layer;
  state.captions = captions ?? [];
  state.style = style || 'boxed';
  state.enabled = enabled !== false;
  state.activeId = null; // force a rebuild against the new list
  paintCaption(video, state);
  if (!video.paused) startCaptions(video, state);
}

function clearCaptions(video) {
  const state = video && captionStates.get(video);
  if (!state) return;
  stopCaptions(state);
  video.removeEventListener('play', state.onPlay);
  video.removeEventListener('pause', state.onPause);
  video.removeEventListener('seeked', state.onSync);
  video.removeEventListener('timeupdate', state.onSync);
  state.layer?.replaceChildren();
  captionStates.delete(video);
}

function startCaptions(video, state) {
  if (state.raf) return;
  const tick = () => {
    state.raf = requestAnimationFrame(tick);
    paintCaption(video, state);
  };
  state.raf = requestAnimationFrame(tick);
}

function stopCaptions(state) {
  if (state.raf) cancelAnimationFrame(state.raf);
  state.raf = 0;
}

function paintCaption(video, state) {
  const layer = state.layer;
  if (!layer) return;
  const t = video.currentTime;
  const active = state.enabled
    ? state.captions.find(c => t >= c.start && t <= c.end)
    : null;
  if (!active) {
    // Keyed off the DOM, not activeId: setCaptions nulls activeId to force a
    // rebuild, which would otherwise make this skip clearing a stale line.
    if (layer.firstChild) {
      layer.replaceChildren();
      state.words = [];
    }
    state.activeId = null;
    return;
  }
  if (state.activeId !== active.id || state.activeStyle !== state.style) {
    state.activeId = active.id;
    state.activeStyle = state.style;
    layer.replaceChildren(buildCaption(active, state.style, state));
  }
  for (const word of state.words) {
    const on = word.at <= t;
    if (word.on !== on) {
      word.on = on;
      word.el.classList.toggle('cap-word-on', on);
    }
  }
}

function buildCaption(caption, style, state) {
  const line = document.createElement('span');
  line.className = `cap-line cap-${style}`;
  state.words = [];
  const words = caption.words ?? [];
  if (style === 'karaoke' && words.length > 0) {
    for (const word of words) {
      const el = document.createElement('span');
      el.className = 'cap-word';
      el.textContent = `${word.t} `; // textContent, never innerHTML — caption text is user data
      line.appendChild(el);
      state.words.push({ el, at: word.s, on: false });
    }
  } else {
    line.textContent = caption.text;
  }
  return line;
}

// Music bed preview. The export mixes a synthesized pad (three sines + tremolo
// + lowpass); this mirrors it with WebAudio so the slider is audible before
// export. Pure synthesis — no createMediaElementSource, so a cross-origin
// media response can never silence the program monitor.
let audioCtx = null;
const musicBeds = new WeakMap();

export function setMusicBed(video, level) {
  if (!video) return;
  const wanted = Math.max(0, Math.min(1, level || 0));
  let bed = musicBeds.get(video);
  if (wanted <= 0.001) {
    if (bed) teardownBed(video, bed);
    return;
  }
  bed ??= createBed(video);
  if (!bed) return;
  bed.level = wanted;
  applyBedGain(bed, video.paused ? 0 : wanted);
}

function createBed(video) {
  try {
    audioCtx ??= new (window.AudioContext || window.webkitAudioContext)();
  } catch {
    return null; // no WebAudio: the bed is preview-only, export is unaffected
  }
  const master = audioCtx.createGain();
  master.gain.value = 0;
  master.connect(audioCtx.destination);
  const lowpass = audioCtx.createBiquadFilter();
  lowpass.type = 'lowpass';
  lowpass.frequency.value = 1200;
  lowpass.connect(master);
  const oscillators = [196, 294, 392].map(frequency => {
    const oscillator = audioCtx.createOscillator();
    oscillator.type = 'sine';
    oscillator.frequency.value = frequency;
    const gain = audioCtx.createGain();
    gain.gain.value = 0.33;
    oscillator.connect(gain).connect(lowpass);
    oscillator.start();
    return oscillator;
  });
  const bed = { master, oscillators, level: 0 };
  bed.onPlay = () => {
    audioCtx.resume?.().catch(() => {});
    applyBedGain(bed, bed.level);
  };
  bed.onPause = () => applyBedGain(bed, 0);
  video.addEventListener('play', bed.onPlay);
  video.addEventListener('pause', bed.onPause);
  video.addEventListener('ended', bed.onPause);
  musicBeds.set(video, bed);
  return bed;
}

function applyBedGain(bed, level) {
  // 0.25 keeps the pad under the voice, matching the export's music*0.5 into
  // an amix that halves both legs.
  const now = audioCtx.currentTime;
  bed.master.gain.cancelScheduledValues(now);
  bed.master.gain.setTargetAtTime(level * 0.25, now, 0.08);
}

function teardownBed(video, bed) {
  video.removeEventListener('play', bed.onPlay);
  video.removeEventListener('pause', bed.onPause);
  video.removeEventListener('ended', bed.onPause);
  for (const oscillator of bed.oscillators) {
    try { oscillator.stop(); } catch { /* already stopped */ }
  }
  try { bed.master.disconnect(); } catch { /* already detached */ }
  musicBeds.delete(video);
}

// Unsaved-changes guard. Autosave debounces 1.5s, so a tab closed right after
// an edit would lose it silently.
let dirtyGuard = null;

export function setDirtyGuard(dirty) {
  if (dirty && !dirtyGuard) {
    dirtyGuard = event => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', dirtyGuard);
  } else if (!dirty && dirtyGuard) {
    window.removeEventListener('beforeunload', dirtyGuard);
    dirtyGuard = null;
  }
}

// Editor keyboard: ONE document-level listener while the editor overlay is
// open. preventDefault must be decided synchronously (a Blazor round-trip is
// too late to stop the page scrolling on Space/arrows), so the handled-key
// set lives here; Blazor gets a compact payload and owns all semantics.
let keyState = null;

const NAVIGATION_KEYS = new Set([
  ' ', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown',
  'Home', 'End', 'PageUp', 'PageDown',
]);
const LETTER_KEYS = new Set(['j', 'k', 'l', 'i', 'o', 'z', '+', '=', '-', '_']);

export function bindEditorKeys(dotnet) {
  unbindEditorKeys();
  const state = { dotnet, lastArrow: 0 };
  state.handler = e => {
    if (e.isComposing) return;
    if (e.target?.closest?.('input, textarea, select, [contenteditable="true"]')) return;
    const letter = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    const known = NAVIGATION_KEYS.has(e.key) || LETTER_KEYS.has(letter)
      || e.key === 'Delete' || e.key === 'Backspace';
    if (!known) return;
    if (NAVIGATION_KEYS.has(e.key) || e.key === 'Backspace'
        || ((e.metaKey || e.ctrlKey) && (letter === 'z' || letter === 'k'))) {
      e.preventDefault();
    }
    if (e.repeat && e.key.startsWith('Arrow')) {
      // Auto-repeat fires faster than SignalR round-trips are worth.
      const now = performance.now();
      if (now - state.lastArrow < 33) return;
      state.lastArrow = now;
    }
    state.dotnet.invokeMethodAsync('OnEditorKey', {
      key: e.key,
      shift: e.shiftKey,
      alt: e.altKey,
      mod: e.metaKey || e.ctrlKey,
    }).catch(() => {});
  };
  document.addEventListener('keydown', state.handler);
  keyState = state;
}

export function unbindEditorKeys() {
  if (keyState) {
    document.removeEventListener('keydown', keyState.handler);
    keyState = null;
  }
}
