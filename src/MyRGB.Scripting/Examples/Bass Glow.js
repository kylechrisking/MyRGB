// Bass Glow: an example effect for MyRGB that follows the music.
//
// Reading "music" makes MyRGB listen to what your PC is playing, the same way
// its own music effects do. Nothing is recorded or sent anywhere.

effect({
  name: "Bass Glow",
  description: "The bass fills your desk from the bottom up, and each kick shifts the color.",
  author: "MyRGB",

  settings: {
    sensitivity: { type: "number", label: "Sensitivity", min: 1, max: 10, default: 5 },
    colors: { type: "choice", label: "Colors", options: ["Rainbow", "One color"], default: "Rainbow" },
    color:  { type: "color",  label: "Color", default: "#ff2060" },
  },

  light(p, time) {
    const level = clamp(music.bass * (0.4 + settings.sensitivity * 0.16), 0, 1);
    // p.y is 0 at the top of your desk and 1 at the bottom.
    const height = 1 - p.y;
    const lit = 1 - smooth(level - 0.1, level + 0.1, height);
    const color = settings.colors === "One color"
      ? settings.color
      : hsv(music.beats * 40 + height * 60, 1, 1);
    return dim(color, 0.05 + 0.95 * lit);
  },
});
