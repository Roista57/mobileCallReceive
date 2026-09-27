import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

val versionFile = rootProject.file("../version.properties").canonicalFile
val versionProperties = Properties().apply {
    require(versionFile.isFile) { "공통 버전 파일을 찾을 수 없습니다: $versionFile" }
    versionFile.inputStream().use(::load)
}
val releaseVersionName = versionProperties.getProperty("versionName")
    ?.takeIf { it.matches(Regex("\\d+\\.\\d+\\.\\d+")) }
    ?: error("version.properties의 versionName은 x.y.z 형식이어야 합니다.")
val releaseVersionCode = versionProperties.getProperty("versionCode")
    ?.toIntOrNull()
    ?.takeIf { it > 0 }
    ?: error("version.properties의 versionCode는 양의 정수여야 합니다.")

val signingEnvironment = mapOf(
    "ANDROID_KEYSTORE_FILE" to System.getenv("ANDROID_KEYSTORE_FILE"),
    "ANDROID_KEYSTORE_PASSWORD" to System.getenv("ANDROID_KEYSTORE_PASSWORD"),
    "ANDROID_KEY_ALIAS" to System.getenv("ANDROID_KEY_ALIAS"),
    "ANDROID_KEY_PASSWORD" to System.getenv("ANDROID_KEY_PASSWORD"),
)
val hasReleaseSigning = signingEnvironment.values.all { !it.isNullOrBlank() }

android {
    namespace = "net.onebell.mcs"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "net.onebell.mcs"
        minSdk = 29
        targetSdk = 37
        versionCode = releaseVersionCode
        versionName = releaseVersionName

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = file(signingEnvironment.getValue("ANDROID_KEYSTORE_FILE")!!)
                storePassword = signingEnvironment.getValue("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = signingEnvironment.getValue("ANDROID_KEY_ALIAS")
                keyPassword = signingEnvironment.getValue("ANDROID_KEY_PASSWORD")
            }
        }
    }
    buildTypes {
        release {
            if (hasReleaseSigning) {
                signingConfig = signingConfigs.getByName("release")
            }
            optimization {
                enable = false
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
}

val verifyReleaseSigning by tasks.registering {
    group = "verification"
    description = "배포용 Android 서명 환경과 keystore를 확인합니다."
    doLast {
        val missing = signingEnvironment.filterValues { it.isNullOrBlank() }.keys
        check(missing.isEmpty()) {
            "release APK 서명 정보가 없습니다: ${missing.joinToString()}. " +
                "GitHub Secrets와 release 워크플로 환경 변수를 확인하세요."
        }
        val keyStore = file(signingEnvironment.getValue("ANDROID_KEYSTORE_FILE")!!)
        check(keyStore.isFile) { "release keystore 파일을 찾을 수 없습니다: $keyStore" }
    }
}

tasks.matching { it.name == "preReleaseBuild" }.configureEach {
    dependsOn(verifyReleaseSigning)
}

dependencies {
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("androidx.datastore:datastore-preferences:1.1.7")
    implementation("androidx.work:work-runtime-ktx:2.10.1")
    implementation("com.squareup.retrofit2:retrofit:2.11.0")
    implementation("com.squareup.retrofit2:converter-gson:2.11.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
    androidTestImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    testImplementation(libs.junit)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
    debugImplementation(libs.androidx.compose.ui.tooling)
}
