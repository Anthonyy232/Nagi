using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Nagi.WinUI.Helpers;

public static class ScrollEdgeFade
{
    private const float FadeHeight = 24;
    private const float BlurAmount = 3;
    private const float BlurPadding = 3 * BlurAmount;
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly UISettings UI = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollEdgeFade), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(object), typeof(ScrollEdgeFade), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is State previous) previous.Dispose();
        element.SetValue(StateProperty, (bool)args.NewValue ? new State(element) : null);
    }

    private sealed class State : IDisposable
    {
        private readonly FrameworkElement _element;
        private ScrollViewer? _viewer;
        private ScrollContentPresenter? _presenter;
        private Grid? _parent;
        private Canvas? _overlay;
        private Visual? _sourceVisual;
        private CompositionVisualSurface? _surface;
        private CompositionSurfaceBrush? _sourceBrush;
        private ContainerVisual? _root;
        private SpriteVisual? _center;
        private CompositionEffectFactory? _effectFactory;
        private LoadedImageSurface? _noise;
        private CompositionSurfaceBrush? _noiseBrush;
        private Edge? _top;
        private Edge? _bottom;
        private (double Width, double Height, double Top, double Bottom, double Scale, bool EffectsAllowed) _lastState;

        public State(FrameworkElement element)
        {
            _element = element;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                element.LayoutUpdated += OnLayoutUpdated;
                Attach();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            _element.LayoutUpdated += OnLayoutUpdated;
            Attach();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _element.LayoutUpdated -= OnLayoutUpdated;
            Detach();
        }

        private void Attach()
        {
            if (_viewer is not null || _element.Visibility != Visibility.Visible) return;
            if (_element is Control control) control.ApplyTemplate();
            var viewer = _element as ScrollViewer ?? _element.FindDescendant<ScrollViewer>();
            viewer?.ApplyTemplate();
            var presenter = viewer?.FindDescendant<ScrollContentPresenter>();
            if (viewer is null || presenter is null || VisualTreeHelper.GetParent(presenter) is not Grid parent) return;
            if (presenter.ActualWidth == 0 || presenter.ActualHeight == 0) return;

            _viewer = viewer;
            _presenter = presenter;
            _parent = parent;
            _overlay = new Canvas { IsHitTestVisible = false, Margin = presenter.Margin };
            Grid.SetRow(_overlay, Grid.GetRow(presenter));
            Grid.SetColumn(_overlay, Grid.GetColumn(presenter));
            Grid.SetRowSpan(_overlay, Grid.GetRowSpan(presenter));
            Grid.SetColumnSpan(_overlay, Grid.GetColumnSpan(presenter));
            parent.Children.Insert(parent.Children.IndexOf(presenter) + 1, _overlay);

            _sourceVisual = ElementCompositionPreview.GetElementVisual(presenter);
            var compositor = _sourceVisual.Compositor;
            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = _sourceVisual;
            _sourceBrush = compositor.CreateSurfaceBrush(_surface);
            _center = compositor.CreateSpriteVisual();
            _center.Brush = _sourceBrush;
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _root.IsVisible = false;
            _root.Children.InsertAtTop(_center);

            _noise = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/ScrollEdgeNoise.png"));
            _noiseBrush = compositor.CreateSurfaceBrush(_noise);
            _noiseBrush.Stretch = CompositionStretch.None;
            _noiseBrush.HorizontalAlignmentRatio = 0;
            _noiseBrush.VerticalAlignmentRatio = 0;
            _effectFactory = CreateEffectFactory(compositor);
            _top = new Edge(_sourceVisual, _effectFactory, _noiseBrush, false);
            _bottom = new Edge(_sourceVisual, _effectFactory, _noiseBrush, true);
            _root.Children.InsertAtTop(_top.Visual);
            _root.Children.InsertAtTop(_bottom.Visual);
            ElementCompositionPreview.SetElementChildVisual(_overlay, _root);

            viewer.ViewChanged += OnViewChanged;
            UI.ColorValuesChanged += OnSettingsChanged;
            UI.AdvancedEffectsEnabledChanged += OnSettingsChanged;
            _lastState = default;
            Update();
        }

        private static CompositionEffectFactory CreateEffectFactory(Compositor compositor)
        {
            // Composition requires a tree, so each reference needs a fresh source node.
            static CompositionEffectSourceParameter Content() => new("Content");
            static CompositionEffectSourceParameter EdgeMask() => new("Edge");

            var blurred = new CompositeEffect
            {
                Mode = CanvasComposite.DestinationIn,
                Sources =
                {
                    new GaussianBlurEffect { Source = Content(), BlurAmount = BlurAmount, BorderMode = EffectBorderMode.Hard },
                    EdgeMask()
                }
            };
            var sharp = new CompositeEffect
            {
                Mode = CanvasComposite.DestinationOut,
                Sources = { Content(), EdgeMask() }
            };
            var mixed = new CompositeEffect { Mode = CanvasComposite.Add, Sources = { sharp, blurred } };
            var noise = new CompositeEffect
            {
                Mode = CanvasComposite.DestinationIn,
                Sources =
                {
                    new BorderEffect
                    {
                        Source = new CompositionEffectSourceParameter("Noise"),
                        ExtendX = CanvasEdgeBehavior.Wrap,
                        ExtendY = CanvasEdgeBehavior.Wrap
                    },
                    EdgeMask()
                }
            };
            // Source-atop adds grain without increasing the artwork's coverage or filling transparent pixels.
            var material = new CompositeEffect
            {
                Mode = CanvasComposite.SourceAtop,
                Sources = { mixed, new OpacityEffect { Source = noise, Opacity = 0.02f } }
            };
            return compositor.CreateEffectFactory(new CompositeEffect
            {
                Mode = CanvasComposite.DestinationOut,
                Sources = { material, EdgeMask() }
            });
        }

        private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => Update();
        private void OnLayoutUpdated(object? sender, object args)
        {
            if (_element.Visibility != Visibility.Visible) return;
            Attach();
            Update();
        }
        private void OnSettingsChanged(UISettings sender, object args) => _element.DispatcherQueue.TryEnqueue(Update);

        private void Update()
        {
            if (_viewer is null || _presenter is null || _root is null) return;
            var width = _presenter.ActualWidth;
            var height = _presenter.ActualHeight;
            var top = Math.Clamp(_viewer.VerticalOffset / FadeHeight, 0, 1);
            var bottom = Math.Clamp((_viewer.ScrollableHeight - _viewer.VerticalOffset) / FadeHeight, 0, 1);
            var scale = _element.XamlRoot.RasterizationScale;
            var effectsAllowed = !Accessibility.HighContrast && UI.AdvancedEffectsEnabled &&
                                 _effectFactory!.LoadStatus == CompositionEffectFactoryLoadStatus.Success;
            var state = (width, height, top, bottom, scale, effectsAllowed);
            if (state == _lastState) return;
            _lastState = state;

            var enabled = effectsAllowed && width > 0 && height > 0 && _viewer.ScrollableHeight > 0;
            _root.IsVisible = enabled;
            // VisualSurface captures the source without its own opacity, as in WinUI Gallery's OpacityMaskView.
            _sourceVisual!.Opacity = enabled ? 0 : 1;
            if (!enabled) return;

            var size = new Vector2((float)width, (float)height);
            // Join captures on physical pixel boundaries to avoid a line at fractional display scales.
            var viewportPixels = (int)Math.Round(height * scale);
            var edgePixels = Math.Min((int)Math.Ceiling((FadeHeight + BlurPadding) * scale), viewportPixels / 2);
            var topEnd = (float)(edgePixels / scale);
            var bottomStart = (float)((viewportPixels - edgePixels) / scale);
            _top!.Update(size.X, topEnd, 0, (float)top);
            _bottom!.Update(size.X, size.Y - bottomStart, bottomStart, (float)bottom);
            var topInset = top > 0 ? topEnd : 0;
            var centerEnd = bottom > 0 ? bottomStart : size.Y;
            var centerSize = new Vector2(size.X, centerEnd - topInset);
            _center!.IsVisible = centerSize.Y > 0;
            if (_center.IsVisible && (_center.Size != centerSize || _center.Offset.Y != topInset))
            {
                _center.Size = centerSize;
                _center.Offset = new Vector3(0, topInset, 0);
                _surface!.SourceSize = centerSize;
                _surface.SourceOffset = new Vector2(0, topInset);
            }
        }

        private void Detach()
        {
            if (_viewer is null) return;
            _viewer.ViewChanged -= OnViewChanged;
            UI.ColorValuesChanged -= OnSettingsChanged;
            UI.AdvancedEffectsEnabledChanged -= OnSettingsChanged;
            _sourceVisual!.Opacity = 1;
            ElementCompositionPreview.SetElementChildVisual(_overlay!, null);
            _parent!.Children.Remove(_overlay!);
            _top?.Dispose();
            _bottom?.Dispose();
            _root?.Dispose();
            _center?.Dispose();
            _effectFactory?.Dispose();
            _noiseBrush?.Dispose();
            _noise?.Dispose();
            _sourceBrush?.Dispose();
            _surface?.Dispose();
            _viewer = null;
            _presenter = null;
            _parent = null;
            _overlay = null;
            _sourceVisual = null;
            _top = null;
            _bottom = null;
            _root = null;
            _center = null;
            _effectFactory = null;
            _noiseBrush = null;
            _noise = null;
            _sourceBrush = null;
            _surface = null;
        }

        public void Dispose()
        {
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            _element.LayoutUpdated -= OnLayoutUpdated;
            Detach();
        }
    }

    private sealed class Edge : IDisposable
    {
        private const int FadeSegments = 8;
        private readonly CompositionVisualSurface _content;
        private readonly CompositionSurfaceBrush _contentBrush;
        private readonly CompositionLinearGradientBrush _gradient;
        private readonly SpriteVisual _maskVisual;
        private readonly CompositionVisualSurface _maskSurface;
        private readonly CompositionSurfaceBrush _maskBrush;
        private readonly CompositionEffectBrush _brush;
        private (float Width, float Height, float Y, float Strength) _lastState;

        public Edge(Visual source, CompositionEffectFactory factory, CompositionSurfaceBrush noise, bool bottom)
        {
            var compositor = source.Compositor;
            _content = compositor.CreateVisualSurface();
            _content.SourceVisual = source;
            _contentBrush = compositor.CreateSurfaceBrush(_content);
            _gradient = compositor.CreateLinearGradientBrush();
            _gradient.MappingMode = CompositionMappingMode.Relative;
            _gradient.StartPoint = bottom ? Vector2.UnitY : Vector2.Zero;
            _gradient.EndPoint = bottom ? Vector2.Zero : Vector2.UnitY;
            for (var i = 0; i <= FadeSegments; i++)
                _gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.Transparent));
            _gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));
            _maskVisual = compositor.CreateSpriteVisual();
            _maskVisual.Brush = _gradient;
            _maskSurface = compositor.CreateVisualSurface();
            _maskSurface.SourceVisual = _maskVisual;
            _maskBrush = compositor.CreateSurfaceBrush(_maskSurface);
            _brush = factory.CreateBrush();
            _brush.SetSourceParameter("Content", _contentBrush);
            _brush.SetSourceParameter("Edge", _maskBrush);
            _brush.SetSourceParameter("Noise", noise);
            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = _brush;
            Visual.IsVisible = false;
        }

        public SpriteVisual Visual { get; }

        public void Update(float width, float height, float y, float strength)
        {
            var state = (width, height, y, strength);
            if (state == _lastState) return;
            _lastState = state;
            Visual.IsVisible = strength > 0;
            if (strength <= 0) return;
            var size = new Vector2(width, height);
            if (Visual.Size != size)
            {
                Visual.Size = size;
                _content.SourceSize = size;
                _maskVisual.Size = size;
                _maskSurface.SourceSize = size;
            }
            Visual.Offset = new Vector3(0, y, 0);
            _content.SourceOffset = new Vector2(0, y);
            var fraction = Math.Min(FadeHeight / height, 1);
            for (var i = 0; i <= FadeSegments; i++)
            {
                var position = (float)i / FadeSegments;
                var opacity = position * position * (3 - 2 * position);
                var stop = _gradient.ColorStops[i];
                stop.Offset = fraction * position;
                stop.Color = Windows.UI.Color.FromArgb((byte)Math.Round(255 * strength * (1 - opacity)), 255, 255, 255);
            }
        }

        public void Dispose()
        {
            Visual.Dispose();
            _brush.Dispose();
            _maskBrush.Dispose();
            _maskSurface.Dispose();
            _maskVisual.Dispose();
            _gradient.Dispose();
            _contentBrush.Dispose();
            _content.Dispose();
        }
    }
}
