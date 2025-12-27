using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;

namespace Nagi.WinUI.Behaviors;

/// <summary>
/// Attached behaviors for applying Composition-based animations via XAML.
/// These animations run on the compositor thread, ensuring 60 FPS even when the UI thread is busy.
/// </summary>
public static class AnimationBehaviors
{
    #region EnableScaleAnimation Attached Property

    /// <summary>
    /// Gets whether scale animation is enabled on hover/press.
    /// </summary>
    public static bool GetEnableScaleAnimation(DependencyObject obj)
        => (bool)obj.GetValue(EnableScaleAnimationProperty);

    /// <summary>
    /// Sets whether scale animation is enabled on hover/press.
    /// </summary>
    public static void SetEnableScaleAnimation(DependencyObject obj, bool value)
        => obj.SetValue(EnableScaleAnimationProperty, value);

    public static readonly DependencyProperty EnableScaleAnimationProperty =
        DependencyProperty.RegisterAttached(
            "EnableScaleAnimation",
            typeof(bool),
            typeof(AnimationBehaviors),
            new PropertyMetadata(false, OnEnableScaleAnimationChanged));

    private static void OnEnableScaleAnimationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Loaded += Element_LoadedForScale;
        }
    }

    private static void Element_LoadedForScale(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Loaded -= Element_LoadedForScale;

        var hoverScale = GetHoverScale(element);
        var pressedScale = GetPressedScale(element);

        SetupScaleImplicitAnimations(element, hoverScale, pressedScale);
    }

    #endregion

    #region HoverScale and PressedScale Properties

    public static double GetHoverScale(DependencyObject obj)
        => (double)obj.GetValue(HoverScaleProperty);

    public static void SetHoverScale(DependencyObject obj, double value)
        => obj.SetValue(HoverScaleProperty, value);

    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached(
            "HoverScale",
            typeof(double),
            typeof(AnimationBehaviors),
            new PropertyMetadata(1.1d));

    public static double GetPressedScale(DependencyObject obj)
        => (double)obj.GetValue(PressedScaleProperty);

    public static void SetPressedScale(DependencyObject obj, double value)
        => obj.SetValue(PressedScaleProperty, value);

    public static readonly DependencyProperty PressedScaleProperty =
        DependencyProperty.RegisterAttached(
            "PressedScale",
            typeof(double),
            typeof(AnimationBehaviors),
            new PropertyMetadata(0.95d));

    #endregion

    #region EnableTiltAnimation Attached Property

    public static bool GetEnableTiltAnimation(DependencyObject obj)
        => (bool)obj.GetValue(EnableTiltAnimationProperty);

    public static void SetEnableTiltAnimation(DependencyObject obj, bool value)
        => obj.SetValue(EnableTiltAnimationProperty, value);

    public static readonly DependencyProperty EnableTiltAnimationProperty =
        DependencyProperty.RegisterAttached(
            "EnableTiltAnimation",
            typeof(bool),
            typeof(AnimationBehaviors),
            new PropertyMetadata(false, OnEnableTiltAnimationChanged));

    private static void OnEnableTiltAnimationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Loaded += Element_LoadedForTilt;
        }
    }

    private static void Element_LoadedForTilt(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Loaded -= Element_LoadedForTilt;
        SetupTiltAnimation(element);
    }

    #endregion

    #region Animation Implementation

    private static void SetupScaleImplicitAnimations(UIElement element, double hoverScale, double pressedScale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        float hScale = (float)hoverScale;
        float pScale = (float)pressedScale;

        // Use spring animation for natural feel
        var springAnimation = compositor.CreateSpringVector3Animation();
        springAnimation.DampingRatio = 0.65f;
        springAnimation.Period = TimeSpan.FromMilliseconds(50);
        springAnimation.Target = "Scale";

        // Merge with existing implicit animations if any
        var implicitAnimations = visual.ImplicitAnimations ?? compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Scale"] = springAnimation;
        visual.ImplicitAnimations = implicitAnimations;

        // Track whether we're over the element
        bool isPointerOver = false;
        bool isPressed = false;

        void UpdateCenterPoint()
        {
            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                visual.CenterPoint = new Vector3((float)(fe.ActualWidth / 2), (float)(fe.ActualHeight / 2), 0);
            }
        }

        void UpdateScale()
        {
            UpdateCenterPoint();

            if (isPressed)
            {
                visual.Scale = new Vector3(pScale, pScale, 1.0f);
            }
            else if (isPointerOver)
            {
                visual.Scale = new Vector3(hScale, hScale, 1.0f);
            }
            else
            {
                visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);
            }
        }

        PointerEventHandler enteredHandler = (s, e) => { isPointerOver = true; UpdateScale(); };
        PointerEventHandler exitedHandler = (s, e) => { isPointerOver = false; isPressed = false; UpdateScale(); };
        PointerEventHandler pressedHandler = (s, e) => { isPressed = true; UpdateScale(); };
        PointerEventHandler releasedHandler = (s, e) => { isPressed = false; UpdateScale(); };
        PointerEventHandler captureLostHandler = (s, e) => { isPressed = false; UpdateScale(); };
        SizeChangedEventHandler sizeChangedHandler = (s, e) => UpdateCenterPoint();

        element.PointerEntered += enteredHandler;
        element.PointerExited += exitedHandler;
        element.PointerPressed += pressedHandler;
        element.PointerReleased += releasedHandler;
        element.PointerCaptureLost += captureLostHandler;

        if (element is FrameworkElement frameworkElement)
        {
            frameworkElement.SizeChanged += sizeChangedHandler;

            // Cleanup on Unload
            RoutedEventHandler? unloadedHandler = null;
            unloadedHandler = (s, e) =>
            {
                frameworkElement.Unloaded -= unloadedHandler;
                element.PointerEntered -= enteredHandler;
                element.PointerExited -= exitedHandler;
                element.PointerPressed -= pressedHandler;
                element.PointerReleased -= releasedHandler;
                element.PointerCaptureLost -= captureLostHandler;
                frameworkElement.SizeChanged -= sizeChangedHandler;

                try
                {
                    var v = ElementCompositionPreview.GetElementVisual(element);
                    if (v != null)
                    {
                        v.ImplicitAnimations = null;
                        v.Scale = new Vector3(1.0f, 1.0f, 1.0f);
                    }
                }
                catch { /* Best effort cleanup */ }
            };
            frameworkElement.Unloaded += unloadedHandler;
        }
    }

    private static void SetupTiltAnimation(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        const float maxTiltDegrees = 4f;

        // Create spring animation for smooth return
        var rotationXAnimation = compositor.CreateSpringScalarAnimation();
        rotationXAnimation.DampingRatio = 0.5f;
        rotationXAnimation.Period = TimeSpan.FromMilliseconds(50);
        rotationXAnimation.Target = "RotationAngleInDegrees";

        // Merge with existing implicit animations if any
        var implicitAnimations = visual.ImplicitAnimations ?? compositor.CreateImplicitAnimationCollection();
        implicitAnimations["RotationAngleInDegrees"] = rotationXAnimation;
        visual.ImplicitAnimations = implicitAnimations;

        PointerEventHandler movedHandler = (s, e) =>
        {
            // Guard against division by zero before layout completes
            if (element.ActualSize.X == 0 || element.ActualSize.Y == 0)
                return;

            var pointer = e.GetCurrentPoint(element);
            var position = pointer.Position;

            float centerX = (float)element.ActualSize.X / 2;
            float centerY = (float)element.ActualSize.Y / 2;

            // Calculate tilt based on pointer position relative to center
            float normalizedX = ((float)position.X - centerX) / centerX;
            float normalizedY = ((float)position.Y - centerY) / centerY;

            // Clamp to avoid extreme values at edges
            normalizedX = Math.Clamp(normalizedX, -1f, 1f);
            normalizedY = Math.Clamp(normalizedY, -1f, 1f);

            // Set up rotation 
            visual.CenterPoint = new Vector3(centerX, centerY, 0);
            visual.RotationAxis = new Vector3(-normalizedY, normalizedX, 0);
            visual.RotationAngleInDegrees = MathF.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY) * maxTiltDegrees;
        };

        PointerEventHandler exitedHandler = (s, e) =>
        {
            visual.RotationAngleInDegrees = 0;
        };

        element.PointerMoved += movedHandler;
        element.PointerExited += exitedHandler;

        if (element is FrameworkElement frameworkElement)
        {
            // Cleanup on Unload
            RoutedEventHandler? unloadedHandler = null;
            unloadedHandler = (s, e) =>
            {
                frameworkElement.Unloaded -= unloadedHandler;
                element.PointerMoved -= movedHandler;
                element.PointerExited -= exitedHandler;

                try
                {
                    var v = ElementCompositionPreview.GetElementVisual(element);
                    if (v != null)
                    {
                        v.ImplicitAnimations = null;
                        v.RotationAngleInDegrees = 0;
                    }
                }
                catch { /* Best effort cleanup */ }
            };
            frameworkElement.Unloaded += unloadedHandler;
        }
    }

    #endregion
}
