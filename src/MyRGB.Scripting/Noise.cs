namespace MyRGB.Scripting;

/// <summary>
/// Smooth 3D noise (Ken Perlin's improved noise) for effect scripts: <c>noise(x, y, z)</c> gives 0..1,
/// changing gently as the inputs change. Pass time as <c>z</c> to make it drift.
/// </summary>
internal static class Noise
{
    private static readonly byte[] P = Build();

    private static byte[] Build()
    {
        // A fixed shuffle, so an effect looks the same on every PC.
        var perm = new byte[256];
        for (int i = 0; i < 256; i++) perm[i] = (byte)i;
        var rng = new Random(1337);
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        var p = new byte[512];
        for (int i = 0; i < 512; i++) p[i] = perm[i & 255];
        return p;
    }

    public static double Sample(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return 0.5;
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        int X = (int)((long)fx & 255), Y = (int)((long)fy & 255), Z = (int)((long)fz & 255);
        x -= fx; y -= fy; z -= fz;
        double u = Fade(x), v = Fade(y), w = Fade(z);

        int a = P[X] + Y, aa = P[a] + Z, ab = P[a + 1] + Z;
        int b = P[X + 1] + Y, ba = P[b] + Z, bb = P[b + 1] + Z;

        double n = Lerp(w,
            Lerp(v, Lerp(u, Grad(P[aa], x, y, z), Grad(P[ba], x - 1, y, z)),
                    Lerp(u, Grad(P[ab], x, y - 1, z), Grad(P[bb], x - 1, y - 1, z))),
            Lerp(v, Lerp(u, Grad(P[aa + 1], x, y, z - 1), Grad(P[ba + 1], x - 1, y, z - 1)),
                    Lerp(u, Grad(P[ab + 1], x, y - 1, z - 1), Grad(P[bb + 1], x - 1, y - 1, z - 1))));
        return Math.Clamp((n + 1) / 2, 0, 1);
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    private static double Grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y, v = h < 4 ? y : h is 12 or 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
