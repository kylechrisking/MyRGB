// Northern Lights: an example effect for MyRGB.
//
// Copy this file, rename it, and change it to make your own effect.
// Save the file, then press "Reload" in MyRGB to see your changes.
// "How to make effects.txt" in this folder explains everything you can use.

effect({
  name: "Northern Lights",
  description: "Slow curtains of green and purple drift across your desk.",
  author: "MyRGB",

  // Each setting shows up as a slider, color picker, list or switch in MyRGB.
  settings: {
    low:   { type: "color",  label: "First color",  default: "#00e08a" },
    high:  { type: "color",  label: "Second color", default: "#8a2be2" },
    speed: { type: "number", label: "Speed", min: 1, max: 10, default: 3 },
    glow:  { type: "number", label: "Brightness %", min: 10, max: 100, default: 90, step: 5 },
  },

  // Called once for every light, every frame. p.x and p.y say where the light is
  // on your desk (0 to 1, left to right and top to bottom). time counts seconds.
  light(p, time) {
    const t = time * settings.speed * 0.05;
    // noise() gives a smooth value from 0 to 1 that changes gently as x, y and t change.
    const curtain = noise(p.x * 2.5, p.y * 0.6 - t, t);
    const shimmer = noise(p.x * 8, p.y * 4, t * 3);
    // A second, slower noise decides which color shows where.
    const which = smooth(0.35, 0.65, noise(p.x * 1.2 + 10, t * 0.5, 3));
    const color = mix(settings.low, settings.high, which);
    const brightness = (0.15 + 0.85 * smooth(0.2, 0.7, curtain)) * (0.6 + 0.4 * shimmer);
    return dim(color, brightness * settings.glow / 100);
  },
});
