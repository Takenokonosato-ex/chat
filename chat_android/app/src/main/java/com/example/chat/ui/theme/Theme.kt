package com.example.chat.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext

private val DiscordColorScheme = darkColorScheme(
    primary = DiscordBlurple,
    secondary = DiscordSuccess,
    tertiary = DiscordDanger,
    background = DiscordBackground,
    surface = DiscordSidebar,
    onPrimary = DiscordTextPrimary,
    onSecondary = DiscordTextPrimary,
    onTertiary = DiscordTextPrimary,
    onBackground = DiscordTextPrimary,
    onSurface = DiscordTextPrimary,
    surfaceVariant = DiscordDarkBackground,
    onSurfaceVariant = DiscordTextSecondary
)

@Composable
fun ChatTheme(
    darkTheme: Boolean = true, // Force dark theme for Discord style
    // Dynamic color is available on Android 12+
    dynamicColor: Boolean = false, // Disable dynamic colors to keep Discord style
    content: @Composable () -> Unit
) {
    val colorScheme = DiscordColorScheme

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content
    )
}