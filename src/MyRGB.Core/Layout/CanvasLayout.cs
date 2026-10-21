using MyRGB.Core.Devices;

namespace MyRGB.Core.Layout;

/// <summary>Where a device sits on the shared effect canvas. Canvas coordinates run 0..1 on both axes.</summary>
public sealed record DevicePlacement(string DeviceId, double X, double Y, double Width, double Height)
{
    /// <summary>
    /// True when the device's LEDs form one line that runs the other way to its box: a RAM stick
    /// reports a row but stands upright, a board's single-LED zones stack into a column but the
    /// board is wide. Such lines are turned to run along the box's long side.
    /// </summary>
    public bool Turns(IReadOnlyList<LedInfo> leds)
    {
        var shape = LedShapes.Of(leds);
        // On screen the box is Width wide and Height / Aspect tall (in canvas widths).
        bool tall = Height / CanvasLayout.Aspect > Width;
        return shape == LedShape.Row && tall || shape == LedShape.Column && !tall;
    }

    /// <summary>An LED's position on the canvas.</summary>
    public (double X, double Y) Map(LedInfo led, bool turned) => turned
        ? (X + led.Y * Width, Y + led.X * Height)
        : (X + led.X * Width, Y + led.Y * Height);
}

public enum LedShape { Point, Row, Column, Area }

public static class LedShapes
{
    /// <summary>Whether a device's LEDs sit on one horizontal line, one vertical line, or neither.</summary>
    public static LedShape Of(IReadOnlyList<LedInfo> leds)
    {
        if (leds.Count < 2) return LedShape.Point;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var l in leds)
        {
            minX = Math.Min(minX, l.X); maxX = Math.Max(maxX, l.X);
            minY = Math.Min(minY, l.Y); maxY = Math.Max(maxY, l.Y);
        }
        bool flatX = maxX - minX < 0.001f, flatY = maxY - minY < 0.001f;
        return (flatX, flatY) switch
        {
            (true, true) => LedShape.Point,
            (false, true) => LedShape.Row,
            (true, false) => LedShape.Column,
            _ => LedShape.Area,
        };
    }
}

/// <summary>
/// Maps each device's LEDs onto the canvas. Devices without a saved placement get a sensible
/// default spot for their type, laid out like a desk: PC parts on top, keyboard in the middle,
/// mouse to the right, headset to the left.
/// </summary>
public sealed class CanvasLayout
{
    /// <summary>The canvas is drawn 16:9, like a desk seen from the front.</summary>
    public const double Aspect = 16.0 / 9.0;

    private readonly object _gate = new();
    private readonly Dictionary<string, DevicePlacement> _placements = new(StringComparer.OrdinalIgnoreCase);

    public CanvasLayout(IEnumerable<DevicePlacement>? saved = null)
    {
        foreach (var p in saved ?? []) _placements[p.DeviceId] = p;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<DevicePlacement> Placements
    {
        get { lock (_gate) return _placements.Values.ToList(); }
    }

    // Types of devices seen, so defaults can line up same-type devices. Placements don't persist types.
    private readonly Dictionary<string, DeviceType> _types = new(StringComparer.OrdinalIgnoreCase);

    public DevicePlacement GetOrCreate(IRgbDevice device)
    {
        lock (_gate)
        {
            _types[device.Id] = device.Type;
            if (_placements.TryGetValue(device.Id, out var p)) return p;
            // Same-type devices (four RAM sticks, two fan hubs) go side by side, not on top of each other.
            int sameType = _types.Count(t => t.Value == device.Type && t.Key != device.Id && _placements.ContainsKey(t.Key));
            p = DefaultFor(device, sameType);
            _placements[device.Id] = p;
            return p;
        }
    }

    public void Set(DevicePlacement placement)
    {
        lock (_gate) _placements[placement.DeviceId] = Clamp(placement);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Nudge overlapping devices apart, each by the least it takes, keeping everything else where
    /// it is. Returns how many devices moved. Devices with no placement yet are left alone.
    /// </summary>
    public int Tidy(IEnumerable<string> deviceIds)
    {
        const double Gap = 0.012;
        int moved;
        lock (_gate)
        {
            var ids = deviceIds.Where(_placements.ContainsKey).ToList();
            var original = ids.ToDictionary(id => id, id => _placements[id], StringComparer.OrdinalIgnoreCase);
            for (int pass = 0; pass < 60; pass++)
            {
                bool any = false;
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        var a = _placements[ids[i]];
                        var b = _placements[ids[j]];
                        double overlapX = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X) + Gap;
                        double overlapY = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) + Gap;
                        if (overlapX <= 0 || overlapY <= 0) continue;
                        any = true;
                        // Push the second one along whichever axis needs less; on screen x is 16/9 wider.
                        bool sideways = overlapX * Aspect <= overlapY;
                        double dir = sideways ? (b.X + b.Width / 2 >= a.X + a.Width / 2 ? 1 : -1) : (b.Y + b.Height / 2 >= a.Y + a.Height / 2 ? 1 : -1);
                        var nb = sideways ? b with { X = b.X + dir * overlapX } : b with { Y = b.Y + dir * overlapY };
                        nb = Clamp(nb);
                        // Pinned against an edge: move the first one the other way instead.
                        if (nb == b)
                            _placements[ids[i]] = Clamp(sideways ? a with { X = a.X - dir * overlapX } : a with { Y = a.Y - dir * overlapY });
                        else _placements[ids[j]] = nb;
                    }
                if (!any) break;
            }
            moved = ids.Count(id => _placements[id] != original[id]);
        }
        if (moved > 0) Changed?.Invoke(this, EventArgs.Empty);
        return moved;
    }

    public void Reset()
    {
        lock (_gate) _placements.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static DevicePlacement Clamp(DevicePlacement p)
    {
        double w = Math.Clamp(p.Width, 0.02, 1), h = Math.Clamp(p.Height, 0.02, 1);
        return p with { Width = w, Height = h, X = Math.Clamp(p.X, 0, 1 - w), Y = Math.Clamp(p.Y, 0, 1 - h) };
    }

    /// <param name="k">How many devices of the same type were placed before this one.</param>
    private static DevicePlacement DefaultFor(IRgbDevice d, int k)
    {
        // Top band: the PC (fans and strips on the left, board, RAM and GPU in the middle).
        // Bottom: the desk (headset, keyboard, mouse, stand), roughly as they sit in front of you.
        (double x, double y, double w, double h) = d.Type switch
        {
            DeviceType.Keyboard => (0.22, 0.56 + k * 0.05, 0.46, 0.2),
            DeviceType.Keypad => (0.08, 0.6, 0.11, 0.14),
            DeviceType.Mouse => (0.75 + k * 0.08, 0.6, 0.06, 0.12),
            DeviceType.MouseMat => (0.71, 0.54, 0.18, 0.26),
            DeviceType.HeadsetStand => (0.9, 0.48, 0.07, 0.24),
            DeviceType.Headset => (0.04 + k * 0.1, 0.54, 0.1, 0.16),
            DeviceType.Speaker => (0.04 + k * 0.84, 0.78, 0.08, 0.18),
            DeviceType.Monitor or DeviceType.Light => (0.15, 0.42 + k * 0.05, 0.7, 0.04),
            DeviceType.LedStrip or DeviceType.Cooler or DeviceType.Case => (0.05, 0.05 + (k % 3) * 0.11, 0.27, 0.09),
            DeviceType.Motherboard => (0.38 + k * 0.03, 0.05, 0.17, 0.27),
            DeviceType.Memory => (0.57 + (k % 8) * 0.034, 0.05, 0.026, 0.2),
            DeviceType.Gpu => (0.38, 0.34 + (k % 2) * 0.03, 0.24, 0.05),
            _ => (0.05 + (k % 5) * 0.18, 0.84, 0.15, 0.1),
        };
        return Clamp(new DevicePlacement(d.Id, x, y, w, h));
    }
}
