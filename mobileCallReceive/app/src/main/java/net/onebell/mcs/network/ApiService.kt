package net.onebell.mcs.network

import net.onebell.mcs.model.CallEvent
import retrofit2.Response
import retrofit2.http.*

data class CallEventRequest(
    val schemaVersion: Int = 1,
    val eventId: String,
    val phoneNumber: String,
    val receivedAt: String,
    val sentAt: String,
    val isTest: Boolean,
) {
    companion object {
        fun from(event: CallEvent, sentAt: String) = CallEventRequest(
            eventId = event.eventId, phoneNumber = event.phoneNumber,
            receivedAt = event.receivedAt, sentAt = sentAt, isTest = event.isTest)
    }
}
data class CallEventResponse(val success: Boolean = false, val eventId: String? = null)
data class HealthResponse(val success: Boolean = false, val status: String? = null,
    val serverTime: String? = null, val version: String? = null)

interface ApiService {
    @GET suspend fun health(@Url url: String): Response<HealthResponse>
    @POST suspend fun send(@Url url: String, @Body event: CallEventRequest): Response<CallEventResponse>
}
