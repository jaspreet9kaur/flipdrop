package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

/**
 * App-scoped queue of outgoing transfers. At most two files upload at once; the rest wait.
 * State is exposed as a [StateFlow] for Compose to observe. Nothing is persisted.
 */
object TransferQueue {
    enum class State { QUEUED, SENDING, DONE, FAILED }

    data class Item(
        val id: Long,
        val uri: Uri,
        val name: String,
        val kind: FileKind,
        val size: Long,
        val state: State,
        val sent: Long,
        val savedName: String?,
        val error: String?,
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val gate = Semaphore(2)
    private val ids = AtomicLong(1)

    private val _items = MutableStateFlow<List<Item>>(emptyList())
    val items: StateFlow<List<Item>> = _items.asStateFlow()

    /** What's needed to (re)send one item. Holds no Context. */
    private data class Request(val uri: Uri, val velocityDp: Float, val xFraction: Float)
    private val requests = ConcurrentHashMap<Long, Request>()

    /** Queues each URI and starts it; returns the new item ids in order. */
    fun enqueue(context: Context, uris: List<Uri>, velocityDp: Float = 0f, xFraction: Float = 0.5f): List<Long> {
        val app = context.applicationContext
        val newIds = ArrayList<Long>(uris.size)
        for (uri in uris) {
            val id = ids.getAndIncrement()
            val meta = FileInfo.of(app, uri)
            requests[id] = Request(uri, velocityDp, xFraction)
            _items.update { it + Item(id, uri, meta.name, meta.kind, meta.size, State.QUEUED, 0L, null, null) }
            newIds += id
            launchSend(app, id)
        }
        return newIds
    }

    /** Re-sends a previously failed item. */
    fun retry(context: Context, id: Long) {
        val app = context.applicationContext
        val item = _items.value.firstOrNull { it.id == id } ?: return
        if (item.state != State.FAILED || !requests.containsKey(id)) return
        patch(id) { it.copy(state = State.QUEUED, sent = 0L, error = null, savedName = null) }
        launchSend(app, id)
    }

    /** Drops items that are done or failed. In-flight and queued items stay. */
    fun clearFinished() {
        _items.update { list -> list.filterNot { it.state == State.DONE || it.state == State.FAILED } }
        val live = _items.value.mapTo(HashSet()) { it.id }
        requests.keys.retainAll(live)
    }

    private fun launchSend(app: Context, id: Long) {
        scope.launch {
            gate.withPermit {
                val req = requests[id] ?: return@withPermit
                if (_items.value.none { it.id == id }) return@withPermit // cleared while queued
                patch(id) { it.copy(state = State.SENDING) }
                try {
                    val result = FlipDropClient.sendFile(app, req.uri, req.velocityDp, req.xFraction) { sent, total ->
                        patch(id) { if (it.state == State.SENDING) it.copy(sent = sent, size = if (total > 0) total else it.size) else it }
                    }
                    patch(id) {
                        it.copy(
                            state = State.DONE,
                            savedName = result.savedName,
                            size = if (result.size > 0) result.size else it.size,
                            sent = if (result.size > 0) result.size else it.sent,
                            error = null,
                        )
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    patch(id) { it.copy(state = State.FAILED, error = e.message ?: "Send failed") }
                }
            }
        }
    }

    private inline fun patch(id: Long, crossinline f: (Item) -> Item) {
        _items.update { list -> list.map { if (it.id == id) f(it) else it } }
    }
}
