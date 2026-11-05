using System;
using System.Runtime.InteropServices;

namespace DoorBird.App.Video.Gpu.Linux;

/// <summary>
/// The handful of EGL and GL entry points needed to wrap a DMA-BUF as a GL texture.
/// Everything beyond eglGetProcAddress/eglQueryString is resolved at runtime so a
/// system without the extensions fails the probe instead of failing to load.
/// </summary>
public static unsafe class EglNative {
    private const string LibEgl = "libEGL.so.1";

    public const int EGL_NONE = 0x3038;
    public const int EGL_WIDTH = 0x3057;
    public const int EGL_HEIGHT = 0x3056;
    public const int EGL_EXTENSIONS = 0x3055;
    public const int EGL_LINUX_DMA_BUF_EXT = 0x3270;
    public const int EGL_LINUX_DRM_FOURCC_EXT = 0x3271;
    public const int EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272;
    public const int EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273;
    public const int EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274;
    public const int EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT = 0x3443;
    public const int EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT = 0x3444;
    public const int EGL_DEVICE_EXT = 0x322C;
    public const int EGL_DRM_DEVICE_FILE_EXT = 0x3233;
    public const int EGL_DRM_RENDER_NODE_FILE_EXT = 0x3377;
    public const int EGL_NO_DISPLAY_SENTINEL = 0;

    [DllImport(LibEgl, EntryPoint = "eglGetProcAddress")]
    public static extern IntPtr GetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(LibEgl, EntryPoint = "eglGetCurrentDisplay")]
    public static extern IntPtr GetCurrentDisplay();

    [DllImport(LibEgl, EntryPoint = "eglQueryString")]
    private static extern IntPtr QueryStringRaw(IntPtr display, int name);

    [DllImport(LibEgl, EntryPoint = "eglGetError")]
    public static extern int GetError();

    public static string QueryString(IntPtr display, int name) =>
        Marshal.PtrToStringAnsi(QueryStringRaw(display, name)) ?? "";

    /// <summary>Extension entry points, resolved through eglGetProcAddress.</summary>
    public sealed class Procs {
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, int*, IntPtr> CreateImageKHR;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> DestroyImageKHR;
        public delegate* unmanaged[Cdecl]<int, IntPtr, void> ImageTargetTexture2DOES;
        public delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr*, int> QueryDisplayAttribEXT;
        public delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr> QueryDeviceStringEXT;

        public static Procs Resolve() {
            var p = new Procs {
                CreateImageKHR = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)GetProcAddress("eglCreateImageKHR"),
                DestroyImageKHR = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)GetProcAddress("eglDestroyImageKHR"),
                ImageTargetTexture2DOES = (delegate* unmanaged[Cdecl]<int, IntPtr, void>)GetProcAddress("glEGLImageTargetTexture2DOES"),
                QueryDisplayAttribEXT = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr*, int>)GetProcAddress("eglQueryDisplayAttribEXT"),
                QueryDeviceStringEXT = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)GetProcAddress("eglQueryDeviceStringEXT"),
            };
            if (p.CreateImageKHR == null || p.DestroyImageKHR == null || p.ImageTargetTexture2DOES == null)
                throw new PlatformNotSupportedException("EGL_KHR_image / GL_OES_EGL_image entry points not available");
            return p;
        }

        /// <summary>The DRM render node behind an EGL display, if the driver reports it.</summary>
        public string? RenderNodeOf(IntPtr display) {
            if (QueryDisplayAttribEXT == null || QueryDeviceStringEXT == null) return null;
            IntPtr device = IntPtr.Zero;
            if (QueryDisplayAttribEXT(display, EGL_DEVICE_EXT, &device) == 0 || device == IntPtr.Zero) return null;
            var node = Marshal.PtrToStringAnsi(QueryDeviceStringEXT(device, EGL_DRM_RENDER_NODE_FILE_EXT));
            if (!string.IsNullOrEmpty(node)) return node;
            return Marshal.PtrToStringAnsi(QueryDeviceStringEXT(device, EGL_DRM_DEVICE_FILE_EXT));
        }
    }
}
