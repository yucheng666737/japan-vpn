package com.jpvpn.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.jpvpn.app.ui.theme.SignalAmber
import com.jpvpn.app.ui.theme.SignalGreen
import com.jpvpn.app.ui.theme.SignalRed
import java.util.Locale

@Composable
fun JapanFlagIcon(
    modifier: Modifier = Modifier,
    width: Dp = 44.dp,
    height: Dp = 30.dp
) {
    val borderColor = MaterialTheme.colorScheme.outline
    Box(
        modifier = modifier
            .width(width)
            .height(height)
            .clip(RoundedCornerShape(6.dp))
            .background(Color.White)
            .border(1.dp, borderColor, RoundedCornerShape(6.dp))
    ) {
        Canvas(modifier = Modifier.matchParentSize()) {
            drawCircle(
                color = Color(0xFFBC002D),
                radius = size.minDimension * 0.30f,
                center = Offset(size.width / 2f, size.height / 2f)
            )
        }
    }
}

@Composable
fun SignalBarsIndicator(
    bars: Int,
    modifier: Modifier = Modifier
) {
    val activeColor = when {
        bars >= 4 -> SignalGreen
        bars >= 2 -> SignalAmber
        else -> SignalRed
    }
    val inactiveColor = MaterialTheme.colorScheme.outline

    Canvas(
        modifier = modifier
            .width(34.dp)
            .height(22.dp)
    ) {
        val barCount = 5
        val gap = 3.dp.toPx()
        val barWidth = (size.width - gap * (barCount - 1)) / barCount
        for (i in 0 until barCount) {
            val fraction = (i + 1) / barCount.toFloat()
            val barHeight = size.height * (0.28f + 0.72f * fraction)
            val left = i * (barWidth + gap)
            val top = size.height - barHeight
            drawRoundRect(
                color = if (i < bars) activeColor else inactiveColor,
                topLeft = Offset(left, top),
                size = Size(barWidth, barHeight),
                cornerRadius = CornerRadius(barWidth / 2f, barWidth / 2f)
            )
        }
    }
}

fun formatDuration(totalSeconds: Long): String {
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return String.format(Locale.US, "%02d:%02d:%02d", hours, minutes, seconds)
}

fun formatSpeed(bytesPerSec: Long): String {
    return when {
        bytesPerSec >= 1_048_576L -> String.format(Locale.US, "%.1f MB/s", bytesPerSec / 1_048_576.0)
        bytesPerSec >= 1_024L -> String.format(Locale.US, "%.1f KB/s", bytesPerSec / 1_024.0)
        else -> "$bytesPerSec B/s"
    }
}

fun formatUpdatedTime(timestampMillis: Long): String {
    if (timestampMillis <= 0L) return "尚未更新"
    val fmt = java.text.SimpleDateFormat("HH:mm:ss", Locale.getDefault())
    return fmt.format(java.util.Date(timestampMillis))
}
