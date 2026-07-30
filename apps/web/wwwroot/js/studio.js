// Studio program-monitor interop. Kept tiny: Blazor owns all state; this file
// only touches the <video> element (and the document keydown listener) where
// the DOM API is the only way in.

export function setPlaying(video, playing) {
  if (!video) return;
  clearReverse(video);
  if (playing) {
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
        } else if (wasPaused || state.reverseTimer) {
          // Paused past the end (scrub, frame-step): clamp to the end of the
          // cut instead of yanking the playhead back to the top.
          const lastEnd = state.segments[state.segments.length - 1]?.end ?? 0;
          video.currentTime = Math.max(0, lastEnd - 0.05);
        } else {
          // Natural end of playback: stop and rewind, ready to replay.
          video.pause();
          video.currentTime = state.segments[0]?.start ?? 0;
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
