using System.Windows.Controls;
using System.Windows.Markup;

namespace Quickshell.App;

/// <summary>
/// The control templates the friendly-shell design needs and Fluent does not draw (QS277, QS278).
///
/// <para><b>A new one per call.</b> A template is sealed on the thread that first applies it, and each
/// window — each test, too — runs on a thread of its own, so a template shared through a static field
/// is refused by the second window that tries it.</para>
/// </summary>
internal static class Templates
{
    /// <summary>
    /// A button with no fill until the pointer is over it, rounded at 6. Fluent's toggle fills a
    /// checked toggle with the accent, which is the opaque block the design does not have.
    /// </summary>
    public static ControlTemplate Ghost() => Parse("""
        <ControlTemplate TargetType="ButtonBase">
          <Border x:Name="Ground" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Ground" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Ground" Property="Background" Value="{DynamicResource SubtleFillColorTertiaryBrush}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    /// <summary>
    /// A button drawn as nothing but its content, for a tab-shaped button whose look is its content's:
    /// Home on the strip, which is a tab to the eye and a button to everything else.
    /// </summary>
    public static ControlTemplate Plain() => Parse("""
        <ControlTemplate TargetType="ButtonBase">
          <Border Background="Transparent">
            <ContentPresenter />
          </Border>
        </ControlTemplate>
        """);

    /// <summary>A tab control that is its strip and nothing else: the tabs side by side, no content area under them.</summary>
    public static ControlTemplate Strip() => Parse("""
        <ControlTemplate TargetType="TabControl">
          <StackPanel IsItemsHost="True" Orientation="Horizontal" VerticalAlignment="Bottom" />
        </ControlTemplate>
        """);

    /// <summary>A tab drawn as its header and nothing else; the header carries the dot, the title, the close button and the underline.</summary>
    public static ControlTemplate Tab() => Parse("""
        <ControlTemplate TargetType="TabItem">
          <Border Background="Transparent">
            <ContentPresenter ContentSource="Header" />
          </Border>
        </ControlTemplate>
        """);

    /// <summary>
    /// The terminal glyph the design draws for a server — a rounded rectangle with a prompt in it, on a
    /// 24 grid, stroked at 2 — at a size. Drawn, because the icon fonts' nearest glyph is Command
    /// Prompt's <c>C:\</c>, which reads as a local shell (QS277).
    /// </summary>
    public static System.Windows.Controls.Viewbox Terminal(double size, string stroke)
    {
        System.Windows.Shapes.Path drawn = new()
        {
            Data = TerminalShape,
            StrokeThickness = 2,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
        };

        drawn.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);

        return new System.Windows.Controls.Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas { Width = 24, Height = 24, Children = { drawn } },
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
    }

    private static readonly System.Windows.Media.Geometry TerminalShape = Frozen(System.Windows.Media.Geometry.Parse(
        "M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M7,9 L10,12 L7,15"));

    private static System.Windows.Media.Geometry Frozen(System.Windows.Media.Geometry geometry)
    {
        geometry.Freeze();

        return geometry;
    }

    private static ControlTemplate Parse(string xaml) =>
        (ControlTemplate)XamlReader.Parse(xaml.Replace(
            "<ControlTemplate ",
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ",
            StringComparison.Ordinal));
}
