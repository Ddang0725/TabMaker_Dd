/* YIN pitch estimation and dynamic-programming guitar fingering. */
function estimatePitch(samples, offset, rate) {
  const size = 768, maxLag = 110, minLag = 5;
  let energy = 0;
  for (let j = 0; j < size; j++) energy += samples[offset + j] ** 2;
  if (Math.sqrt(energy / size) < 0.009) return null;
  const difference = new Float64Array(maxLag + 1);
  let sum = 0;
  for (let lag = 1; lag <= maxLag; lag++) {
    let d = 0;
    for (let j = 0; j < size; j++) {
      const delta = samples[offset + j] - samples[offset + j + lag];
      d += delta * delta;
    }
    sum += d;
    difference[lag] = sum ? d * lag / sum : 1;
  }
  for (let lag = minLag; lag < maxLag; lag++) {
    if (difference[lag] < 0.16) {
      while (lag + 1 < maxLag && difference[lag + 1] < difference[lag]) lag++;
      const left = difference[lag - 1], mid = difference[lag], right = difference[lag + 1];
      const denominator = left - 2 * mid + right;
      const refined = lag + (denominator ? Math.max(-0.5, Math.min(0.5, (left - right) / (2 * denominator))) : 0);
      const midi = Math.round(69 + 12 * Math.log2(rate / refined / 440));
      return midi >= 36 && midi <= 88 ? midi : null;
    }
  }
  return null;
}
function fingerNotes(notes, tuning, maxFret) {
  const layers = [];
  for (const note of notes) {
    const choices = tuning.map((open, string) => ({string, fret: note.midi - open}))
      .filter(c => c.fret >= 0 && c.fret <= maxFret);
    if (!choices.length) { layers.push([{string:null, fret:null, cost:0, previous:0}]); continue; }
    const previous = layers[layers.length - 1];
    for (const c of choices) {
      c.cost = Infinity;
      c.previous = 0;
      const base = c.fret * 0.07 + (c.fret === 0 ? -0.2 : 0);
      if (!previous) { c.cost = base; continue; }
      previous.forEach((p, index) => {
        const motion = p.fret === null ? 0 : Math.abs(c.fret - p.fret) * 0.65 + Math.abs(c.string - p.string) * 0.9;
        const cost = p.cost + motion + base;
        if (cost < c.cost) { c.cost = cost; c.previous = index; }
      });
    }
    layers.push(choices);
  }
  if (!layers.length) return [];
  let index = layers.at(-1).reduce((best, c, i, a) => c.cost < a[best].cost ? i : best, 0);
  const result = [];
  for (let i = notes.length - 1; i >= 0; i--) {
    const choice = layers[i][index];
    result.push({...notes[i], string:choice.string, fret:choice.fret});
    index = choice.previous;
  }
  return result.reverse();
}
self.onmessage = ({data}) => {
  try {
    const {samples, rate, start, tuning, maxFret} = data;
    const hop = 160, window = 878, frames = [];
    for (let offset = 0; offset + window <= samples.length; offset += hop) {
      frames.push(estimatePitch(samples, offset, rate));
      if (frames.length % 25 === 0) self.postMessage({progress: Math.round(offset / samples.length * 95)});
    }
    const smoothed = frames.map((pitch, i) => {
      const nearby = frames.slice(Math.max(0, i - 1), i + 2).filter(p => p !== null).sort((a,b) => a-b);
      return pitch === null ? null : nearby[Math.floor(nearby.length / 2)];
    });
    const notes = [];
    for (let i = 0; i < smoothed.length;) {
      let end = i + 1;
      while (end < smoothed.length && smoothed[end] === smoothed[i]) end++;
      const duration = (end - i) * hop / rate;
      if (smoothed[i] !== null && duration >= 0.08) notes.push({midi:smoothed[i], start:start + i * hop / rate, duration});
      i = end;
    }
    self.postMessage({result:fingerNotes(notes, tuning, maxFret)});
  } catch (error) { self.postMessage({error:error.message}); }
};
