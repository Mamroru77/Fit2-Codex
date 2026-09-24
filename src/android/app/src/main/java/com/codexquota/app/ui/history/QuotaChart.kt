package com.codexquota.app.ui.history

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp

/**
 * A minimal single-series line chart.
 *
 * It is drawn with a `Canvas` rather than pulled from a charting library: the requirement is one
 * series, real gaps, and no interpolation, which is a few lines of geometry and would otherwise be a
 * heavy dependency.
 *
 * The one rule that matters: each [ChartSegment] is a separate path, so a gap in the data is a gap in
 * the drawing rather than a line joining two points that were never adjacent.
 */
@Composable
fun QuotaChart(
    model: ChartModel,
    modifier: Modifier = Modifier,
    lineColor: Color = MaterialTheme.colorScheme.primary,
) {
    val strokeWidth = 2.dp

    Canvas(
        modifier = modifier
            .fillMaxWidth()
            .height(180.dp),
    ) {
        if (model.isEmpty) {
            return@Canvas
        }

        val startsAt = model.startsAt ?: return@Canvas
        val endsAt = model.endsAt ?: return@Canvas

        // A single-sample series has no time span, so it is drawn as a flat line rather than
        // dividing by zero.
        val span = (endsAt.toEpochMilli() - startsAt.toEpochMilli()).coerceAtLeast(1L).toFloat()

        // The vertical range is padded so a flat series is not drawn on the very edge of the canvas.
        val low = (model.minPercent - 5.0).coerceAtLeast(0.0)
        val high = (model.maxPercent + 5.0).coerceAtMost(100.0)
        val range = (high - low).coerceAtLeast(1.0)

        model.segments.forEach { segment ->
            if (segment.points.isEmpty()) {
                return@forEach
            }

            val path = Path()
            var started = false

            segment.points.forEach { point ->
                val x = ((point.timestamp.toEpochMilli() - startsAt.toEpochMilli()) / span) * size.width
                val y = size.height - (((point.percent - low) / range) * size.height).toFloat()

                if (started) {
                    path.lineTo(x, y)
                } else {
                    path.moveTo(x, y)
                    started = true
                }
            }

            drawPath(path = path, color = lineColor, style = Stroke(width = strokeWidth.toPx()))
        }
    }
}
