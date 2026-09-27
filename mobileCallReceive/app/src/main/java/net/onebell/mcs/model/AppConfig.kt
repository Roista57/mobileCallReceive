package net.onebell.mcs.model

data class AppConfig(
    val serverIp: String = "127.0.0.1",
    val serverPort: String = "18080",
    val apiPath: String = "/api/call",
    val enabled: Boolean = false,
) {
    fun validationError(): String? {
        val parts = serverIp.split('.')
        if (parts.size != 4 || parts.any { it.isEmpty() || it.length > 3 ||
                it.any { c -> c !in '0'..'9' } || it.toIntOrNull() !in 0..255 ||
                (it.length > 1 && it.startsWith('0')) }) return "올바른 IPv4 주소를 입력하세요."
        if (serverPort.toIntOrNull() !in 1..65535) return "Port는 1~65535 사이 숫자여야 합니다."
        if (!apiPath.matches(Regex("/[A-Za-z0-9/_-]*")) || apiPath.startsWith("//"))
            return "API Path는 /로 시작하고 영문·숫자·/·_·-만 사용할 수 있습니다."
        return null
    }
}
