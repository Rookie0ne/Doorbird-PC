using System.Runtime.InteropServices;

namespace DoorBird.App.Video.Gpu.Linux;

// Mirrors libavutil/hwcontext_drm.h, which FFmpeg.AutoGen does not generate.
// Layout matches the C structs exactly (AV_DRM_MAX_PLANES = 4).

[StructLayout(LayoutKind.Sequential)]
public struct AVDRMObjectDescriptor {
    public int fd;
    public nuint size;
    public ulong format_modifier;
}

[StructLayout(LayoutKind.Sequential)]
public struct AVDRMPlaneDescriptor {
    public int object_index;
    public nint offset;
    public nint pitch;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AVDRMLayerDescriptor {
    public uint format;
    public int nb_planes;
    public AVDRMPlaneDescriptor plane0, plane1, plane2, plane3;
    public AVDRMPlaneDescriptor Plane(int i) => i switch { 0 => plane0, 1 => plane1, 2 => plane2, _ => plane3 };
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AVDRMFrameDescriptor {
    public int nb_objects;
    public AVDRMObjectDescriptor object0, object1, object2, object3;
    public int nb_layers;
    public AVDRMLayerDescriptor layer0, layer1, layer2, layer3;
    public AVDRMObjectDescriptor Object(int i) => i switch { 0 => object0, 1 => object1, 2 => object2, _ => object3 };
    public AVDRMLayerDescriptor Layer(int i) => i switch { 0 => layer0, 1 => layer1, 2 => layer2, _ => layer3 };
}

public static class DrmFourcc {
    public const uint R8 = 0x20203852;    // 'R8  '
    public const uint GR88 = 0x38385247;  // 'GR88'
    public const uint RG88 = 0x38384752;  // 'RG88'
    public const uint NV12 = 0x3231564E;  // 'NV12'
    public const ulong ModInvalid = 0x00ffffffffffffff;
}
