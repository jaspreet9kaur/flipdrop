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
        val openPair = intent.getBooleanExtra(EXTRA_PAIR, false) && PairingStore.load(this) == null
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                FlingScreen(onBack = { finish() }, openPairInitially = openPair)
            }
        }
    }

    companion object {
        const val EXTRA_PAIR = "pair"
    }
}
