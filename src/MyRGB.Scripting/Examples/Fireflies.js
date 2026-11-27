// Fireflies: an example effect for MyRGB that remembers things between frames.
//
// frame() runs once per frame before any light is colored. Use it to move things
// around. light() then colors each light from what frame() worked out.

const flies = [];

effect({
  name: "Fireflies",
  description: "Little lights wander around and glow on and off.",
  author: "MyRGB",

  settings: {
    color:  { type: "color",  label: "Color", default: "#ffcc33" },
    count:  { type: "number", label: "How many", min: 1, max: 30, default: 8 },
    size:   { type: "number", label: "Size", min: 1, max: 10, default: 4 },
    night:  { type: "switch", label: "Dark background", default: true },
  },

  frame(time, delta) {
    while (flies.length < settings.count) {
      flies.push({ x: Math.random(), y: Math.random(), dx: 0, dy: 0, phase: Math.random() * 6.3 });
    }
    flies.length = settings.count;
    for (const f of flies) {
      // Drift a little in a random direction, and bounce off the edges.
      f.dx = clamp(f.dx + (Math.random() - 0.5) * delta * 0.4, -0.08, 0.08);
      f.dy = clamp(f.dy + (Math.random() - 0.5) * delta * 0.4, -0.08, 0.08);
      f.x += f.dx * delta;
      f.y += f.dy * delta;
      if (f.x < 0 || f.x > 1) { f.dx = -f.dx; f.x = clamp(f.x, 0, 1); }
      if (f.y < 0 || f.y > 1) { f.dy = -f.dy; f.y = clamp(f.y, 0, 1); }
      f.glow = Math.max(0, Math.sin(time * 1.3 + f.phase));
    }
  },

  light(p, time) {
    const radius = settings.size * 0.025;
    let bright = 0;
    for (const f of flies) {
      const d = Math.hypot(p.x - f.x, p.y - f.y);
      bright += f.glow * (1 - smooth(0, radius, d));
    }
    const background = settings.night ? black : dim(settings.color, 0.08);
    return mix(background, settings.color, bright);
  },
});
