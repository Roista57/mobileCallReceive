package net.onebell.mcs.model

import java.time.OffsetDateTime
import java.util.UUID

data class CallEvent(
    val eventId: String = UUID.randomUUID().toString(),
    val phoneNumber: String,
    val receivedAt: String = OffsetDateTime.now().toString(),
    val isTest: Boolean = false,
    val blocked: Boolean = false,
)
