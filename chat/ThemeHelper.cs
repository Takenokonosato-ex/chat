using Microsoft.UI.Xaml;

namespace chat
{
    public static class ThemeHelper
    {
        public static ElementTheme CurrentTheme { get; private set; } = ElementTheme.Dark;

        public static void SetTheme(FrameworkElement rootElement, ElementTheme theme)
        {
            rootElement.RequestedTheme = theme;
            CurrentTheme = theme;
        }

        public static ElementTheme ToggleTheme(FrameworkElement rootElement)
        {
            var newTheme = CurrentTheme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;
            SetTheme(rootElement, newTheme);
            return newTheme;
        }
    }
}
