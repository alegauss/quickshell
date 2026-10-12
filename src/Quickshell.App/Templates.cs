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

    private static ControlTemplate Parse(string xaml) =>
        (ControlTemplate)XamlReader.Parse(xaml.Replace(
            "<ControlTemplate ",
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ",
            StringComparison.Ordinal));
}
