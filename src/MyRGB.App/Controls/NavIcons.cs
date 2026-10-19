using Avalonia.Media;

namespace MyRGB.App.Controls;

/// <summary>Sidebar and tour icons (24x24), shared by XAML (x:Static) and view models.</summary>
public static class NavIcons
{
    public static Geometry Home { get; } = StreamGeometry.Parse("M12,3 L21,10.5 V21 H14.5 V15 H9.5 V21 H3 V10.5 Z");
    public static Geometry Presets { get; } = StreamGeometry.Parse("M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z");
    public static Geometry Community { get; } = StreamGeometry.Parse("M9,4 A3.5,3.5 0 1 1 8.99,4 Z M3,19 C3,15.5 5.7,13 9,13 C12.3,13 15,15.5 15,19 V20 H3 Z M16.5,6 A3,3 0 1 1 16.49,6 Z M16,12.3 C19,12.3 21,14.5 21,17.5 V18.5 H16.6 C16.5,16.2 15.6,14.3 14.1,12.9 C14.7,12.5 15.3,12.3 16,12.3 Z");
    public static Geometry Studio { get; } = StreamGeometry.Parse("M12,2 L14.4,9.6 L22,12 L14.4,14.4 L12,22 L9.6,14.4 L2,12 L9.6,9.6 Z");
    public static Geometry Layout { get; } = StreamGeometry.Parse("M3,3 H14 V11 H3 Z M16,3 H21 V11 H16 Z M3,13 H9 V21 H3 Z M11,13 H21 V21 H11 Z");
    public static Geometry Devices { get; } = StreamGeometry.Parse("F0 M2,6 H22 V18 H2 Z M3.5,7.5 V16.5 H20.5 V7.5 Z M5,9 H7 V11 H5 Z M8.5,9 H10.5 V11 H8.5 Z M12,9 H14 V11 H12 Z M15.5,9 H17.5 V11 H15.5 Z M7,13 H17 V15 H7 Z");
    public static Geometry Vendors { get; } = StreamGeometry.Parse("F0 M4,7 H20 L19,21 H5 Z M6.2,9 L6.9,19 H17.1 L17.8,9 Z M9,3 H15 V5 H21 V7 H3 V5 H9 Z M9,11 H10.6 V17 H9 Z M13.4,11 H15 V17 H13.4 Z");
    public static Geometry Settings { get; } = StreamGeometry.Parse("M3,5 H21 V7 H3 Z M3,11 H21 V13 H3 Z M3,17 H21 V19 H3 Z M7,3 H10 V9 H7 Z M14,9 H17 V15 H14 Z M5,15 H8 V21 H5 Z");
    public static Geometry Help { get; } = StreamGeometry.Parse("F0 M12,2 A10,10 0 1 1 11.99,2 Z M12,4 A8,8 0 1 0 12.01,4 Z M11,16 H13 V18 H11 Z M12,6 C14.2,6 16,7.6 16,9.6 C16,11.3 14.9,12 13.9,12.6 C13.3,13 13,13.3 13,14 V14.5 H11 V13.8 C11,12.4 11.8,11.7 12.7,11.1 C13.5,10.6 14,10.3 14,9.6 C14,8.7 13.1,8 12,8 C10.9,8 10,8.7 10,9.7 H8 C8,7.6 9.8,6 12,6 Z");
    public static Geometry Warning { get; } = StreamGeometry.Parse("F0 M12,2 L23,21 H1 Z M12,6 L4.5,19 H19.5 Z M11,10 H13 V15 H11 Z M11,16.5 H13 V18.5 H11 Z");
    public static Geometry Info { get; } = StreamGeometry.Parse("F0 M12,2 A10,10 0 1 1 11.99,2 Z M12,4 A8,8 0 1 0 12.01,4 Z M11,10 H13 V17 H11 Z M11,6.5 H13 V8.5 H11 Z");
    public static Geometry Check { get; } = StreamGeometry.Parse("M9,16.2 L4.8,12 L3.4,13.4 L9,19 L21,7 L19.6,5.6 Z");
}
