package io.github.jaspreet9kaur.flipdrop.fling

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme

class FlingActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val openPair = intent.getBooleanExtra(EXTRA_PAIR, false)
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                FlingScreen(onBack = { finish() })
                if (openPair && PairingStore.load(this) == null) {
                    // The share sheet sent us here because nothing is paired yet.
                    val show = androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf(true) }
                    if (show.value) PairDialog(onDismiss = { show.value = false }, onPaired = { show.value = false })
                }
            }
        }
    }

    companion object {
        const val EXTRA_PAIR = "pair"
    }
}