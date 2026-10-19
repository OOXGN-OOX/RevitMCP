using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace RevitMCP.Loader;

internal static class WindowTheme
{
    public static void Apply(Window window)
    {
        if (SystemParameters.HighContrast) return;
        var style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Setter Property="Padding" Value="14,8"/>
              <Setter Property="Margin" Value="0,0,8,8"/>
              <Setter Property="Background" Value="#363636"/>
              <Setter Property="Foreground" Value="#FAF9F7"/>
              <Setter Property="BorderBrush" Value="#6B7280"/>
              <Setter Property="Template">
                <Setter.Value><ControlTemplate TargetType="Button">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                  </Border>
                </ControlTemplate></Setter.Value>
              </Setter>
              <Style.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#484848"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter Property="Background" Value="#111111"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="#FAF9F7"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#9CA3AF"/><Setter Property="BorderBrush" Value="#484848"/></Trigger>
              </Style.Triggers>
            </Style>
            """);
        window.Resources[typeof(Button)] = style;
    }
}
