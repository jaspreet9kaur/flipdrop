package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
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

    /** The coroutine driving each in-flight/queued item, so it can be cancelled mid-transfer. */
    private val jobs = ConcurrentHashMap<Long, Job>()

    /**
     * Queues each URI and starts it; returns the new item ids in order. Returns immediately: the
     * placeholder name/kind are derived from the URI's last path segment (no I/O), and the real
     * [FileInfo] (which runs a synchronous ContentResolver query) is resolved on Dispatchers.IO
     * inside the item's own coroutine. This keeps the caller's thread — often the main thread —
     * free of per-URI provider queries (no ANR).
     */
    fun enqueue(context: Context, uris: List<Uri>, velocityDp: Float = 0f, xFraction: Float = 0.5f): List<Long> {
        val app = context.applicationContext
        val newIds = ArrayList<Long>(uris.size)
        for (uri in uris) {
            val id = ids.getAndIncrement()
            val placeholder = placeholderName(uri)
            requests[id] = Request(uri, velocityDp, xFraction)
            _items.update {
                it + Item(id, uri, placeholder, FileInfo.kindOf(null, placeholder), -1L, State.QUEUED, 0L, null, null)
            }
            newIds += id
            launchSend(app, id)
        }
        return newIds
    }

    /** Re-sends a previously failed item. The metadata is re-resolved off the main thread too. */
    fun retry(context: Context, id: Long) {
        val app = context.applicationContext
        val item = _items.value.firstOrNull { it.id == id } ?: return
        if (item.state != State.FAILED || !requests.containsKey(id)) return
        patch(id) { it.copy(state = State.QUEUED, sent = 0L, error = null, savedName = null) }
        launchSend(app, id)
    }

    /** Cancels an in-flight or queued item: stops its coroutine and marks it FAILED("Cancelled"). */
    fun cancel(id: Long) {
        val item = _items.value.firstOrNull { it.id == id } ?: return
        if (item.state == State.DONE || item.state == State.FAILED) return
        jobs[id]?.cancel()
        patch(id) { it.copy(state = State.FAILED, error = "Cancelled") }
    }

    /** Drops items that are done or failed. In-flight and queued items stay. */
    fun clearFinished() {
        _items.update { list -> list.filterNot { it.state == State.DONE || it.state == State.FAILED } }
        val live = _items.value.mapTo(HashSet()) { it.id }
        requests.keys.retainAll(live)
        jobs.keys.retainAll(live)
    }

    /** Name for a freshly queued item before any provider query: the URI's last path segment. */
    private fun placeholderName(uri: Uri): String =
        uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() } ?: "file"

    private fun launchSend(app: Context, id: Long) {
        // LAZY so jobs[id] is set before the body can run (and before any cancel() can arrive).
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                // Resolve the real metadata off the main thread (this coroutine runs on IO).
                requests[id]?.let { req ->
                    val meta = FileInfo.of(app, req.uri)
                    patch(id) {
                        if (it.state == State.QUEUED) it.copy(name = meta.name, kind = meta.kind, size = meta.size) else it
                    }
                }
                gate.withPermit {
                    val req = requests[id] ?: return@withPermit
                    if (_items.value.none { it.id == id }) return@withPermit // cleared while queued
                    patch(id) { it.copy(state = State.SENDING) }
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
                }
            } catch (e: CancellationException) {
                // cancel(id) has already (or will) mark the item FAILED("Cancelled").
                throw e
            } catch (e: Exception) {
                patch(id) { it.copy(state = State.FAILED, error = e.message ?: "Send failed") }
            } finally {
                jobs.remove(id)
            }
        }
        jobs[id] = job
        job.start()
    }

    private inline fun patch(id: Long, crossinline f: (Item) -> Item) {
        _items.update { list -> list.map { if (it.id == id) f(it) else it } }
    }
}
