using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The control templates the friendly-shell design needs and Fluent does not draw (QS277, QS278).
///
/// <para><b>Built in code, never parsed (QS288).</b> They were XAML read by <c>XamlReader</c>, and the
/// window builds the sidebar and the tab strip in its constructor, so the first start loaded WPF's
/// XAML parser on the way to the first paint and the performance gate's start went a third slower. A
/// factory tree and triggers in code are the same template with nothing to load.</para>
///
/// <para><b>A new one per call.</b> A template is sealed on the thread that first applies it, and each
/// window — each test, too — runs on a thread of its own, so a template shared through a static field
/// is refused by the second window that tries it.</para>
/// </summary>
internal static class Templates
{
    /// <summary>The name a template's outer border goes by, which its triggers fill.</summary>
    private const string Ground = "Ground";

    /// <summary>
    /// A button with no fill until the pointer is over it, rounded at 6. Fluent's toggle fills a
    /// checked toggle with the accent, which is the opaque block the design does not have.
    /// </summary>
    public static ControlTemplate Ghost()
    {
        FrameworkElementFactory content = new(typeof(ContentPresenter));

        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        ControlTemplate template = new(typeof(ButtonBase)) { VisualTree = Rounded(content) };

        template.Triggers.Add(Filled(UIElement.IsMouseOverProperty, "SubtleFillColorSecondaryBrush"));
        template.Triggers.Add(Filled(ButtonBase.IsPressedProperty, "SubtleFillColorTertiaryBrush"));

        Trigger disabled = new() { Property = UIElement.IsEnabledProperty, Value = false };

        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(disabled);

        return template;
    }

    /// <summary>
    /// A list row: rounded, a subtle fill under the pointer, and the selection brush where selected
    /// (QS277), which the sidebar keeps under <paramref name="selection"/>.
    /// </summary>
    public static ControlTemplate Row(string selection)
    {
        FrameworkElementFactory content = new(typeof(ContentPresenter));

        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        ControlTemplate template = new(typeof(ListBoxItem)) { VisualTree = Rounded(content) };

        // Selected after the pointer, so a selected row under the pointer still reads as selected.
        template.Triggers.Add(Filled(UIElement.IsMouseOverProperty, "SubtleFillColorSecondaryBrush"));
        template.Triggers.Add(Filled(ListBoxItem.IsSelectedProperty, selection));

        return template;
    }

    /// <summary>A text box with nothing of its own around it, whose frame is the caller's.</summary>
    public static ControlTemplate Bare()
    {
        FrameworkElementFactory host = new(typeof(ScrollViewer), "PART_ContentHost");

        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);

        return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
    }

    /// <summary>
    /// A button drawn as nothing but its content, for a tab-shaped button whose look is its content's:
    /// Home on the strip, which is a tab to the eye and a button to everything else.
    /// </summary>
    public static ControlTemplate Plain() => new(typeof(ButtonBase)) { VisualTree = Clear(new FrameworkElementFactory(typeof(ContentPresenter))) };

    /// <summary>A tab control that is its strip and nothing else: the tabs side by side, no content area under them.</summary>
    public static ControlTemplate Strip()
    {
        FrameworkElementFactory tabs = new(typeof(StackPanel));

        tabs.SetValue(Panel.IsItemsHostProperty, true);
        tabs.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        tabs.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);

        return new ControlTemplate(typeof(TabControl)) { VisualTree = tabs };
    }

    /// <summary>A tab drawn as its header and nothing else; the header carries the dot, the title, the close button and the underline.</summary>
    public static ControlTemplate Tab()
    {
        FrameworkElementFactory header = new(typeof(ContentPresenter));

        header.SetValue(ContentPresenter.ContentSourceProperty, "Header");

        return new ControlTemplate(typeof(TabItem)) { VisualTree = Clear(header) };
    }

    /// <summary>
    /// The terminal glyph the design draws for a server — a rounded rectangle with a prompt in it, on a
    /// 24 grid, stroked at 2 — at a size. Drawn, because the icon fonts' nearest glyph is Command
    /// Prompt's <c>C:\</c>, which reads as a local shell (QS277).
    /// </summary>
    public static Viewbox Terminal(double size, string stroke)
    {
        System.Windows.Shapes.Path drawn = new()
        {
            Data = TerminalShape,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        drawn.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);

        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas { Width = 24, Height = 24, Children = { drawn } },
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static readonly Geometry TerminalShape = Frozen(Geometry.Parse(
        "M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M7,9 L10,12 L7,15"));

    /// <summary>A transparent border named <see cref="Ground"/>, rounded at 6, padded as the control is, around this.</summary>
    private static FrameworkElementFactory Rounded(FrameworkElementFactory inside)
    {
        FrameworkElementFactory ground = new(typeof(Border), Ground);

        ground.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        ground.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        ground.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        ground.AppendChild(inside);

        return ground;
    }

    /// <summary>A transparent border around this, so the whole of it takes the pointer.</summary>
    private static FrameworkElementFactory Clear(FrameworkElementFactory inside)
    {
        FrameworkElementFactory ground = new(typeof(Border));

        ground.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        ground.AppendChild(inside);

        return ground;
    }

    /// <summary>While <paramref name="property"/> is true, <see cref="Ground"/> takes the theme's brush under <paramref name="key"/>.</summary>
    private static Trigger Filled(DependencyProperty property, string key)
    {
        Trigger trigger = new() { Property = property, Value = true };

        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(key), Ground));

        return trigger;
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();

        return geometry;
    }
}
