using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using DoorBird.App.Video.Gpu;
using DoorBird.App.Video.Gpu.Linux;

namespace DoorBird.App.Views;

/// <summary>
/// Hosts a composition surface that a GPU presenter draws decoded video into.
/// On attach it builds the platform presenter (Linux: VAAPI over EGL) and exposes it
/// through <see cref="PresenterProperty"/>, which the live view binds one-way-to-source
/// so the view model can hand it to the decoder. Frames are letterboxed to the
/// control's bounds like Image with Stretch=Uniform.
/// </summary>
public class VideoSurface : Control {
    public static readonly StyledProperty<IGpuVideoPresenter?> PresenterProperty =
        AvaloniaProperty.Register<VideoSurface, IGpuVideoPresenter?>(nameof(Presenter), defaultBindingMode: BindingMode.OneWayToSource);

    public static readonly StyledProperty<string?> UnavailableReasonProperty =
        AvaloniaProperty.Register<VideoSurface, string?>(nameof(UnavailableReason), defaultBindingMode: BindingMode.OneWayToSource);

    /// <summary>When false the surface visual collapses so a software frame underneath shows through.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<VideoSurface, bool>(nameof(IsActive));

    public IGpuVideoPresenter? Presenter {
        get => GetValue(PresenterProperty);
        set => SetValue(PresenterProperty, value);
    }

    public string? UnavailableReason {
        get => GetValue(UnavailableReasonProperty);
        set => SetValue(UnavailableReasonProperty, value);
    }

    public bool IsActive {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private VaapiEglPresenter? _presenter;
    private PixelSize _frameSize;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _ = InitializeAsync();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e) {
        TearDown();
        base.OnDetachedFromLogicalTree(e);
    }

    private async System.Threading.Tasks.Task InitializeAsync() {
        try {
            var selfVisual = ElementComposition.GetElementVisual(this);
            if (selfVisual == null) { UnavailableReason = "no composition visual"; return; }
            var compositor = selfVisual.Compositor;
            _surface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            ElementComposition.SetElementChildVisual(this, _visual);

            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop == null) { UnavailableReason = "compositor has no GPU interop on this backend"; return; }
            var glFeature = await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature))
                as IOpenGlTextureSharingRenderInterfaceContextFeature;

            // Diagnostic escape hatch for broken drivers: skips the zero-copy presenter so
            // Auto falls back to software and Enabled uses hardware decode with a copy.
            if (Environment.GetEnvironmentVariable("DOORBIRD_DISABLE_ZERO_COPY") is "1" or "true") {
                UnavailableReason = "zero-copy disabled by DOORBIRD_DISABLE_ZERO_COPY";
                Video.VideoLog.Write("surface", UnavailableReason);
                return;
            }

            if (OperatingSystem.IsLinux() && glFeature != null) {
                _presenter = VaapiEglPresenter.TryCreate(compositor, _surface, interop, glFeature, out var reason);
                if (_presenter == null) { UnavailableReason = reason; Video.VideoLog.Write("surface", $"no presenter: {reason}"); return; }
                Video.VideoLog.Write("surface", $"presenter ready, device={_presenter.PreferredDevice ?? "default"}");
                _presenter.FrameSizeChanged += size => Dispatcher.UIThread.Post(() => { _frameSize = size; UpdateLayout(); });
                Presenter = _presenter;
                UnavailableReason = null;
            } else {
                UnavailableReason = OperatingSystem.IsLinux()
                    ? "OpenGL texture sharing not available"
                    : "zero-copy presenter not implemented for this platform yet";
                Video.VideoLog.Write("surface", UnavailableReason);
            }
        } catch (Exception ex) {
            UnavailableReason = ex.Message;
            Video.VideoLog.Write("surface", $"init failed: {ex}");
        }
    }

    private void TearDown() {
        Presenter = null;
        _presenter?.Dispose();
        _presenter = null;
        _surface?.Dispose();
        _surface = null;
        _visual = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == IsActiveProperty) UpdateLayout();
    }

    private void UpdateLayout() {
        if (_visual == null) return;
        if (!IsActive || _frameSize.Width == 0 || _frameSize.Height == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) {
            _visual.Size = new Vector(0, 0);
            return;
        }
        var scale = Math.Min(Bounds.Width / _frameSize.Width, Bounds.Height / _frameSize.Height);
        var w = _frameSize.Width * scale;
        var h = _frameSize.Height * scale;
        _visual.Offset = new Vector3D((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, 0);
        _visual.Size = new Vector(w, h);
    }
}
