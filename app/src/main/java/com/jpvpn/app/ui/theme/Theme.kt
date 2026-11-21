package com.jpvpn.app.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val JapanCrimson = Color(0xFFC62828)
val JapanCrimsonSoft = Color(0xFFFFE4E6)
val ConnectedGreen = Color(0xFF16A34A)
val ConnectedGreenSoft = Color(0xFFDCFCE7)
val ConnectingAmber = Color(0xFFEA580C)
val ConnectingAmberSoft = Color(0xFFFFEDD5)
val SignalGreen = Color(0xFF22C55E)
val SignalAmber = Color(0xFFF59E0B)
val SignalRed = Color(0xFFEF4444)

private val LightColors = lightColorScheme(
    primary = JapanCrimson,
    onPrimary = Color.White,
    primaryContainer = JapanCrimsonSoft,
    onPrimaryContainer = Color(0xFF7F1D1D),
    secondary = ConnectedGreen,
    onSecondary = Color.White,
    background = Color(0xFFF8F9FA),
    onBackground = Color(0xFF111827),
    surface = Color.White,
    onSurface = Color(0xFF111827),
    surfaceVariant = Color(0xFFF1F3F5),
    onSurfaceVariant = Color(0xFF6B7280),
    outline = Color(0xFFE5E7EB)
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFEF4444),
    onPrimary = Color.White,
    primaryContainer = Color(0xFF451A1A),
    onPrimaryContainer = Color(0xFFFECACA),
    secondary = Color(0xFF22C55E),
    onSecondary = Color.Black,
    background = Color(0xFF0F1115),
    onBackground = Color(0xFFF3F4F6),
    surface = Color(0xFF1A1D24),
    onSurface = Color(0xFFF3F4F6),
    surfaceVariant = Color(0xFF252A34),
    onSurfaceVariant = Color(0xFF9CA3AF),
    outline = Color(0xFF2E3440)
)

@Composable
fun JapanVpnTheme(
    darkTheme: Boolean,
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = if (darkTheme) DarkColors else LightColors,
        content = content
    )
}
