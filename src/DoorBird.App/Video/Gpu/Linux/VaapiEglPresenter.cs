using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using static Avalonia.OpenGL.GlConsts;

namespace DoorBird.App.Video.Gpu.Linux;

/// <summary>
/// Zero-copy presenter for Linux: VAAPI surface -> DRM PRIME (DMA-BUF) -> EGLImage ->
/// GL texture in a context shared with Avalonia's renderer -> NV12-to-RGB shader into
/// a texture the compositor imports. The decoded pixels never leave the GPU.
///
/// Requirements, all verified at construction and by the first frames:
///   - Avalonia running its OpenGL backend through EGL (X11RenderingMode.Egl)
///   - EGL_EXT_image_dma_buf_import and GL_OES_EGL_image
///   - a VAAPI driver on the same DRM render node the EGL display uses
/// Every failure returns false from <see cref="TryPresent"/>, and the decoder's
/// Auto-mode probe turns that into a software fallback.
/// </summary>
public sealed unsafe class VaapiEglPresenter : IGpuVideoPresenter {
    private const int RingSize = 3;
    private const int AV_HWFRAME_MAP_READ = 1;
    // GL constants GlConsts does not define.
    private const int GL_TEXTURE_WRAP_S = 0x2802;
    private const int GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_TRIANGLE_STRIP = 0x0005;

    private readonly Compositor _compositor;
    private readonly CompositionDrawingSurface _surface;
    private readonly ICompositionGpuInterop _interop;
    private readonly IOpenGlTextureSharingRenderInterfaceContextFeature _glFeature;
    private readonly IGlContext _ctx;
    private readonly GlInterface _gl;
    private readonly EglNative.Procs _egl;
    private readonly IntPtr _display;
    private readonly bool _hasModifiers;
    private readonly object _lock = new();
    private bool _disposed;

    private int _program, _vbo, _fbo, _uY, _uUV, _uColorspace, _aPos;
    private readonly int[] _planeTex = new int[2];

    private sealed class Slot {
        public ICompositionImportableOpenGlSharedTexture? Texture;
        public ICompositionImportedGpuImage? Imported;
        public Task? Pending;
    }
    private readonly Slot[] _ring = new Slot[RingSize];
    private int _ringIndex;
    private PixelSize _ringSize;

    // GL entry points GlInterface does not wrap.
    private readonly delegate* unmanaged[Cdecl]<int, int, void> _uniform1i;
    private readonly delegate* unmanaged[Cdecl]<int, float, void> _uniform1f;
    private readonly delegate* unmanaged[Cdecl]<int, int, int*, void> _getShaderiv;
    private readonly delegate* unmanaged[Cdecl]<int, int, int*, void> _getProgramiv;
    private readonly delegate* unmanaged[Cdecl]<int, int, int*, byte*, void> _getShaderInfoLog;
    private readonly delegate* unmanaged[Cdecl]<int> _getError;
    private readonly delegate* unmanaged[Cdecl]<int, int, int, int, int, int, void> _texImage2DNoData;

    public AVHWDeviceType DeviceType => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
    public string? PreferredDevice { get; }
    public string Label => "VAAPI zero-copy";
    public string? LastError { get; private set; }
    public PixelSize FrameSize { get; private set; }
    public event Action<PixelSize>? FrameSizeChanged;

    private VaapiEglPresenter(Compositor compositor, CompositionDrawingSurface surface, ICompositionGpuInterop interop,
        IOpenGlTextureSharingRenderInterfaceContextFeature glFeature, IGlContext ctx) {
        _compositor = compositor;
        _surface = surface;
        _interop = interop;
        _glFeature = glFeature;
        _ctx = ctx;
        _gl = ctx.GlInterface;

        using (_ctx.MakeCurrent()) {
            _display = EglNative.GetCurrentDisplay();
            if (_display == IntPtr.Zero) throw new PlatformNotSupportedException("no current EGL display; Avalonia is not using the EGL backend");
            var eglExt = EglNative.QueryString(_display, EglNative.EGL_EXTENSIONS);
            if (!eglExt.Contains("EGL_EXT_image_dma_buf_import")) throw new PlatformNotSupportedException("EGL_EXT_image_dma_buf_import missing");
            _hasModifiers = eglExt.Contains("EGL_EXT_image_dma_buf_import_modifiers");
            var glExt = _gl.GetString(GL_EXTENSIONS) ?? "";
            if (!glExt.Contains("GL_OES_EGL_image")) throw new PlatformNotSupportedException("GL_OES_EGL_image missing");

            _egl = EglNative.Procs.Resolve();
            PreferredDevice = _egl.RenderNodeOf(_display);

            _uniform1i = (delegate* unmanaged[Cdecl]<int, int, void>)Proc("glUniform1i");
            _uniform1f = (delegate* unmanaged[Cdecl]<int, float, void>)Proc("glUniform1f");
            _getShaderiv = (delegate* unmanaged[Cdecl]<int, int, int*, void>)Proc("glGetShaderiv");
            _getProgramiv = (delegate* unmanaged[Cdecl]<int, int, int*, void>)Proc("glGetProgramiv");
            _getShaderInfoLog = (delegate* unmanaged[Cdecl]<int, int, int*, byte*, void>)Proc("glGetShaderInfoLog");
            _getError = (delegate* unmanaged[Cdecl]<int>)Proc("glGetError");

            BuildPipeline();
        }
        for (var i = 0; i < RingSize; i++) _ring[i] = new Slot();
    }

    /// <summary>Creates the presenter or returns null with a reason when this machine cannot do zero-copy.</summary>
    public static VaapiEglPresenter? TryCreate(Compositor compositor, CompositionDrawingSurface surface,
        ICompositionGpuInterop interop, IOpenGlTextureSharingRenderInterfaceContextFeature glFeature, out string? reason) {
        reason = null;
        if (!OperatingSystem.IsLinux()) { reason = "not Linux"; return null; }
        if (!glFeature.CanCreateSharedContext) { reason = "GL backend cannot share contexts"; return null; }
        IGlContext? ctx = null;
        try {
            ctx = glFeature.CreateSharedContext();
            if (ctx == null) { reason = "could not create shared GL context"; return null; }
            return new VaapiEglPresenter(compositor, surface, interop, glFeature, ctx);
        } catch (Exception ex) {
            reason = ex.Message;
            ctx?.Dispose();
            return null;
        }
    }

    private IntPtr Proc(string name) {
        var p = _gl.GetProcAddress(name);
        if (p == IntPtr.Zero) p = EglNative.GetProcAddress(name);
        if (p == IntPtr.Zero) throw new PlatformNotSupportedException($"{name} not available");
        return p;
    }

    private void BuildPipeline() {
        var es = _ctx.Version.Type == GlProfileType.OpenGLES;
        var header = es ? "precision mediump float;\n" : "";
        const string vs =
            "attribute vec2 aPos;\n" +
            "varying vec2 vUv;\n" +
            "void main() {\n" +
            // Avalonia treats shared textures as bottom-left origin; DMA-BUF rows are
            // top-down. Flip V so the picture is upright after the compositor copies it.
            "  vUv = vec2(aPos.x * 0.5 + 0.5, 0.5 - aPos.y * 0.5);\n" +
            "  gl_Position = vec4(aPos, 0.0, 1.0);\n" +
            "}\n";
        var fs = header +
            "varying vec2 vUv;\n" +
            "uniform sampler2D uY;\n" +
            "uniform sampler2D uUV;\n" +
            "uniform float uBt601;\n" +
            "void main() {\n" +
            "  float y = 1.1643 * (texture2D(uY, vUv).r - 0.0625);\n" +
            "  vec2 uv = texture2D(uUV, vUv).rg - 0.5;\n" +
            "  vec3 c709 = vec3(y + 1.5748 * uv.y, y - 0.1873 * uv.x - 0.4681 * uv.y, y + 1.8556 * uv.x);\n" +
            "  vec3 c601 = vec3(y + 1.4020 * uv.y, y - 0.3441 * uv.x - 0.7141 * uv.y, y + 1.7720 * uv.x);\n" +
            "  gl_FragColor = vec4(mix(c709, c601, uBt601), 1.0);\n" +
            "}\n";

        var v = CompileShader(GL_VERTEX_SHADER, vs);
        var f = CompileShader(GL_FRAGMENT_SHADER, fs);
        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, v);
        _gl.AttachShader(_program, f);
        _gl.LinkProgram(_program);
        int ok;
        _getProgramiv(_program, GL_LINK_STATUS, &ok);
        if (ok == 0) throw new InvalidOperationException("GL program link failed");
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        _uY = _gl.GetUniformLocationString(_program, "uY");
        _uUV = _gl.GetUniformLocationString(_program, "uUV");
        _uColorspace = _gl.GetUniformLocationString(_program, "uBt601");
        _aPos = _gl.GetAttribLocationString(_program, "aPos");

        var quad = new float[] { -1, -1, 1, -1, -1, 1, 1, 1 };
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        fixed (float* q = quad) _gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(quad.Length * sizeof(float)), (IntPtr)q, GL_STATIC_DRAW);
        _gl.BindBuffer(GL_ARRAY_BUFFER, 0);

        _fbo = _gl.GenFramebuffer();
        for (var i = 0; i < 2; i++) {
            _planeTex[i] = _gl.GenTexture();
            _gl.BindTexture(GL_TEXTURE_2D, _planeTex[i]);
            _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        }
        _gl.BindTexture(GL_TEXTURE_2D, 0);
    }

    private int CompileShader(int type, string source) {
        var sh = _gl.CreateShader(type);
        _gl.ShaderSourceString(sh, source);
        _gl.CompileShader(sh);
        int ok;
        _getShaderiv(sh, GL_COMPILE_STATUS, &ok);
        if (ok == 0) {
            var buf = stackalloc byte[1024];
            int len;
            _getShaderInfoLog(sh, 1024, &len, buf);
            throw new InvalidOperationException("GL shader compile failed: " + Marshal.PtrToStringAnsi((IntPtr)buf, Math.Max(0, len)));
        }
        return sh;
    }

    // ------------------------------------------------------------------ present

    public bool TryPresent(AVFrame* hwFrame) {
        if (_disposed) return false;
        if (hwFrame->format != (int)AVPixelFormat.AV_PIX_FMT_VAAPI) { LastError = "not a VAAPI frame"; return false; }

        AVFrame* drm = null;
        IntPtr imgY = IntPtr.Zero, imgUV = IntPtr.Zero;
        try {
            var size = new PixelSize(hwFrame->width, hwFrame->height);
            if (!EnsureRing(size)) return false;

            // Pick the next slot; if the compositor still holds it, wait briefly, else
            // drop this frame rather than tear or stall the decoder.
            var slot = _ring[_ringIndex];
            _ringIndex = (_ringIndex + 1) % RingSize;
            if (slot.Pending is { IsCompleted: false } pending && !pending.Wait(100)) return true;

            drm = ffmpeg.av_frame_alloc();
            drm->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
            var r = ffmpeg.av_hwframe_map(drm, hwFrame, AV_HWFRAME_MAP_READ);
            if (r < 0) { LastError = "DRM PRIME export: " + FfmpegNative.ErrorString(r); return false; }
            var desc = (AVDRMFrameDescriptor*)drm->data[0];
            if (desc == null) { LastError = "empty DRM descriptor"; return false; }

            // NV12 arrives either as two layers (R8 + GR88) or one NV12 layer with two
            // planes. Either way, plane 0 is luma at full size and plane 1 is
            // interleaved chroma at half size.
            if (!ResolvePlanes(desc, out var luma, out var chroma, out var lumaObj, out var chromaObj)) return false;

            lock (_lock) {
                if (_disposed) return false;
                using (_ctx.MakeCurrent()) {
                    imgY = CreateImage(luma, lumaObj, DrmFourcc.R8, size.Width, size.Height);
                    imgUV = CreateImage(chroma, chromaObj, DrmFourcc.GR88, (size.Width + 1) / 2, (size.Height + 1) / 2);
                    if (imgY == IntPtr.Zero || imgUV == IntPtr.Zero) {
                        LastError = $"eglCreateImageKHR failed (0x{EglNative.GetError():X})";
                        return false;
                    }

                    _gl.ActiveTexture(GL_TEXTURE0);
                    _gl.BindTexture(GL_TEXTURE_2D, _planeTex[0]);
                    _egl.ImageTargetTexture2DOES(GL_TEXTURE_2D, imgY);
                    _gl.ActiveTexture(GL_TEXTURE0 + 1);
                    _gl.BindTexture(GL_TEXTURE_2D, _planeTex[1]);
                    _egl.ImageTargetTexture2DOES(GL_TEXTURE_2D, imgUV);

                    _gl.BindFramebuffer(GL_FRAMEBUFFER, _fbo);
                    _gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, slot.Texture!.TextureId, 0);
                    if (_gl.CheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE) {
                        LastError = "framebuffer incomplete";
                        return false;
                    }
                    _gl.Viewport(0, 0, size.Width, size.Height);
                    _gl.UseProgram(_program);
                    _uniform1i(_uY, 0);
                    _uniform1i(_uUV, 1);
                    _uniform1f(_uColorspace, IsBt601(hwFrame) ? 1f : 0f);
                    _gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
                    _gl.VertexAttribPointer(_aPos, 2, GL_FLOAT, 0, 0, IntPtr.Zero);
                    _gl.EnableVertexAttribArray(_aPos);
                    _gl.DrawArrays(GL_TRIANGLE_STRIP, 0, 4);
                    _gl.BindBuffer(GL_ARRAY_BUFFER, 0);
                    _gl.BindFramebuffer(GL_FRAMEBUFFER, 0);
                    _gl.BindTexture(GL_TEXTURE_2D, 0);
                    // The compositor copies this texture from its own context; make sure
                    // the GPU has finished writing it before it is handed over.
                    _gl.Finish();
                    var err = _getError();
                    if (err != 0) { LastError = $"GL error 0x{err:X}"; return false; }

                    _egl.DestroyImageKHR(_display, imgY); imgY = IntPtr.Zero;
                    _egl.DestroyImageKHR(_display, imgUV); imgUV = IntPtr.Zero;
                }
            }

            var imported = slot.Imported!;
            slot.Pending = Dispatcher.UIThread.InvokeAsync(() => _surface.UpdateAsync(imported));
            return true;
        } catch (Exception ex) {
            LastError = ex.Message;
            return false;
        } finally {
            if (imgY != IntPtr.Zero || imgUV != IntPtr.Zero) {
                lock (_lock) {
                    if (!_disposed) using (_ctx.MakeCurrent()) {
                        if (imgY != IntPtr.Zero) _egl.DestroyImageKHR(_display, imgY);
                        if (imgUV != IntPtr.Zero) _egl.DestroyImageKHR(_display, imgUV);
                    }
                }
            }
            if (drm != null) ffmpeg.av_frame_free(&drm); // closes the exported fds
        }
    }

    private static bool IsBt601(AVFrame* f) => f->colorspace is AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M;

    private bool ResolvePlanes(AVDRMFrameDescriptor* desc, out AVDRMPlaneDescriptor luma, out AVDRMPlaneDescriptor chroma,
        out AVDRMObjectDescriptor lumaObj, out AVDRMObjectDescriptor chromaObj) {
        luma = chroma = default; lumaObj = chromaObj = default;
        if (desc->nb_layers == 2 && desc->layer0.nb_planes == 1 && desc->layer1.nb_planes == 1) {
            luma = desc->layer0.plane0;
            chroma = desc->layer1.plane0;
        } else if (desc->nb_layers == 1 && desc->layer0.nb_planes == 2) {
            luma = desc->layer0.plane0;
            chroma = desc->layer0.plane1;
        } else {
            LastError = $"unexpected DRM layout ({desc->nb_layers} layers)";
            return false;
        }
        lumaObj = desc->Object(luma.object_index);
        chromaObj = desc->Object(chroma.object_index);
        return true;
    }

    private IntPtr CreateImage(AVDRMPlaneDescriptor plane, AVDRMObjectDescriptor obj, uint fourcc, int width, int height) {
        var attrs = stackalloc int[20];
        var i = 0;
        attrs[i++] = EglNative.EGL_WIDTH; attrs[i++] = width;
        attrs[i++] = EglNative.EGL_HEIGHT; attrs[i++] = height;
        attrs[i++] = EglNative.EGL_LINUX_DRM_FOURCC_EXT; attrs[i++] = (int)fourcc;
        attrs[i++] = EglNative.EGL_DMA_BUF_PLANE0_FD_EXT; attrs[i++] = obj.fd;
        attrs[i++] = EglNative.EGL_DMA_BUF_PLANE0_OFFSET_EXT; attrs[i++] = (int)plane.offset;
        attrs[i++] = EglNative.EGL_DMA_BUF_PLANE0_PITCH_EXT; attrs[i++] = (int)plane.pitch;
        if (_hasModifiers && obj.format_modifier != DrmFourcc.ModInvalid) {
            attrs[i++] = EglNative.EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT; attrs[i++] = (int)(obj.format_modifier & 0xFFFFFFFF);
            attrs[i++] = EglNative.EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT; attrs[i++] = (int)(obj.format_modifier >> 32);
        }
        attrs[i] = EglNative.EGL_NONE;
        return _egl.CreateImageKHR(_display, IntPtr.Zero, EglNative.EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attrs);
    }

    // -------------------------------------------------------------- ring / UI

    private bool EnsureRing(PixelSize size) {
        if (_ringSize == size && _ring[0].Texture != null) return true;
        // Shared textures can be created on this thread; importing them into the
        // compositor must happen on the UI thread.
        try {
            var op = Dispatcher.UIThread.InvokeAsync(() => {
                lock (_lock) {
                    if (_disposed) return false;
                    ReleaseRing();
                    for (var i = 0; i < RingSize; i++) {
                        var tex = _glFeature.CreateSharedTextureForComposition(_ctx, size);
                        _ring[i].Texture = tex;
                        _ring[i].Imported = _interop.ImportImage(tex);
                        _ring[i].Pending = null;
                    }
                    _ringSize = size;
                    return true;
                }
            });
            if (!op.GetTask().Wait(2000)) { LastError = "UI thread busy"; return false; }
            if (!op.GetTask().Result) return false;
            FrameSize = size;
            FrameSizeChanged?.Invoke(size);
            return true;
        } catch (Exception ex) {
            LastError = "ring: " + ex.Message;
            return false;
        }
    }

    private void ReleaseRing() {
        foreach (var s in _ring) {
            if (s.Imported != null) _ = s.Imported.DisposeAsync();
            s.Imported = null;
            s.Texture = null; // owned by the imported image
            s.Pending = null;
        }
        _ringSize = default;
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            try {
                ReleaseRing();
                using (_ctx.MakeCurrent()) {
                    fixed (int* t = _planeTex) _gl.DeleteTextures(2, t);
                    _gl.DeleteFramebuffer(_fbo);
                    _gl.DeleteBuffer(_vbo);
                    _gl.DeleteProgram(_program);
                }
            } catch { }
            _ctx.Dispose();
        }
    }
}
